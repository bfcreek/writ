using Writ.Compiler.Diagnostics;

namespace Writ.Compiler.Syntax;

/// <summary>
/// Parses English-like Writ into a structural AST.
/// Sentences become declarations, assignments, calls, and operators — not an open prompt.
/// </summary>
public sealed partial class Parser
{
    private readonly record struct EnglishName(string Camel, string Raw, SourceSpan Span, int WordCount);

    private readonly List<Token> _tokens;
    private readonly string? _filePath;
    private readonly DiagnosticBag _diagnostics;
    private int _current;
    private int _paragraph;

    public Parser(List<Token> tokens, string? filePath, DiagnosticBag diagnostics)
    {
        _tokens = tokens;
        _filePath = filePath;
        _diagnostics = diagnostics;
    }

    public ProgramAst ParseProgram()
    {
        SkipNewLines();
        var declarations = new List<AstNode>();
        var script = new List<StmtAst>();
        var sawEntry = false;
        var start = Peek().Span;

        while (!IsAtEnd())
        {
            SkipNewLines();
            if (IsAtEnd())
            {
                break;
            }

            if (Check(TokenKind.An) || Check(TokenKind.A))
            {
                declarations.Add(ParseTypeDecl());
            }
            else if (Check(TokenKind.Use))
            {
                declarations.Add(ParseUse());
            }
            else if (Check(TokenKind.Allow))
            {
                declarations.Add(ParseAllow());
            }
            else if (Check(TokenKind.To))
            {
                declarations.Add(ParseFunction());
            }
            else if (Check(TokenKind.When) && IsAtWord(_current + 1, "run"))
            {
                var entry = ParseEntry();
                if (sawEntry || script.Count > 0)
                {
                    _diagnostics.Error("E1109",
                        "The sentences in this file are already what it does when it runs. Remove the other 'When run' block.",
                        _filePath, entry.Span);
                }
                else
                {
                    sawEntry = true;
                    declarations.Add(entry);
                }
            }
            else if (IsAtWord(_current, "also") && CheckNext(TokenKind.Use))
            {
                declarations.Add(ParseInclude());
            }
            else if (IsAtWord(_current, "using"))
            {
                declarations.Add(ParseUsingDecl());
            }
            else if (IsAtWord(_current, "export"))
            {
                declarations.Add(ParseExportDecl());
            }
            else if (Check(TokenKind.Indent) || Check(TokenKind.Dedent))
            {
                var bad = Advance();
                _diagnostics.Error("E1101",
                    $"Expected a sentence, 'An ... has', 'Use ... from', 'Also use', 'Allow', or 'To ...', found '{bad.Text}'.",
                    _filePath, bad.Span);
            }
            else
            {
                var statement = ParseStatement();
                if (sawEntry)
                {
                    _diagnostics.Error("E1109",
                        "The sentences in this file are already what it does when it runs. Remove the other 'When run' block.",
                        _filePath, statement.Span);
                }
                else
                {
                    script.Add(statement);
                }
            }

            SkipNewLines();
            Match(TokenKind.Dot);
            SkipNewLines();
        }

        if (script.Count > 0)
        {
            var span = script.Count == 1 ? script[0].Span : Combine(script[0].Span, script[^1].Span);
            declarations.Add(new FunctionAst(
                "Main",
                [],
                new NamedTypeAst("nothing", span),
                script,
                span,
                IsEntry: true));
        }

        var end = Previous().Span;
        return new ProgramAst(declarations, Combine(start, end));
    }

    private TypeDeclAst ParseTypeDecl()
    {
        var start = Advance().Span; // An / A
        if (IsAtWord(_current, "enumeration", "enum"))
        {
            return ParseEnum(start);
        }

        var name = ParseName();
        Consume(TokenKind.Has, "Expected 'has' after the type name.");

        var fields = new List<FieldDeclAst>();
        var methods = new List<FunctionAst>();
        SourceSpan end;
        if (Check(TokenKind.NewLine))
        {
            Advance();
            Consume(TokenKind.Indent, "Expected an indented list of fields.");
            while (!Check(TokenKind.Dedent) && !IsAtEnd())
            {
                SkipNewLines();
                if (Check(TokenKind.Dedent) || IsAtEnd())
                {
                    break;
                }

                var mark = _current;
                Match(TokenKind.And);
                if (Check(TokenKind.To))
                {
                    methods.Add(ParseFunction());
                    if (_current == mark)
                    {
                        Advance();
                    }

                    continue;
                }

                fields.Add(ParseField());
                Match(TokenKind.NewLine);
                if (_current == mark)
                {
                    var stuck = Peek();
                    _diagnostics.Error("E1106", $"Could not read a field starting at '{stuck.Text}'.", _filePath, stuck.Span);
                    Advance();
                }
            }

            end = Consume(TokenKind.Dedent, "Expected the end of the field list.").Span;
        }
        else
        {
            fields.Add(ParseField());
            while (Match(TokenKind.And) || Match(TokenKind.Comma))
            {
                Match(TokenKind.And); // Oxford comma: "a, b, and c"
                var mark = _current;
                fields.Add(ParseField());
                if (_current == mark)
                {
                    var stuck = Peek();
                    _diagnostics.Error("E1106", $"Could not read a field starting at '{stuck.Text}'.", _filePath, stuck.Span);
                    Advance();
                    break;
                }
            }

            end = fields.Count > 0 ? fields[^1].Span : name.Span;
            Match(TokenKind.NewLine);
        }

        return new TypeDeclAst(name.Camel, fields, Combine(start, end))
        {
            Methods = methods
        };
    }

    private FieldDeclAst ParseField()
    {
        var fieldName = ParseName();
        if (!Match(TokenKind.Which))
        {
            return new FieldDeclAst(fieldName.Camel, null, fieldName.Span);
        }

        if (!Match(TokenKind.Is, TokenKind.Are))
        {
            _diagnostics.Error("E1104", "Expected 'is' or 'are' after 'which'.", _filePath, Peek().Span);
        }

        var fieldType = ParseType();
        return new FieldDeclAst(fieldName.Camel, fieldType, Combine(fieldName.Span, fieldType.Span));
    }

    private AllowDeclAst ParseAllow()
    {
        var start = Consume(TokenKind.Allow, "Expected 'Allow'.").Span;
        var phrases = new List<string>();
        var words = new List<string>();
        var end = start;
        while (!Check(TokenKind.NewLine) && !IsAtEnd())
        {
            var token = Advance();
            end = token.Span;
            if (token.Kind is TokenKind.Comma or TokenKind.And)
            {
                if (words.Count > 0)
                {
                    phrases.Add(string.Join(' ', words));
                    words.Clear();
                }

                continue;
            }

            words.Add(token.Text);
        }

        if (words.Count > 0)
        {
            phrases.Add(string.Join(' ', words));
        }

        Match(TokenKind.NewLine);
        return new AllowDeclAst(phrases, Combine(start, end));
    }

    private UseDeclAst ParseUse()
    {
        var start = Consume(TokenKind.Use, "Expected 'Use'.").Span;
        var alias = ParseName();
        Consume(TokenKind.From, "Expected 'from' and a manifest path.");
        var kind = "file";
        if (Check(TokenKind.Identifier))
        {
            var marker = Peek().Text.ToLowerInvariant();
            if (marker is "mcp" or "sqlite" or "http")
            {
                Advance();
                kind = marker;
            }
        }

        var path = Consume(TokenKind.StringLiteral, kind switch
        {
            "mcp" => "Expected the MCP server command line in quotes.",
            "sqlite" => "Expected the SQLite database path in quotes.",
            "http" => "Expected the OpenAPI document path or base URL in quotes.",
            _ => "Expected the manifest path in quotes."
        });
        Match(TokenKind.NewLine);
        return new UseDeclAst(alias.Camel, path.Literal as string ?? path.Text, Combine(start, path.Span), kind);
    }

    private IncludeDeclAst ParseInclude()
    {
        var start = Advance().Span; // also
        Consume(TokenKind.Use, "Expected 'use'.");
        var path = Consume(TokenKind.StringLiteral, "Expected the file name in quotes, such as Also use \"helpers.writ\".");
        Match(TokenKind.NewLine);
        return new IncludeDeclAst(path.Literal as string ?? path.Text, Combine(start, path.Span));
    }

    /// <summary>
    /// When run, given a folder which is a string, returning a string
    ///     …
    /// The script's parameters and result. Without them, the sentences in the file are the script.
    /// A single sentence may sit on the same line: When run, say "hello".
    /// </summary>
    private FunctionAst ParseEntry()
    {
        var start = Consume(TokenKind.When, "Expected 'When'.").Span;
        Advance(); // run
        Match(TokenKind.Comma);
        var parameters = Check(TokenKind.Given) ? ParseParameterList() : [];
        Match(TokenKind.Comma);
        TypeAst returnType = new NamedTypeAst("nothing", start);
        if (Match(TokenKind.Returning))
        {
            returnType = ParseType();
        }

        IReadOnlyList<StmtAst> body;
        if (Check(TokenKind.NewLine) || IsAtEnd())
        {
            Consume(TokenKind.NewLine, "Expected a newline after 'When run …'.");
            body = ParseBlock();
        }
        else
        {
            body = [ParseStatement()];
        }

        var end = body.Count > 0 ? body[^1].Span : start;
        return new FunctionAst("Main", parameters, returnType, body, Combine(start, end), IsEntry: true);
    }

    private FunctionAst ParseFunction()
    {
        var start = Consume(TokenKind.To, "Expected 'To'.").Span;
        var filter = false;
        var command = false;
        if (IsAtWord(_current, "filter"))
        {
            Advance();
            filter = true;
        }
        else if (IsAtWord(_current, "command"))
        {
            Advance();
            command = true;
        }

        var name = ParseName();
        Match(TokenKind.Comma);

        var parameters = ParseParameterList();

        Match(TokenKind.Comma);
        Consume(TokenKind.Returning, "Expected 'returning' and a result type.");
        var returnType = ParseType();
        Consume(TokenKind.NewLine, "Expected a newline after the description.");
        var body = ParseBlock();
        var end = body.Count > 0 ? body[^1].Span : returnType.Span;
        return new FunctionAst(name.Camel, parameters, returnType, body, Combine(start, end))
        {
            IsFilter = filter,
            CmdletBinding = command
        };
    }

    private List<ParameterAst> ParseParameterList()
    {
        var parameters = new List<ParameterAst>();
        if (Match(TokenKind.Given))
        {
            do
            {
                Match(TokenKind.And);
                var paramName = ParseName();
                Consume(TokenKind.Which, "Expected 'which' after a parameter name.");
                if (!Match(TokenKind.Is, TokenKind.Are))
                {
                    _diagnostics.Error("E1104", "Expected 'is' or 'are' in the parameter description.", _filePath, Peek().Span);
                }

                var paramType = ParseType();
                var parameter = new ParameterAst(paramName.Camel, paramType, Combine(paramName.Span, paramType.Span));
                parameter = ParseParameterTail(parameter);
                parameters.Add(parameter);
                var mark = _current;
                var more = Match(TokenKind.And) || (Check(TokenKind.Comma) && !CheckNext(TokenKind.Returning) && Match(TokenKind.Comma));
                if (!more)
                {
                    break;
                }

                if (_current == mark)
                {
                    var stuck = Peek();
                    _diagnostics.Error("E1106", $"Could not read a parameter starting at '{stuck.Text}'.", _filePath, stuck.Span);
                    Advance();
                }
            }
            while (true);
        }

        return parameters;
    }

    private IReadOnlyList<StmtAst> ParseBlock()
    {
        Consume(TokenKind.Indent, "Expected an indented block.");
        var statements = new List<StmtAst>();
        while (!Check(TokenKind.Dedent) && !IsAtEnd())
        {
            SkipNewLines();
            Match(TokenKind.Dot);
            SkipNewLines();
            if (Check(TokenKind.Dedent) || IsAtEnd())
            {
                break;
            }

            var mark = _current;
            statements.Add(ParseStatement());
            SkipNewLines();
            Match(TokenKind.Dot);
            SkipNewLines();
            if (_current == mark)
            {
                var stuck = Peek();
                _diagnostics.Error("E1106", $"Could not read a statement starting at '{stuck.Text}'.", _filePath, stuck.Span);
                Advance();
            }
        }

        Consume(TokenKind.Dedent, "Expected the end of the block.");
        return statements;
    }

    private StmtAst ParseStatement()
    {
        if (Check(TokenKind.If))
        {
            return ParseIf();
        }

        if (Check(TokenKind.For))
        {
            return CheckNext(TokenKind.Each) ? ParseForEach() : ParseCountedFor();
        }

        if (Check(TokenKind.Return) || Check(TokenKind.Give))
        {
            return ParseReturn();
        }

        if (Check(TokenKind.Ensure))
        {
            return ParseEnsure();
        }

        if (Check(TokenKind.Say) || IsConsoleVerb())
        {
            return ParseSay();
        }

        if (TryParseProse() is { } prose)
        {
            return prose;
        }

        if (TryParseHostStatement() is { } host)
        {
            return host;
        }

        if (Check(TokenKind.While) || Check(TokenKind.Until))
        {
            return ParseWhile();
        }

        if (Check(TokenKind.Try))
        {
            return ParseTry();
        }

        if (TryParseConstruct() is { } construct)
        {
            return construct;
        }

        if (!LineHasAssignment())
        {
            var phrase = Check(TokenKind.Ask) ? ParseExpression() : ParseOpenPhrase();
            Match(TokenKind.NewLine);
            return new ExpressionStmtAst(phrase, phrase.Span);
        }

        return ParseAssign();
    }

    private bool LineHasAssignment()
    {
        for (var i = _current; i < _tokens.Count; i++)
        {
            var kind = _tokens[i].Kind;
            if (kind is TokenKind.NewLine or TokenKind.EndOfFile or TokenKind.Indent or TokenKind.Dedent)
            {
                return false;
            }

            if (kind is TokenKind.Is or TokenKind.Are or TokenKind.Be)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>"say", "display", and "print" all write a value. The other two are ordinary words everywhere else.</summary>
    private bool IsConsoleVerb() => IsAtWord(_current, "display", "print");

    /// <summary>
    /// Sentences recognized directly. Each family takes the verb, noun, and unit variants
    /// implemented below, and those words stay ordinary in every other position.
    /// A sentence that does not fit exactly is left for the command catalog.
    /// </summary>
    private StmtAst? TryParseHostStatement()
    {
        var mark = _current;
        ExprAst? expr = ParseClear()
            ?? ParseWait()
            ?? ParseStopWith()
            ?? ParseHostMessage()
            ?? ParseGo()
            ?? (IsReadPrompt() ? ParseReadPrompt() : null)
            ?? (IsPromptFor() ? ParsePrompt() : null)
            ?? ParseTransfer()
            ?? ParseRemove()
            ?? ParseCreate()
            ?? ParseControl()
            ?? ParsePathStatement();
        if (expr is null || (!Check(TokenKind.NewLine) && !IsAtEnd() && !Check(TokenKind.Dedent)))
        {
            _current = mark;
            return null;
        }

        return FinishHost(expr);
    }

    private StmtAst FinishHost(ExprAst expr)
    {
        if (!Check(TokenKind.NewLine) && !IsAtEnd() && !Check(TokenKind.Dedent))
        {
            _diagnostics.Error("E1104", "Expected the end of the line.", _filePath, Peek().Span);
            while (!IsAtEnd() && !Check(TokenKind.NewLine) && !Check(TokenKind.Dedent))
            {
                Advance();
            }
        }

        Match(TokenKind.NewLine);
        return new ExpressionStmtAst(expr, expr.Span);
    }

    private bool IsReadPrompt() =>
        WordAt(_current, "read") && IsAt(_current + 1, TokenKind.StringLiteral);

    private bool IsUserPrompt()
    {
        if (!Check(TokenKind.Ask))
        {
            return false;
        }

        if (WordAt(_current + 1, "for"))
        {
            return true;
        }

        if (WordAt(_current + 1, "the") && IsAudience(_current + 2) && WordAt(_current + 3, "for"))
        {
            return true;
        }

        return IsAudience(_current + 1) && WordAt(_current + 2, "for");
    }

    private bool IsPromptFor()
    {
        if (!WordAt(_current, "prompt") || WordAt(_current + 1, "is"))
        {
            return false;
        }

        if (WordAt(_current + 1, "for"))
        {
            return true;
        }

        return WordAt(_current + 1, "the") && IsAudience(_current + 2) && WordAt(_current + 3, "for");
    }

    private bool IsAudience(int index) =>
        WordAt(index, "user", "person", "someone", "operator", "human", "reader");

    private ExprAst ParseReadPrompt()
    {
        var start = Advance().Span;
        var prompt = ParseUnary();
        return Host("read", start, prompt.Span, prompt);
    }

    private ExprAst ParseUserPrompt()
    {
        var start = Advance().Span;
        if (WordAt(_current, "the"))
        {
            Advance();
        }

        if (IsAudience(_current))
        {
            Advance();
        }

        if (WordAt(_current, "for"))
        {
            Advance();
        }

        var prompt = ParseMessage(start);
        return Host("read", start, prompt.Span, prompt);
    }

    private ExprAst ParsePrompt()
    {
        var start = Advance().Span;
        if (WordAt(_current, "the"))
        {
            Advance();
        }

        if (IsAudience(_current))
        {
            Advance();
        }

        if (WordAt(_current, "for"))
        {
            Advance();
        }

        var prompt = ParseMessage(start);
        return Host("read", start, prompt.Span, prompt);
    }

    /// <summary>A quoted value, a name, or the rest of the line kept as text.</summary>
    private ExprAst ParseMessage(SourceSpan start)
    {
        var simple = IsAt(_current, TokenKind.StringLiteral)
            || IsAt(_current, TokenKind.LeftParen)
            || IsAt(_current, TokenKind.IntegerLiteral)
            || (IsAt(_current, TokenKind.The) && StartsName(_current + 1) && IsLineEnd(_current + 2));
        if (simple)
        {
            return ParseUnary();
        }

        var words = new List<string>();
        var end = start;
        while (!IsAtEnd() && !Check(TokenKind.NewLine) && !Check(TokenKind.Dedent))
        {
            var token = Advance();
            words.Add(token.Text);
            end = token.Span;
        }

        if (words.Count == 0)
        {
            _diagnostics.Error("E1104", "Expected a message.", _filePath, Peek().Span);
            return new LiteralExprAst("", start);
        }

        return new LiteralExprAst(string.Join(' ', words), Combine(start, end));
    }

    private bool IsLineEnd(int index) =>
        index >= _tokens.Count
        || _tokens[index].Kind is TokenKind.NewLine or TokenKind.EndOfFile or TokenKind.Dedent or TokenKind.Indent;

    private static readonly string[][] ParentHeads =
    [
        ["containing", "folder", "of"],
        ["containing", "directory", "of"],
        ["parent", "folder", "of"],
        ["parent", "directory", "of"],
        ["parent", "dir", "of"]
    ];

    private static readonly string[][] LeafHeads =
    [
        ["file", "name", "of"],
        ["filename", "of"],
        ["base", "name", "of"],
        ["leaf", "name", "of"],
        ["leaf", "of"]
    ];

    private static readonly string[][] ExtensionHeads =
    [
        ["extension", "of"],
        ["file", "extension", "of"],
        ["suffix", "of"]
    ];

    private static readonly string[][] StemHeads =
    [
        ["name", "without", "extension", "of"],
        ["name", "without", "the", "extension", "of"],
        ["file", "name", "without", "extension", "of"],
        ["file", "name", "without", "the", "extension", "of"],
        ["bare", "name", "of"],
        ["stem", "of"]
    ];

    private static readonly string[][] ResolveHeads =
    [
        ["full", "path", "of"],
        ["absolute", "path", "of"],
        ["resolved", "path", "of"],
        ["complete", "path", "of"]
    ];

    private static readonly string[][] JoinHeads =
    [
        ["combined", "path", "of"],
        ["joined", "path", "of"],
        ["path", "joining"],
        ["join", "of"]
    ];

    private ExprAst? ParseClear()
    {
        if (WordAt(_current, "cls") && IsLineEnd(_current + 1))
        {
            var token = Advance();
            return Host("clear", token.Span, token.Span);
        }

        if (!WordAt(_current, "clear", "wipe", "erase", "blank", "cls"))
        {
            return null;
        }

        var nounAt = WordAt(_current + 1, "the") ? _current + 2 : _current + 1;
        if (!WordAt(nounAt, "screen", "console", "window", "display", "terminal", "host") || !IsLineEnd(nounAt + 1))
        {
            return null;
        }

        var start = Advance().Span;
        if (WordAt(_current, "the"))
        {
            Advance();
        }

        var end = Advance().Span;
        return Host("clear", start, end);
    }

    private ExprAst? ParseWait()
    {
        if (!WordAt(_current, "wait", "pause", "sleep", "hold", "delay") || WordAt(_current + 1, "is"))
        {
            return null;
        }

        var index = _current + 1;
        if (WordAt(index, "for"))
        {
            index++;
        }

        if (IsAt(index, TokenKind.IntegerLiteral))
        {
            index++;
        }
        else if (IsAt(index, TokenKind.The) && StartsName(index + 1))
        {
            index += 2;
        }
        else
        {
            return null;
        }

        if (IsDuration(index))
        {
            index++;
        }

        if (!IsLineEnd(index))
        {
            return null;
        }

        var start = Advance().Span;
        if (WordAt(_current, "for"))
        {
            Advance();
        }

        var amount = ParseUnary();
        var kind = "sleep";
        if (IsDuration(_current))
        {
            kind = SleepKind(Peek().Text);
            Advance();
        }

        return Host(kind, start, Previous().Span, amount);
    }

    private ExprAst? ParseStopWith()
    {
        if (!WordAt(_current, "stop", "halt", "fail", "abort", "quit", "exit", "die", "end") || !WordAt(_current + 1, "with"))
        {
            return null;
        }

        var start = Advance().Span;
        Advance();
        var message = ParseMessage(start);
        return Host("stop", start, message.Span, message);
    }

    private ExprAst? ParseHostMessage()
    {
        if (!WordAt(_current, "warn", "caution", "alert", "note", "mention", "verbose") || WordAt(_current + 1, "is"))
        {
            return null;
        }

        var start = Advance();
        var kind = start.Text.ToLowerInvariant() is "note" or "mention" or "verbose" ? "note" : "warn";
        var message = ParseMessage(start.Span);
        return Host(kind, start.Span, message.Span, message);
    }

    private ExprAst? ParseGo()
    {
        var enter = WordAt(_current, "enter") && !WordAt(_current + 1, "is");
        var directed = WordAt(_current, "go", "change", "switch", "cd", "head", "move", "relocate") && WordAt(_current + 1, "to");
        if (!enter && !directed)
        {
            return null;
        }

        var start = Advance().Span;
        if (directed)
        {
            Advance();
        }

        var path = ParseUnary();
        return Host("go", start, path.Span, path);
    }

    private ExprAst? ParseTransfer()
    {
        string? kind = null;
        var links = new[] { "to", "onto", "into" };
        var pathFirst = false;
        if (WordAt(_current, "add", "append", "tack"))
        {
            kind = "add";
        }
        else if (WordAt(_current, "write", "save", "overwrite"))
        {
            kind = "write";
            if (SeesAhead("with") && !SeesAhead("to") && !SeesAhead("onto") && !SeesAhead("into"))
            {
                links = ["with"];
                pathFirst = true;
            }
        }
        else if (WordAt(_current, "copy", "duplicate"))
        {
            kind = "copy";
        }
        else if (WordAt(_current, "move", "relocate"))
        {
            if (WordAt(_current + 1, "to"))
            {
                return null;
            }

            kind = "move";
        }
        else if (WordAt(_current, "rename", "retitle"))
        {
            kind = "rename";
            links = ["to", "as"];
        }

        if (kind is null || !SeesAhead(links))
        {
            return null;
        }

        var mark = _current;
        var start = Advance().Span;
        var first = ParseOperand();
        if (!WordAt(_current, links))
        {
            _current = mark;
            return null;
        }

        Advance();
        var second = ParseOperand();
        if (kind == "copy" && second is NameExprAst { Name: "clipboard" })
        {
            // "copy the text to the clipboard" is Set-Clipboard, read from the catalog, not Copy-Item.
            _current = mark;
            return null;
        }

        var value = pathFirst ? second : first;
        var path = pathFirst ? first : second;
        return Host(kind, start, second.Span, value, path);
    }

    private ExprAst? ParseRemove()
    {
        if (!WordAt(_current, "remove", "delete", "erase", "wipe"))
        {
            return null;
        }

        var start = Advance().Span;
        var path = ParseUnary();
        var recurse = false;
        if (WordAt(_current, "recursively"))
        {
            Advance();
            recurse = true;
        }
        else if (WordAt(_current, "and") && WordAt(_current + 1, "everything") && WordAt(_current + 2, "in") && WordAt(_current + 3, "it"))
        {
            Advance();
            Advance();
            Advance();
            Advance();
            recurse = true;
        }
        else if (WordAt(_current, "and") && WordAt(_current + 1, "below"))
        {
            Advance();
            Advance();
            recurse = true;
        }

        return Host(recurse ? "removeAll" : "remove", start, Previous().Span, path);
    }

    private ExprAst? ParseCreate()
    {
        if (!WordAt(_current, "create", "make"))
        {
            return null;
        }

        var nounAt = WordAt(_current + 1, "a", "an", "the") ? _current + 2 : _current + 1;
        if (!WordAt(nounAt, "folder", "directory", "dir", "file"))
        {
            return null;
        }

        var start = Advance().Span;
        if (WordAt(_current, "a", "an", "the"))
        {
            Advance();
        }

        var noun = Advance();
        var file = noun.Text.Equals("file", StringComparison.OrdinalIgnoreCase);
        ExprAst path;
        if (Check(TokenKind.NewLine) || IsAtEnd() || Check(TokenKind.Dedent))
        {
            path = new NameExprAst(noun.Text.ToLowerInvariant(), noun.Span);
        }
        else
        {
            path = ParseUnary();
        }

        return Host(file ? "createFile" : "createDir", start, path.Span, path);
    }

    private ExprAst? ParseControl()
    {
        if (!WordAt(_current, "start", "launch", "open", "run", "stop", "kill", "end", "halt", "abort", "restart"))
        {
            return null;
        }

        if (WordAt(_current, "run") && (IsAt(_current + 1, TokenKind.StringLiteral)
            || WordAt(_current + 1, "script")
            || (WordAt(_current + 1, "the") && WordAt(_current + 2, "script"))))
        {
            return null;
        }

        if (WordAt(_current + 1, "with"))
        {
            return null;
        }

        var nounAt = WordAt(_current + 1, "the", "a", "an") ? _current + 2 : _current + 1;
        if (_tokens.Count <= nounAt || !StartsName(nounAt))
        {
            return null;
        }

        var kind = ControlKind(Peek().Text, _tokens[nounAt].Text);
        if (kind is null)
        {
            return null;
        }

        var start = Advance().Span;
        if (WordAt(_current, "the", "a", "an"))
        {
            Advance();
        }

        var noun = Advance();
        ExprAst value;
        if (Check(TokenKind.NewLine) || IsAtEnd() || Check(TokenKind.Dedent))
        {
            value = new NameExprAst(noun.Text.ToLowerInvariant(), noun.Span);
        }
        else
        {
            value = ParseUnary();
        }

        return Host(kind, start, value.Span, value);
    }

    private ExprAst? ParsePathStatement()
    {
        if (LineHasAssignment() || !PathHeadAhead())
        {
            return null;
        }

        return ParseExpression();
    }

    private ExprAst? TryParsePathCommand(SourceSpan start)
    {
        if (TakeHead(ParentHeads))
        {
            var path = ParseUnary();
            return Host("parent", start, path.Span, path);
        }

        if (TakeHead(StemHeads))
        {
            var path = ParseUnary();
            return Host("stem", start, path.Span, path);
        }

        if (TakeHead(LeafHeads))
        {
            var path = ParseUnary();
            return Host("leaf", start, path.Span, path);
        }

        if (TakeHead(ExtensionHeads))
        {
            var path = ParseUnary();
            return Host("extension", start, path.Span, path);
        }

        if (TakeHead(ResolveHeads))
        {
            var path = ParseUnary();
            return Host("resolve", start, path.Span, path);
        }

        if (TakeHead(JoinHeads))
        {
            var folder = ParseUnary();
            if (!WordAt(_current, "and"))
            {
                _diagnostics.Error("E1104", "Expected 'and', as in the combined path of the folder and the name.", _filePath, Peek().Span);
                return new LiteralExprAst("", Combine(start, folder.Span));
            }

            Advance();
            var child = ParseUnary();
            return Host("join", start, child.Span, folder, child);
        }

        return null;
    }

    private bool TakeHead(string[][] heads)
    {
        foreach (var head in heads)
        {
            if (!HeadAt(_current, head))
            {
                continue;
            }

            foreach (var _ in head)
            {
                Advance();
            }

            return true;
        }

        return false;
    }

    private bool PathHeadAhead()
    {
        var index = WordAt(_current, "the", "a", "an") ? _current + 1 : _current;
        return AnyHead(index, ParentHeads) || AnyHead(index, LeafHeads) || AnyHead(index, StemHeads) || AnyHead(index, ExtensionHeads)
            || AnyHead(index, ResolveHeads) || AnyHead(index, JoinHeads);
    }

    private bool AnyHead(int index, string[][] heads)
    {
        foreach (var head in heads)
        {
            if (HeadAt(index, head))
            {
                return true;
            }
        }

        return false;
    }

    private bool HeadAt(int index, string[] head)
    {
        for (var n = 0; n < head.Length; n++)
        {
            if (!WordAt(index + n, head[n]))
            {
                return false;
            }
        }

        return true;
    }

    private bool SeesAhead(params string[] words)
    {
        for (var i = _current + 1; i < _tokens.Count && !IsLineEnd(i); i++)
        {
            if (WordAt(i, words))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsDuration(int index) =>
        WordAt(index, "second", "seconds", "sec", "secs", "minute", "minutes", "min", "mins", "hour", "hours", "hr", "hrs", "millisecond", "milliseconds", "ms", "msec");

    private static string SleepKind(string unit) => unit.ToLowerInvariant() switch
    {
        "minute" or "minutes" or "min" or "mins" => "sleepmin",
        "hour" or "hours" or "hr" or "hrs" => "sleephour",
        "millisecond" or "milliseconds" or "ms" or "msec" => "sleepms",
        _ => "sleep"
    };

    private static string? ControlKind(string verb, string noun)
    {
        verb = verb.ToLowerInvariant();
        noun = noun.ToLowerInvariant();
        var starting = verb is "start" or "launch" or "open" or "run";
        var stopping = verb is "stop" or "kill" or "end" or "halt" or "abort";
        var program = noun is "program" or "app" or "application" or "executable" or "tool" or "command" or "exe" or "binary";
        var service = noun is "service" or "daemon";
        var process = noun is "process";
        if (program && starting)
        {
            return "startProgram";
        }

        if (service && starting)
        {
            return "startService";
        }

        if (service && stopping)
        {
            return "stopService";
        }

        if (service && verb == "restart")
        {
            return "restartService";
        }

        if (process && stopping)
        {
            return "stopProcess";
        }

        if (process && starting)
        {
            return "startProgram";
        }

        return null;
    }

    /// <summary>One value in a file sentence. Link words such as "onto" stay outside the name.</summary>
    private ExprAst ParseOperand()
    {
        if (IsAt(_current, TokenKind.StringLiteral) || IsAt(_current, TokenKind.LeftParen) || IsAt(_current, TokenKind.IntegerLiteral))
        {
            return ParseUnary();
        }

        if (WordAt(_current, "the", "a", "an") && StartsName(_current + 1) && !IsLink(_current + 1))
        {
            var start = Advance().Span;
            var name = Advance();
            return new NameExprAst(name.Text, Combine(start, name.Span));
        }

        if (StartsName(_current) && !IsLink(_current))
        {
            var name = Advance();
            return new NameExprAst(name.Text, name.Span);
        }

        return ParseUnary();
    }

    private bool IsLink(int index) =>
        WordAt(index, "to", "onto", "into", "as", "with");

    private ExprAst Host(string name, SourceSpan start, SourceSpan end, params ExprAst[] args) =>
        new CallExprAst("writ.host." + name, args, Combine(start, end));

    /// <summary>The tokens from <paramref name="from"/> up to the current one, as the sentence was written.</summary>
    private string RawText(int from)
    {
        var parts = new List<string>();
        for (var i = from; i < _current && i < _tokens.Count; i++)
        {
            var token = _tokens[i];
            parts.Add(token.Kind == TokenKind.StringLiteral ? $"\"{token.Text}\"" : token.Text);
        }

        return string.Join(' ', parts);
    }

    private bool WordAt(int index, params string[] words)
    {
        if ((uint)index >= (uint)_tokens.Count)
        {
            return false;
        }

        var text = _tokens[index].Text;
        foreach (var word in words)
        {
            if (text.Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private SayStmtAst ParseSay()
    {
        var start = Peek().Span;
        if (!Check(TokenKind.Say) && !IsConsoleVerb())
        {
            _diagnostics.Error("E1104", "Expected 'say', 'display', or 'print'.", _filePath, start);
        }
        else
        {
            Advance();
        }
        var parts = new List<ExprAst> { ParseComparison() };
        while (Match(TokenKind.And))
        {
            parts.Add(ParseComparison());
        }

        Match(TokenKind.NewLine);
        return new SayStmtAst(parts, Combine(start, parts[^1].Span));
    }

    private EnsureStmtAst ParseEnsure()
    {
        var start = Consume(TokenKind.Ensure, "Expected 'ensure'.").Span;
        var condition = ParseExpression();
        Match(TokenKind.NewLine);
        return new EnsureStmtAst(condition, Combine(start, condition.Span));
    }

    private WhileStmtAst ParseWhile()
    {
        var keyword = Advance();
        var condition = ParseExpression();
        var body = ParseLoopBody("loop");
        var end = body.Count > 0 ? body[^1].Span : condition.Span;
        return new WhileStmtAst(condition, body, keyword.Kind == TokenKind.Until, Combine(keyword.Span, end));
    }

    private AssignStmtAst ParseAssign()
    {
        Match(TokenKind.Let);
        var start = Peek().Span;
        string? scope = null;
        Match(TokenKind.The, TokenKind.A, TokenKind.An);
        if (IsAtWord(_current, "global", "local", "private")
            || (IsAtWord(_current, "script") && !IsAtWord(_current + 1, "folder", "directory")))
        {
            scope = Advance().Text.ToLowerInvariant();
        }

        var words = ReadWords();
        if (words is null)
        {
            var bad = Peek();
            _diagnostics.Error("E1104", $"Expected a name, found '{bad.Text}'.", _filePath, bad.Span);
            words = new EnglishName("_error", "_error", bad.Span, 1);
        }

        var name = words.Value;
        if (!Match(TokenKind.Is, TokenKind.Are, TokenKind.Be))
        {
            _diagnostics.Error("E1104", "Expected 'is' to give this name a value.", _filePath, Peek().Span);
        }

        string? option = null;
        if (IsWord(_current, "fixed", "locked") && IsWord(_current + 1, "at"))
        {
            option = Advance().Text.Equals("locked", StringComparison.OrdinalIgnoreCase) ? "constant" : "readonly";
            Advance();
        }

        var value = ParseExpressionOrPhrase();
        Match(TokenKind.NewLine);
        return new AssignStmtAst(name.Camel, value, Combine(start, value.Span))
        {
            Scope = scope,
            Option = option
        };
    }

    private IfStmtAst ParseIf()
    {
        var start = Consume(TokenKind.If, "Expected 'if'.").Span;
        var condition = ParseExpression();
        IReadOnlyList<StmtAst> thenBody;
        if (Check(TokenKind.NewLine) && CheckNext(TokenKind.Indent))
        {
            Consume(TokenKind.NewLine, "Expected a newline after the condition.");
            thenBody = ParseBlock();
        }
        else if (Check(TokenKind.Comma) || _paragraph > 0)
        {
            thenBody = ParseParagraphClauses(stopAtOtherwise: true);
        }
        else
        {
            Consume(TokenKind.NewLine, "Expected a newline after the condition.");
            thenBody = ParseBlock();
        }

        var (otherwise, end) = ParseOtherwise(condition.Span);
        return new IfStmtAst(condition, thenBody, otherwise, Combine(start, end));
    }

    private (IReadOnlyList<StmtAst>? Body, SourceSpan End) ParseOtherwise(SourceSpan fallback)
    {
        SkipNewLines();
        if (Check(TokenKind.Comma) || (Check(TokenKind.And) && CheckNext(TokenKind.Otherwise)))
        {
            Advance();
            SkipNewLines();
        }

        if (!Check(TokenKind.Otherwise))
        {
            return (null, fallback);
        }

        var otherwiseTok = Advance();
        if (Check(TokenKind.When))
        {
            _tokens[_current] = _tokens[_current] with { Kind = TokenKind.If };
        }

        if (Check(TokenKind.If))
        {
            var nested = ParseIf();
            return ([nested], nested.Span);
        }

        if (Check(TokenKind.NewLine) && CheckNext(TokenKind.Indent))
        {
            Consume(TokenKind.NewLine, "Expected a newline after 'otherwise'.");
            var indented = ParseBlock();
            var indentedEnd = indented.Count > 0 ? indented[^1].Span : otherwiseTok.Span;
            return (indented, indentedEnd);
        }

        if (Check(TokenKind.Comma) || _paragraph > 0)
        {
            var inline = ParseParagraphClauses();
            var inlineEnd = inline.Count > 0 ? inline[^1].Span : otherwiseTok.Span;
            return (inline, inlineEnd);
        }

        Consume(TokenKind.NewLine, "Expected a newline after 'otherwise'.");
        var body = ParseBlock();
        var end = body.Count > 0 ? body[^1].Span : otherwiseTok.Span;
        return (body, end);
    }

    private StmtAst ParseForEach()
    {
        var start = Consume(TokenKind.For, "Expected 'for'.").Span;
        Consume(TokenKind.Each, "Expected 'each'.");
        var iterator = ParseName();
        if (Check(TokenKind.From))
        {
            Consume(TokenKind.From, "Expected 'from'.");
            var from = ParseExpression();
            Consume(TokenKind.To, "Expected 'to'.");
            var to = ParseExpression();
            var counted = ParseLoopBody("loop");
            var countedEnd = counted.Count > 0 ? counted[^1].Span : to.Span;
            return new ForStmtAst(iterator.Camel, from, to, counted, Combine(start, countedEnd));
        }

        Consume(TokenKind.In, "Expected 'in'.");
        var collection = ParseExpression();
        var body = ParseLoopBody("loop");
        var end = body.Count > 0 ? body[^1].Span : collection.Span;
        return new ForEachStmtAst(iterator.Camel, collection, body, Combine(start, end));
    }

    private ReturnStmtAst ParseReturn()
    {
        var start = Peek().Span;
        if (Match(TokenKind.Give))
        {
            Consume(TokenKind.Back, "Expected 'back' after 'give'.");
        }
        else
        {
            Consume(TokenKind.Return, "Expected 'return'.");
        }

        var value = ParseExpression();
        Match(TokenKind.NewLine);
        return new ReturnStmtAst(value, Combine(start, value.Span));
    }

    private TypeAst ParseType()
    {
        Match(TokenKind.A, TokenKind.An);
        if (Check(TokenKind.List))
        {
            var listTok = Advance();
            Consume(TokenKind.Of, "Expected 'of' after 'list'.");
            var element = ParseType();
            return new ListTypeAst(element, Combine(listTok.Span, element.Span));
        }

        if (Match(TokenKind.Integer))
        {
            return new NamedTypeAst("integer", Previous().Span);
        }

        if (Match(TokenKind.Decimal))
        {
            return new NamedTypeAst("decimal", Previous().Span);
        }

        if (Match(TokenKind.String))
        {
            return new NamedTypeAst("string", Previous().Span);
        }

        if (Match(TokenKind.Boolean))
        {
            return new NamedTypeAst("boolean", Previous().Span);
        }

        if (!StartsName(_current))
        {
            var bad = Peek();
            _diagnostics.Error("E1104", $"Expected a type name. Found '{bad.Text}'.", _filePath, bad.Span);
            return new NamedTypeAst(bad.Text, bad.Span);
        }

        var name = Advance();
        if (name.Text.Equals("dictionary", StringComparison.OrdinalIgnoreCase)
            || name.Text.Equals("grid", StringComparison.OrdinalIgnoreCase)
            || name.Text.Equals("reference", StringComparison.OrdinalIgnoreCase)
            || name.Text.Equals("ref", StringComparison.OrdinalIgnoreCase))
        {
            return FinishCompositeType(name);
        }

        return new NamedTypeAst(name.Text, name.Span);
    }

    private ExprAst ParseExpressionOrPhrase()
    {
        var start = _current;
        var expr = ParseExpression();
        if (Check(TokenKind.NewLine) || Check(TokenKind.Dedent) || IsAtEnd() || AtSentenceEnd())
        {
            return expr;
        }

        _current = start;
        return ParseOpenPhrase();
    }

    /// <summary>A period that closes the sentence, or a clause boundary inside a paragraph.</summary>
    private bool AtSentenceEnd()
    {
        if (Check(TokenKind.Dot) && (CheckNext(TokenKind.NewLine) || CheckNext(TokenKind.Dedent) || CheckNext(TokenKind.EndOfFile) || SentenceContinues()))
        {
            return true;
        }

        return _paragraph > 0 && AtClauseEnd();
    }

    private IntentExprAst ParseOpenPhrase()
    {
        var start = Peek().Span;
        var parts = new List<string>();
        var end = start;
        while (!IsAtEnd() && !Check(TokenKind.NewLine) && !Check(TokenKind.Dedent) && !AtSentenceEnd())
        {
            var tok = Advance();
            parts.Add(tok.Kind == TokenKind.StringLiteral ? $"\"{tok.Text}\"" : tok.Text);
            end = tok.Span;
        }

        var phrase = string.Join(' ', parts).Trim();
        if (phrase.Length == 0)
        {
            _diagnostics.Error("E1105", "Expected a description of the value.", _filePath, start);
        }

        return new IntentExprAst(phrase, Combine(start, end));
    }

    private ExprAst ParseExpression()
    {
        var expr = ParseOr();
        if (Match(TokenKind.Rounded))
        {
            Consume(TokenKind.To, "Expected 'to' after 'rounded'.");
            var digits = ParsePrimary();
            Match(TokenKind.Places);
            expr = new CallExprAst("round", [expr, digits], Combine(expr.Span, digits.Span));
        }

        if (Match(TokenKind.As))
        {
            Match(TokenKind.A, TokenKind.An);
            var cast = Peek();
            string? builtin = cast.Kind switch
            {
                TokenKind.String => "str",
                TokenKind.Integer => "int",
                TokenKind.Decimal => "decimal",
                TokenKind.Boolean => "bool",
                _ => null
            };

            if (builtin is not null)
            {
                Advance();
                expr = new CallExprAst(builtin, [expr], Combine(expr.Span, cast.Span));
            }
            else if (cast.Kind == TokenKind.Identifier &&
                     (cast.Text.Equals("json", StringComparison.OrdinalIgnoreCase) ||
                      cast.Text.Equals("csv", StringComparison.OrdinalIgnoreCase) ||
                      cast.Text.Equals("yaml", StringComparison.OrdinalIgnoreCase) ||
                      cast.Text.Equals("xml", StringComparison.OrdinalIgnoreCase)))
            {
                Advance();
                expr = new CallExprAst(cast.Text.ToLowerInvariant(), [expr], Combine(expr.Span, cast.Span));
            }
            else if (cast.Kind == TokenKind.Identifier && IsPrimitiveName(cast.Text))
            {
                Advance();
                expr = new ConstructExprAst("cast", [expr, new LiteralExprAst(cast.Text, cast.Span)], Combine(expr.Span, cast.Span));
            }
            else if (cast.Kind is TokenKind.List or TokenKind.Identifier)
            {
                _current--; // give the article back to ParseType
                if (Previous().Kind is not (TokenKind.A or TokenKind.An))
                {
                    _current++;
                }

                var type = ParseType();
                expr = new TreatExprAst(expr, type, Combine(expr.Span, type.Span));
            }
            else
            {
                _diagnostics.Error("E1104", "Expected a type after 'as', such as 'a string', 'JSON', or 'a list of Invoice'.",
                    _filePath, cast.Span);
            }
        }

        return ApplySoftCast(expr);
    }

    private ExprAst ParseOr()
    {
        var expr = ParseAnd();
        while (true)
        {
            if (Match(TokenKind.Or))
            {
                if (IsAtWord(_current, "else"))
                {
                    Advance();
                    var chained = ParseAnd();
                    expr = new ConstructExprAst("orElse", [expr, chained], Combine(expr.Span, chained.Span));
                    continue;
                }

                var right = ParseAnd();
                expr = new BinaryExprAst(expr, TokenKind.Or, right, Combine(expr.Span, right.Span));
                continue;
            }

            if (IsAtWord(_current, "xor"))
            {
                Advance();
                var right = ParseAnd();
                expr = new ConstructExprAst("xor", [expr, right], Combine(expr.Span, right.Span));
                continue;
            }

            if (Match(TokenKind.Otherwise))
            {
                var right = ParseAnd();
                expr = new ConstructExprAst("coalesce", [expr, right], Combine(expr.Span, right.Span));
                continue;
            }

            break;
        }

        return expr;
    }

    private ExprAst ParseAnd()
    {
        var expr = ParseComparison();
        while (Match(TokenKind.And))
        {
            if (IsAtWord(_current, "then"))
            {
                Advance();
                var chained = ParseComparison();
                expr = new ConstructExprAst("andThen", [expr, chained], Combine(expr.Span, chained.Span));
                continue;
            }

            var right = ParseComparison();
            expr = new BinaryExprAst(expr, TokenKind.And, right, Combine(expr.Span, right.Span));
        }

        return expr;
    }

    private ExprAst ParseComparison()
    {
        var expr = ParseSum();
        if (Match(TokenKind.Is))
        {
            if (TryParseIs(expr) is { } typed)
            {
                return ParseConstructInfix(typed);
            }

            var op = ParseEnglishComparison();
            var right = ParseSum();
            expr = new BinaryExprAst(expr, op, right, Combine(expr.Span, right.Span));
            return ParseConstructInfix(expr);
        }

        if (Match(TokenKind.EqualEqual, TokenKind.BangEqual, TokenKind.Less, TokenKind.LessEqual,
                TokenKind.Greater, TokenKind.GreaterEqual))
        {
            var op = Previous().Kind;
            var right = ParseSum();
            expr = new BinaryExprAst(expr, op, right, Combine(expr.Span, right.Span));
        }

        return ParseConstructInfix(expr);
    }

    private TokenKind ParseEnglishComparison()
    {
        if (Match(TokenKind.Not))
        {
            Consume(TokenKind.EqualWord, "Expected 'equal'.");
            Consume(TokenKind.To, "Expected 'to'.");
            return TokenKind.BangEqual;
        }

        if (Match(TokenKind.EqualWord))
        {
            Consume(TokenKind.To, "Expected 'to'.");
            return TokenKind.EqualEqual;
        }

        if (Match(TokenKind.Greater))
        {
            Consume(TokenKind.Than, "Expected 'than'.");
            return TokenKind.Greater;
        }

        if (Match(TokenKind.Less))
        {
            Consume(TokenKind.Than, "Expected 'than'.");
            return TokenKind.Less;
        }

        if (Match(TokenKind.At))
        {
            if (Match(TokenKind.Least))
            {
                return TokenKind.GreaterEqual;
            }

            if (Match(TokenKind.Most))
            {
                return TokenKind.LessEqual;
            }
        }

        // Bare "is 0" means equality.
        return TokenKind.EqualEqual;
    }

    private ExprAst ParseSum()
    {
        var expr = ParseProduct();
        while (Match(TokenKind.Plus, TokenKind.Minus))
        {
            var op = Previous().Kind;
            var right = ParseProduct();
            expr = new BinaryExprAst(expr, op, right, Combine(expr.Span, right.Span));
        }

        return expr;
    }

    private ExprAst ParseProduct()
    {
        var expr = ParseThrough();
        while (true)
        {
            if (Match(TokenKind.Star, TokenKind.Slash, TokenKind.Percent, TokenKind.Modulo))
            {
                var op = Previous().Kind == TokenKind.Modulo ? TokenKind.Percent : Previous().Kind;
                var right = ParseThrough();
                expr = new BinaryExprAst(expr, op, right, Combine(expr.Span, right.Span));
                continue;
            }

            if (Match(TokenKind.Divided))
            {
                Consume(TokenKind.By, "Expected 'by' after 'divided'.");
                var right = ParseThrough();
                expr = new BinaryExprAst(expr, TokenKind.Slash, right, Combine(expr.Span, right.Span));
                continue;
            }

            break;
        }

        return expr;
    }

    private ExprAst ParseUnary()
    {
        if (IsWord(_current, "bitwise") && IsWord(_current + 1, "not"))
        {
            var start = Advance().Span;
            Advance();
            var operand = ParseUnary();
            return new ConstructExprAst("bnot", [operand], Combine(start, operand.Span));
        }

        if (Match(TokenKind.Not, TokenKind.Minus))
        {
            var op = Previous();
            var operand = ParseUnary();
            return new UnaryExprAst(op.Kind, operand, Combine(op.Span, operand.Span));
        }

        return ParsePostfix(ParsePrimary());
    }

    private ExprAst ParsePrimary()
    {
        if (Check(TokenKind.Infer))
        {
            var start = Advance().Span;
            // "infer category from the message" or "infer "prompt""
            if (Check(TokenKind.StringLiteral))
            {
                var literal = Advance();
                return new InferExprAst(new LiteralExprAst(literal.Literal ?? literal.Text, literal.Span), Combine(start, literal.Span));
            }

            var prompt = ParseExpression();
            return new InferExprAst(prompt, Combine(start, prompt.Span));
        }

        if (Match(TokenKind.LeftParen))
        {
            var inner = ParseExpression();
            Consume(TokenKind.RightParen, "Expected ')'.");
            return inner;
        }

        if (Match(TokenKind.True, TokenKind.False, TokenKind.IntegerLiteral, TokenKind.DecimalLiteral, TokenKind.StringLiteral))
        {
            if (Previous().Kind == TokenKind.StringLiteral && Previous().Literal is string text && text.Contains('{') && text.Contains('}'))
            {
                return Interpolate(text, Previous().Span);
            }

            return FinishLiteral(new LiteralExprAst(Previous().Literal, Previous().Span));
        }

        if (Check(TokenKind.Ask))
        {
            return IsUserPrompt() ? ParseUserPrompt() : ParseAsk();
        }

        if (IsReadPrompt())
        {
            return ParseReadPrompt();
        }

        if (IsRunCall())
        {
            return ParseRun();
        }

        if (IsAtWord(_current, "nothing", "null"))
        {
            var token = Advance();
            return new LiteralExprAst(null, token.Span);
        }

        if (IsCallCommand())
        {
            return ParseCallCommand();
        }

        if (Check(TokenKind.The) || Check(TokenKind.A) || Check(TokenKind.An) || StartsName(_current))
        {
            return ParsePostfix(ParseNounPhrase());
        }

        var bad = Peek();
        _diagnostics.Error("E1103", $"Expected a value, found '{bad.Text}'.", _filePath, bad.Span);
        Advance();
        return new LiteralExprAst(0L, bad.Span);
    }

    /// <summary>"Processed {the count of the items} items" becomes string concatenation.</summary>
    private ExprAst Interpolate(string text, SourceSpan span)
    {
        ExprAst? result = null;
        var literal = new System.Text.StringBuilder();

        void Append(ExprAst part) =>
            result = result is null ? part : new BinaryExprAst(result, TokenKind.Plus, part, span);

        void Flush()
        {
            if (literal.Length > 0)
            {
                Append(new LiteralExprAst(literal.ToString(), span));
                literal.Clear();
            }
        }

        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '{')
            {
                var close = text.IndexOf('}', i);
                if (close < 0)
                {
                    literal.Append(text[i..]);
                    break;
                }

                Flush();
                var inner = text[(i + 1)..close];
                var tokens = new Lexer(inner, _filePath, _diagnostics).Tokenize();
                var sub = new Parser(tokens, _filePath, _diagnostics);
                var expr = sub.ParseExpression();
                Append(new CallExprAst("str", [expr], span));
                i = close + 1;
                continue;
            }

            literal.Append(text[i]);
            i++;
        }

        Flush();
        return result ?? new LiteralExprAst("", span);
    }

    /// <summary>run "file.ps1" / run the script "file.writ", optionally with arguments.</summary>
    private bool IsRunCall()
    {
        if (!IsAtWord(_current, "run"))
        {
            return false;
        }

        if (IsAt(_current + 1, TokenKind.StringLiteral))
        {
            return true;
        }

        if (IsAtWord(_current + 1, "script") && IsAt(_current + 2, TokenKind.StringLiteral))
        {
            return true;
        }

        return IsAt(_current + 1, TokenKind.The)
            && IsAtWord(_current + 2, "script")
            && IsAt(_current + 3, TokenKind.StringLiteral);
    }

    private RunExprAst ParseRun()
    {
        var start = Advance().Span; // run
        if (Check(TokenKind.The))
        {
            Advance();
        }

        if (IsAtWord(_current, "script"))
        {
            Advance();
        }

        var path = Consume(TokenKind.StringLiteral, "Expected the script path in quotes, such as run \"greet.ps1\".");
        var args = new List<AskArgAst>();
        var end = path.Span;
        if (Match(TokenKind.With))
        {
            args.Add(ParseAskArgument());
            while (Match(TokenKind.And))
            {
                args.Add(ParseAskArgument());
            }

            end = args[^1].Span;
        }

        return new RunExprAst(path.Literal as string ?? path.Text, args, Combine(start, end));
    }

    private AskExprAst ParseAsk()
    {
        var start = Consume(TokenKind.Ask, "Expected 'ask'.").Span;
        var capability = ParseName();
        Consume(TokenKind.To, "Expected 'to' after the capability name.");
        var words = new List<string>();
        var end = capability.Span;
        while (!IsAtEnd() && !Check(TokenKind.With) && !Check(TokenKind.NewLine) && !Check(TokenKind.Dedent)
               && !Check(TokenKind.Indent) && !Check(TokenKind.Comma))
        {
            var token = Advance();
            words.Add(token.Text);
            end = token.Span;
        }

        if (words.Count == 0)
        {
            _diagnostics.Error("E1104", "Expected an operation name after 'to'.", _filePath, Peek().Span);
        }

        var operation = string.Join('_', words);
        var args = new List<AskArgAst>();
        if (Match(TokenKind.With))
        {
            args.Add(ParseAskArgument());
            while (Match(TokenKind.And))
            {
                args.Add(ParseAskArgument());
            }

            end = args[^1].Span;
        }

        var retries = ParseRetrySuffix(ref end);
        return new AskExprAst(capability.Camel, operation, args, Combine(start, end), retries);
    }

    /// <summary>", retrying up to 3 times" — an explicit retry count. Only reads may carry one (checked in binding).</summary>
    private int ParseRetrySuffix(ref SourceSpan end)
    {
        if (!(Check(TokenKind.Comma) && IsAtWord(_current + 1, "retrying", "retry")))
        {
            return 0;
        }

        Advance(); // comma
        Advance(); // retrying
        if (IsAtWord(_current, "up"))
        {
            Advance();
        }

        Match(TokenKind.To);
        if (!Check(TokenKind.IntegerLiteral))
        {
            _diagnostics.Error("E1104", "Expected a number after 'retrying up to', such as 'retrying up to 3 times'.", _filePath, Peek().Span);
            return 0;
        }

        var count = Advance();
        end = count.Span;
        if (IsAtWord(_current, "time") || (Check(TokenKind.Star) && Peek().Text.Equals("times", StringComparison.OrdinalIgnoreCase)))
        {
            end = Advance().Span;
        }

        return count.Literal is long l ? (int)l : 0;
    }

    private bool IsAtWord(int index, params string[] words) =>
        index < _tokens.Count
        && _tokens[index].Kind == TokenKind.Identifier
        && words.Any(w => _tokens[index].Text.Equals(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// try to
    ///     …
    /// if that fails
    ///     …
    /// finally
    ///     …
    /// </summary>
    private TryStmtAst ParseTry()
    {
        var start = Consume(TokenKind.Try, "Expected 'try'.").Span;
        Match(TokenKind.To);
        Consume(TokenKind.NewLine, "Expected a newline after 'try to'.");
        var body = ParseBlock();
        var end = body.Count > 0 ? body[^1].Span : start;

        IReadOnlyList<StmtAst>? onFailure = null;
        IReadOnlyList<StmtAst>? finallyBody = null;

        SkipNewLines();
        if (Check(TokenKind.If) && IsAtWord(_current + 1, "that") && IsAtWord(_current + 2, "fails"))
        {
            Advance();
            Advance();
            Advance();
            Match(TokenKind.Comma);
            Consume(TokenKind.NewLine, "Expected a newline after 'if that fails'.");
            var block = ParseBlock();
            onFailure = block;
            if (block.Count > 0)
            {
                end = block[^1].Span;
            }
        }

        SkipNewLines();
        if (Check(TokenKind.Finally))
        {
            Advance();
            Match(TokenKind.Comma);
            Consume(TokenKind.NewLine, "Expected a newline after 'finally'.");
            var block = ParseBlock();
            finallyBody = block;
            if (block.Count > 0)
            {
                end = block[^1].Span;
            }
        }

        if (onFailure is null && finallyBody is null)
        {
            _diagnostics.Error("E1104", "'try to' needs an 'if that fails' block, a 'finally' block, or both.", _filePath, start);
        }

        return new TryStmtAst(body, onFailure, finallyBody, Combine(start, end));
    }

    private AskArgAst ParseAskArgument()
    {
        if (IsNamedArgument())
        {
            Match(TokenKind.The, TokenKind.A, TokenKind.An);
            var name = ReadWords();
            var nameSpan = name?.Span ?? Peek().Span;
            Consume(TokenKind.Set, "Expected 'set'.");
            Consume(TokenKind.To, "Expected 'to'.");
            var value = ParseComparison();
            return new AskArgAst(name?.Camel ?? "_error", value, Combine(nameSpan, value.Span));
        }

        var expr = ParseComparison();
        return new AskArgAst(null, expr, expr.Span);
    }

    private bool IsNamedArgument()
    {
        var i = _current;
        while (IsAt(i, TokenKind.The) || IsAt(i, TokenKind.A) || IsAt(i, TokenKind.An))
        {
            i++;
        }

        if (!StartsName(i))
        {
            return false;
        }

        i++;
        while (ContinuesName(i))
        {
            i++;
        }

        return IsAt(i, TokenKind.Set) && IsAt(i + 1, TokenKind.To);
    }

    private ExprAst ParsePostfix(ExprAst expr)
    {
        while (Match(TokenKind.Sorted))
        {
            Consume(TokenKind.By, "Expected 'by' after 'sorted'.");
            Match(TokenKind.The, TokenKind.A, TokenKind.An);
            var field = ReadFieldWord();
            var descending = false;
            var end = Previous().Span;
            if (Check(TokenKind.Comma) && (CheckNext(TokenKind.Largest) || CheckNext(TokenKind.Smallest)))
            {
                Advance();
            }

            if (Match(TokenKind.Largest, TokenKind.Smallest))
            {
                descending = Previous().Kind == TokenKind.Largest;
                end = Consume(TokenKind.First, "Expected 'first' after 'largest' or 'smallest'.").Span;
            }

            expr = new SortExprAst(expr, field, descending, Combine(expr.Span, end));
        }

        expr = ApplyCalling(expr);
        return expr;
    }

    private string ReadFieldWord()
    {
        if (Match(TokenKind.Length))
        {
            return "length";
        }

        if (Match(TokenKind.Count))
        {
            return "count";
        }

        var words = ReadWords();
        if (words is null)
        {
            _diagnostics.Error("E1104", "Expected a field name.", _filePath, Peek().Span);
            return "_error";
        }

        return words.Value.Camel;
    }

    private ExprAst ParseNounPhrase()
    {
        var start = Peek().Span;
        var startIndex = _current;
        var article = Match(TokenKind.The) || Match(TokenKind.A) || Match(TokenKind.An);
        if (TryParseConstructNoun(start, article) is { } noun)
        {
            return noun;
        }

        if (article && Check(TokenKind.Empty) && CheckNext(TokenKind.List))
        {
            Advance();
            var end = Advance().Span;
            return new ListLiteralExprAst([], Combine(start, end));
        }

        if (IsTake())
        {
            Advance();
            var count = ParsePrimary();
            Consume(TokenKind.Of, "Expected 'of' after the count.");
            var target = ParsePrimary();
            return new TakeExprAst(target, count, Combine(start, target.Span));
        }

        if (Check(TokenKind.Count) && CheckNext(TokenKind.Of))
        {
            Advance();
            Advance();
            var inner = ParsePrimary();
            return new CallExprAst("len", [inner], Combine(start, inner.Span));
        }

        if (Check(TokenKind.List) && CheckNext(TokenKind.Containing))
        {
            return ParseListContaining(start);
        }

        if (Check(TokenKind.Sum) && CheckNext(TokenKind.Of))
        {
            Advance();
            return ParseSumOf(start);
        }

        if (Check(TokenKind.Length) && CheckNext(TokenKind.Of))
        {
            Advance();
            Advance();
            var inner = ParsePrimary();
            return new CallExprAst("len", [inner], Combine(start, inner.Span));
        }

        // "the environment variable HOME" / "the environment variable "PATH""
        if (IsAtWord(_current, "environment") && IsAtWord(_current + 1, "variable"))
        {
            Advance();
            Advance();
            Token nameToken;
            if (Check(TokenKind.StringLiteral) || StartsName(_current))
            {
                nameToken = Advance();
            }
            else
            {
                _diagnostics.Error("E1104", "Expected the variable's name after 'environment variable'.", _filePath, Peek().Span);
                return new LiteralExprAst("", start);
            }

            var variable = nameToken.Literal as string ?? nameToken.Text;
            return new CallExprAst("env", [new LiteralExprAst(variable, nameToken.Span)], Combine(start, nameToken.Span));
        }

        if (article && TryParsePathCommand(start) is { } pathCommand)
        {
            return pathCommand;
        }

        if (!article && TryParsePossessive(start) is { } possessive)
        {
            return possessive;
        }

        var name = ReadWords();
        if (name is null)
        {
            _diagnostics.Error("E1103", "Expected a name.", _filePath, Peek().Span);
            return new LiteralExprAst(0L, start);
        }

        if (Match(TokenKind.Of))
        {
            var target = ParsePrimary();
            return new OfExprAst(name.Value.Camel, target, Combine(start, target.Span)) { Raw = RawText(startIndex) };
        }

        if (Match(TokenKind.With))
        {
            return ParseWith(name.Value, start);
        }

        if (Match(TokenKind.Where))
        {
            return ParseWhere(name.Value, start);
        }

        var raw = name.Value.WordCount > 1 ? name.Value.Raw : null;
        return new NameExprAst(name.Value.Camel, Combine(start, name.Value.Span), raw);
    }

    private ExprAst ParseSumOf(SourceSpan start)
    {
        Consume(TokenKind.Of, "Expected 'of' after 'sum'.");
        if (LooksLikeSumProjection())
        {
            var left = ParseName();
            Consume(TokenKind.Star, "Expected 'times'.");
            var right = ParseName();
            Consume(TokenKind.For, "Expected 'for each'.");
            Consume(TokenKind.Each, "Expected 'each'.");
            var iterator = ParseName();
            Consume(TokenKind.In, "Expected 'in'.");
            var collection = ParseName();
            var phrase =
                $"sum of {left.Raw} times {right.Raw} for each {iterator.Raw} in {collection.Raw}";
            return new IntentExprAst(phrase, Combine(start, collection.Span), Grammatical: true);
        }

        var inner = ParseOr();
        return new CallExprAst("sum", [inner], Combine(start, inner.Span));
    }

    private ExprAst ParseWith(EnglishName name, SourceSpan start)
    {
        var first = ParseComparison();
        if (Match(TokenKind.Added))
        {
            var baseName = new NameExprAst(name.Camel, name.Span, name.WordCount > 1 ? name.Raw : null);
            return new CallExprAst("append", [baseName, first], Combine(start, Previous().Span));
        }

        var args = new List<ExprAst> { first };
        while (Match(TokenKind.And))
        {
            args.Add(ParseComparison());
        }

        var end = args[^1].Span;
        return new CallExprAst(name.Camel, args, Combine(start, end));
    }

    private ExprAst ParseWhere(EnglishName collection, SourceSpan start)
    {
        Match(TokenKind.The, TokenKind.A, TokenKind.An);
        var fieldName = ReadFieldWord();
        var field = new EnglishName(fieldName, fieldName, Previous().Span, 1);

        var op = "==";
        if (Match(TokenKind.Is))
        {
            op = EnglishOpText(ParseEnglishComparison());
        }
        else if (Match(TokenKind.GreaterEqual, TokenKind.LessEqual, TokenKind.Greater, TokenKind.Less,
                     TokenKind.EqualEqual, TokenKind.BangEqual))
        {
            op = EnglishOpText(Previous().Kind);
        }

        var value = ParsePrimary();
        var phrase = $"{collection.Camel} where {field.Camel} {op} {FormatLiteral(value)}";
        return new IntentExprAst(phrase, Combine(start, value.Span), Grammatical: true);
    }

    private ExprAst ParseListContaining(SourceSpan start)
    {
        Advance(); // list
        Advance(); // containing
        var elements = new List<ExprAst>();
        elements.Add(ParseComparison());
        while (Check(TokenKind.Comma) && !IsContinuation(_current + 1))
        {
            Advance();
            elements.Add(ParseComparison());
        }

        if (Match(TokenKind.And))
        {
            elements.Add(ParseComparison());
        }

        var end = elements.Count > 0 ? elements[^1].Span : start;
        return new ListLiteralExprAst(elements, Combine(start, end));
    }

    private bool LooksLikeSumProjection()
    {
        var i = _current;
        if (!TrySkipWords(ref i))
        {
            return false;
        }

        if (!IsAt(i, TokenKind.Star))
        {
            return false;
        }

        i++;
        if (!TrySkipWords(ref i))
        {
            return false;
        }

        return IsAt(i, TokenKind.For) && IsAt(i + 1, TokenKind.Each);
    }

    private bool TrySkipWords(ref int i)
    {
        while (IsAt(i, TokenKind.The) || IsAt(i, TokenKind.A) || IsAt(i, TokenKind.An))
        {
            i++;
        }

        if (!StartsName(i))
        {
            return false;
        }

        i++;
        while (ContinuesName(i))
        {
            i++;
        }

        return true;
    }

    /// <summary>"the first 3 of the files" takes. "the first is 1" names a value.</summary>
    private bool IsTake()
    {
        if (!Check(TokenKind.First))
        {
            return false;
        }

        var i = _current + 1;
        return SkipNoun(ref i) && IsAt(i, TokenKind.Of);
    }

    private bool SkipNoun(ref int i)
    {
        if (IsAt(i, TokenKind.LeftParen))
        {
            var depth = 0;
            while (i < _tokens.Count)
            {
                if (IsAt(i, TokenKind.LeftParen))
                {
                    depth++;
                }
                else if (IsAt(i, TokenKind.RightParen))
                {
                    depth--;
                }

                i++;
                if (depth == 0)
                {
                    return true;
                }
            }

            return false;
        }

        if (IsAt(i, TokenKind.IntegerLiteral) || IsAt(i, TokenKind.DecimalLiteral) || IsAt(i, TokenKind.StringLiteral)
            || IsAt(i, TokenKind.True) || IsAt(i, TokenKind.False))
        {
            i++;
            return true;
        }

        while (IsAt(i, TokenKind.The) || IsAt(i, TokenKind.A) || IsAt(i, TokenKind.An))
        {
            i++;
        }

        if (!StartsName(i))
        {
            return false;
        }

        i++;
        while (ContinuesName(i))
        {
            i++;
        }

        return true;
    }

    private EnglishName ParseName()
    {
        var start = Peek().Span;
        Match(TokenKind.The, TokenKind.A, TokenKind.An);
        var words = ReadWords();
        if (words is null)
        {
            var bad = Peek();
            _diagnostics.Error("E1104", $"Expected a name, found '{bad.Text}'.", _filePath, bad.Span);
            return new EnglishName("_error", "_error", Combine(start, bad.Span), 1);
        }

        return words.Value;
    }

    /// <summary>
    /// A keyword is special only where the grammar asks for it. In a name it is an ordinary word.
    /// Words that close a name ("is", "of", "which", "in") stay outside it, except as the name itself.
    /// "is", "are", and "be" are the copula and never a name.
    /// </summary>
    private EnglishName? ReadWords()
    {
        if (!StartsName(_current))
        {
            return null;
        }

        var words = new List<string>();
        var start = Peek().Span;
        var end = start;
        var tok = Advance();
        words.Add(tok.Text);
        end = tok.Span;
        while (ContinuesName(_current))
        {
            tok = Advance();
            words.Add(tok.Text);
            end = tok.Span;
        }

        return new EnglishName(ToCamel(words), string.Join(' ', words), Combine(start, end), words.Count);
    }

    /// <summary>Any word spelled with letters, including keywords. Strings, numbers, and symbols are not words.</summary>
    private bool IsSpelledWord(int index)
    {
        if ((uint)index >= (uint)_tokens.Count)
        {
            return false;
        }

        var token = _tokens[index];
        if (token.Kind is TokenKind.StringLiteral or TokenKind.IntegerLiteral or TokenKind.DecimalLiteral
            or TokenKind.EndOfFile or TokenKind.NewLine or TokenKind.Indent or TokenKind.Dedent)
        {
            return false;
        }

        var text = token.Text;
        return text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_');
    }

    /// <summary>The first word of a name. The copula and articles are the grammar around the name.</summary>
    private bool StartsName(int index) => IsSpelledWord(index) && !IsCopula(index);

    /// <summary>Another word of the same name. Clause words end the name.</summary>
    private bool ContinuesName(int index) =>
        IsSpelledWord(index) && !IsNameCloser(index) && !IsTrailingOperator(_tokens[index].Text);

    private bool IsCopula(int index) =>
        index < _tokens.Count && Copula.Contains(_tokens[index].Text);

    private bool IsNameCloser(int index) =>
        index < _tokens.Count && NameClosers.Contains(_tokens[index].Text);

    private static readonly HashSet<string> Copula = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "is", "are", "be"
    };

    private static readonly HashSet<string> NameClosers = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the",
        "is", "are", "be",
        "which", "has",
        "of", "in", "from", "to",
        "given", "returning",
        "where", "with", "and", "or", "not",
        "if", "otherwise", "for", "each", "while", "until",
        "return", "give", "back", "say", "when",
        "allow", "use", "as", "by", "than", "at",
        "let", "try", "finally", "set", "ask",
        "added", "sorted", "containing",
        "equal", "modulo", "divided", "rounded",
        "plus", "minus", "times", "greater", "less"
    };

    private static bool IsTrailingOperator(string text) =>
        text.Equals("spread", StringComparison.OrdinalIgnoreCase)
        || text.Equals("splatted", StringComparison.OrdinalIgnoreCase)
        || text.Equals("calling", StringComparison.OrdinalIgnoreCase)
        || text.Equals("joined", StringComparison.OrdinalIgnoreCase)
        || text.Equals("replacing", StringComparison.OrdinalIgnoreCase)
        || text.Equals("formatted", StringComparison.OrdinalIgnoreCase)
        || text.Equals("piped", StringComparison.OrdinalIgnoreCase)
        || text.Equals("bitwise", StringComparison.OrdinalIgnoreCase)
        || text.Equals("shifted", StringComparison.OrdinalIgnoreCase)
        || text.Equals("contains", StringComparison.OrdinalIgnoreCase)
        || text.Equals("matches", StringComparison.OrdinalIgnoreCase);

    private static string ToCamel(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
        {
            return "_error";
        }

        if (words.Count == 1)
        {
            return words[0];
        }

        var sb = new System.Text.StringBuilder(words[0]);
        for (var i = 1; i < words.Count; i++)
        {
            var word = words[i];
            if (word.Length == 0)
            {
                continue;
            }

            sb.Append(char.ToUpperInvariant(word[0]));
            if (word.Length > 1)
            {
                sb.Append(word[1..]);
            }
        }

        return sb.ToString();
    }

    private static string EnglishOpText(TokenKind op) => op switch
    {
        TokenKind.EqualEqual => "==",
        TokenKind.BangEqual => "!=",
        TokenKind.Less => "<",
        TokenKind.LessEqual => "<=",
        TokenKind.Greater => ">",
        TokenKind.GreaterEqual => ">=",
        _ => "=="
    };

    private static string FormatLiteral(ExprAst expr) => expr switch
    {
        LiteralExprAst { Value: string s } => $"\"{s}\"",
        LiteralExprAst { Value: bool b } => b ? "true" : "false",
        LiteralExprAst { Value: long l } => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
        LiteralExprAst { Value: decimal d } => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        NameExprAst name => name.Name,
        _ => "0"
    };

    private void Synchronize()
    {
        while (!IsAtEnd())
        {
            if (Match(TokenKind.NewLine))
            {
                return;
            }

            if (Check(TokenKind.To) || Check(TokenKind.An) || Check(TokenKind.A) || Check(TokenKind.Use))
            {
                return;
            }

            Advance();
        }
    }

    private void SkipNewLines()
    {
        while (Match(TokenKind.NewLine))
        {
        }
    }

    private bool Match(params TokenKind[] kinds)
    {
        foreach (var kind in kinds)
        {
            if (Check(kind))
            {
                Advance();
                return true;
            }
        }

        return false;
    }

    private Token Consume(TokenKind kind, string message)
    {
        if (Check(kind))
        {
            return Advance();
        }

        if (_paragraph > 0 && kind == TokenKind.NewLine && AtClauseEnd())
        {
            return Peek();
        }

        var found = Peek();
        _diagnostics.Error("E1104", $"{message} Found '{found.Text}'.", _filePath, found.Span);
        return found;
    }

    private bool Check(TokenKind kind) =>
        kind == TokenKind.EndOfFile ? IsAtEnd() : !IsAtEnd() && Peek().Kind == kind;

    private bool CheckNext(TokenKind kind) =>
        _current + 1 < _tokens.Count && _tokens[_current + 1].Kind == kind;

    private bool IsAt(int index, TokenKind kind) =>
        index < _tokens.Count && _tokens[index].Kind == kind;

    private Token Advance()
    {
        if (!IsAtEnd())
        {
            _current++;
        }

        return Previous();
    }

    private bool IsAtEnd() => Peek().Kind == TokenKind.EndOfFile;

    private Token Peek() => _tokens[_current];

    private Token Previous() => _tokens[_current - 1];

    private static SourceSpan Combine(SourceSpan a, SourceSpan b) =>
        new(a.StartLine, a.StartColumn, b.EndLine, b.EndColumn);
}
