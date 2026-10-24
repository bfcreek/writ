using Writ.Compiler.Diagnostics;

namespace Writ.Compiler.Syntax;

/// <summary>
/// Parses English-like Writ into a structural AST.
/// Sentences become declarations, assignments, calls, and operators — not an open prompt.
/// </summary>
public sealed class Parser
{
    private readonly record struct EnglishName(string Camel, string Raw, SourceSpan Span, int WordCount);

    private readonly List<Token> _tokens;
    private readonly string? _filePath;
    private readonly DiagnosticBag _diagnostics;
    private int _current;

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
                declarations.Add(ParseEntry());
            }
            else if (IsAtWord(_current, "also") && CheckNext(TokenKind.Use))
            {
                declarations.Add(ParseInclude());
            }
            else
            {
                var bad = Advance();
                _diagnostics.Error("E1101",
                    $"Expected 'An ... has', 'Use ... from', 'Also use', 'Allow', 'When run', or 'To ...', found '{bad.Text}'.",
                    _filePath, bad.Span);
                Synchronize();
            }

            SkipNewLines();
        }

        var end = Previous().Span;
        return new ProgramAst(declarations, Combine(start, end));
    }

    private TypeDeclAst ParseTypeDecl()
    {
        var start = Advance().Span; // An / A
        var name = ParseName();
        Consume(TokenKind.Has, "Expected 'has' after the type name.");

        var fields = new List<FieldDeclAst>();
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

        return new TypeDeclAst(name.Camel, fields, Combine(start, end));
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
        if (Check(TokenKind.Identifier) && Peek().Text.Equals("mcp", StringComparison.OrdinalIgnoreCase))
        {
            Advance();
            kind = "mcp";
        }

        var path = Consume(TokenKind.StringLiteral, kind == "mcp"
            ? "Expected the MCP server command line in quotes."
            : "Expected the manifest path in quotes.");
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
    /// The script's own parameter block. "returning" is optional; without it the script returns nothing.
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

        Consume(TokenKind.NewLine, "Expected a newline after 'When run …'.");
        var body = ParseBlock();
        var end = body.Count > 0 ? body[^1].Span : start;
        return new FunctionAst("Main", parameters, returnType, body, Combine(start, end), IsEntry: true);
    }

    private FunctionAst ParseFunction()
    {
        var start = Consume(TokenKind.To, "Expected 'To'.").Span;
        var name = ParseName();
        Match(TokenKind.Comma);

        var parameters = ParseParameterList();

        Match(TokenKind.Comma);
        Consume(TokenKind.Returning, "Expected 'returning' and a result type.");
        var returnType = ParseType();
        Consume(TokenKind.NewLine, "Expected a newline after the description.");
        var body = ParseBlock();
        var end = body.Count > 0 ? body[^1].Span : returnType.Span;
        return new FunctionAst(name.Camel, parameters, returnType, body, Combine(start, end));
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
                parameters.Add(new ParameterAst(paramName.Camel, paramType, Combine(paramName.Span, paramType.Span)));
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
            if (Check(TokenKind.Dedent) || IsAtEnd())
            {
                break;
            }

            var mark = _current;
            statements.Add(ParseStatement());
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
            return ParseForEach();
        }

        if (Check(TokenKind.Return) || Check(TokenKind.Give))
        {
            return ParseReturn();
        }

        if (Check(TokenKind.Say))
        {
            return ParseSay();
        }

        if (Check(TokenKind.While) || Check(TokenKind.Until))
        {
            return ParseWhile();
        }

        if (Check(TokenKind.Try))
        {
            return ParseTry();
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

    private SayStmtAst ParseSay()
    {
        var start = Consume(TokenKind.Say, "Expected 'say'.").Span;
        var parts = new List<ExprAst> { ParseComparison() };
        while (Match(TokenKind.And))
        {
            parts.Add(ParseComparison());
        }

        Match(TokenKind.NewLine);
        return new SayStmtAst(parts, Combine(start, parts[^1].Span));
    }

    private WhileStmtAst ParseWhile()
    {
        var keyword = Advance();
        var condition = ParseExpression();
        Consume(TokenKind.NewLine, "Expected a newline after the condition.");
        var body = ParseBlock();
        var end = body.Count > 0 ? body[^1].Span : condition.Span;
        return new WhileStmtAst(condition, body, keyword.Kind == TokenKind.Until, Combine(keyword.Span, end));
    }

    private AssignStmtAst ParseAssign()
    {
        var start = Peek().Span;
        Match(TokenKind.Let);
        var name = ParseName();
        if (!Match(TokenKind.Is, TokenKind.Are, TokenKind.Be))
        {
            _diagnostics.Error("E1104", "Expected 'is' to give this name a value.", _filePath, Peek().Span);
        }

        var value = ParseExpressionOrPhrase();
        Match(TokenKind.NewLine);
        return new AssignStmtAst(name.Camel, value, Combine(start, value.Span));
    }

    private IfStmtAst ParseIf()
    {
        var start = Consume(TokenKind.If, "Expected 'if'.").Span;
        var condition = ParseExpression();
        Consume(TokenKind.NewLine, "Expected a newline after the condition.");
        var thenBody = ParseBlock();
        var (otherwise, end) = ParseOtherwise(condition.Span);
        return new IfStmtAst(condition, thenBody, otherwise, Combine(start, end));
    }

    private (IReadOnlyList<StmtAst>? Body, SourceSpan End) ParseOtherwise(SourceSpan fallback)
    {
        SkipNewLines();
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

        Consume(TokenKind.NewLine, "Expected a newline after 'otherwise'.");
        var body = ParseBlock();
        var end = body.Count > 0 ? body[^1].Span : otherwiseTok.Span;
        return (body, end);
    }

    private ForEachStmtAst ParseForEach()
    {
        var start = Consume(TokenKind.For, "Expected 'for'.").Span;
        Consume(TokenKind.Each, "Expected 'each'.");
        var iterator = ParseName();
        Consume(TokenKind.In, "Expected 'in'.");
        var collection = ParseExpression();
        Consume(TokenKind.NewLine, "Expected a newline after the loop description.");
        var body = ParseBlock();
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

        var name = Consume(TokenKind.Identifier, "Expected a type name.");
        return new NamedTypeAst(name.Text, name.Span);
    }

    private ExprAst ParseExpressionOrPhrase()
    {
        var start = _current;
        var expr = ParseExpression();
        if (Check(TokenKind.NewLine) || Check(TokenKind.Dedent) || Check(TokenKind.EndOfFile))
        {
            return expr;
        }

        _current = start;
        return ParseOpenPhrase();
    }

    private IntentExprAst ParseOpenPhrase()
    {
        var start = Peek().Span;
        var parts = new List<string>();
        var end = start;
        while (!Check(TokenKind.NewLine) && !Check(TokenKind.Dedent) && !Check(TokenKind.EndOfFile))
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
            else if (cast.Kind == TokenKind.Identifier && cast.Text.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                Advance();
                expr = new CallExprAst("json", [expr], Combine(expr.Span, cast.Span));
            }
            else if (cast.Kind == TokenKind.Identifier && cast.Text.Equals("csv", StringComparison.OrdinalIgnoreCase))
            {
                Advance();
                expr = new CallExprAst("csv", [expr], Combine(expr.Span, cast.Span));
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

        return expr;
    }

    private ExprAst ParseOr()
    {
        var expr = ParseAnd();
        while (Match(TokenKind.Or))
        {
            var right = ParseAnd();
            expr = new BinaryExprAst(expr, TokenKind.Or, right, Combine(expr.Span, right.Span));
        }

        return expr;
    }

    private ExprAst ParseAnd()
    {
        var expr = ParseComparison();
        while (Match(TokenKind.And))
        {
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
            var op = ParseEnglishComparison();
            var right = ParseSum();
            expr = new BinaryExprAst(expr, op, right, Combine(expr.Span, right.Span));
            return expr;
        }

        if (Match(TokenKind.EqualEqual, TokenKind.BangEqual, TokenKind.Less, TokenKind.LessEqual,
                TokenKind.Greater, TokenKind.GreaterEqual))
        {
            var op = Previous().Kind;
            var right = ParseSum();
            expr = new BinaryExprAst(expr, op, right, Combine(expr.Span, right.Span));
        }

        return expr;
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
        var expr = ParseUnary();
        while (true)
        {
            if (Match(TokenKind.Star, TokenKind.Slash, TokenKind.Percent, TokenKind.Modulo))
            {
                var op = Previous().Kind == TokenKind.Modulo ? TokenKind.Percent : Previous().Kind;
                var right = ParseUnary();
                expr = new BinaryExprAst(expr, op, right, Combine(expr.Span, right.Span));
                continue;
            }

            if (Match(TokenKind.Divided))
            {
                Consume(TokenKind.By, "Expected 'by' after 'divided'.");
                var right = ParseUnary();
                expr = new BinaryExprAst(expr, TokenKind.Slash, right, Combine(expr.Span, right.Span));
                continue;
            }

            break;
        }

        return expr;
    }

    private ExprAst ParseUnary()
    {
        if (Match(TokenKind.Not, TokenKind.Minus))
        {
            var op = Previous();
            var operand = ParseUnary();
            return new UnaryExprAst(op.Kind, operand, Combine(op.Span, operand.Span));
        }

        return ParsePrimary();
    }

    private ExprAst ParsePrimary()
    {
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

            return new LiteralExprAst(Previous().Literal, Previous().Span);
        }

        if (Check(TokenKind.Ask))
        {
            return ParseAsk();
        }

        if (IsRunCall())
        {
            return ParseRun();
        }

        if (Check(TokenKind.The) || Check(TokenKind.A) || Check(TokenKind.An) || Check(TokenKind.Identifier)
            || Check(TokenKind.Sum) || Check(TokenKind.Length) || Check(TokenKind.Empty) || Check(TokenKind.List)
            || Check(TokenKind.First) || Check(TokenKind.Count))
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
        while (!Check(TokenKind.With) && !Check(TokenKind.NewLine) && !Check(TokenKind.Dedent)
               && !Check(TokenKind.Indent) && !Check(TokenKind.EndOfFile) && !Check(TokenKind.Comma))
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

        return IsAt(i, TokenKind.Identifier) && IsAt(i + 1, TokenKind.Set) && IsAt(i + 2, TokenKind.To);
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
        var article = Match(TokenKind.The) || Match(TokenKind.A) || Match(TokenKind.An);

        if (article && Match(TokenKind.Empty))
        {
            var end = Consume(TokenKind.List, "Expected 'list' after 'empty'.").Span;
            return new ListLiteralExprAst([], Combine(start, end));
        }

        if (Match(TokenKind.First))
        {
            var count = ParsePrimary();
            Consume(TokenKind.Of, "Expected 'of' after the count.");
            var target = ParsePrimary();
            return new TakeExprAst(target, count, Combine(start, target.Span));
        }

        if (Match(TokenKind.Count))
        {
            Consume(TokenKind.Of, "Expected 'of' after 'count'.");
            var inner = ParsePrimary();
            return new CallExprAst("len", [inner], Combine(start, inner.Span));
        }

        if (Check(TokenKind.List) && CheckNext(TokenKind.Containing))
        {
            return ParseListContaining(start);
        }

        if (Match(TokenKind.Sum))
        {
            return ParseSumOf(start);
        }

        if (Match(TokenKind.Length))
        {
            Consume(TokenKind.Of, "Expected 'of' after 'length'.");
            var inner = ParsePrimary();
            return new CallExprAst("len", [inner], Combine(start, inner.Span));
        }

        // "the environment variable HOME" / "the environment variable "PATH""
        if (IsAtWord(_current, "environment") && IsAtWord(_current + 1, "variable"))
        {
            Advance();
            Advance();
            Token nameToken;
            if (Check(TokenKind.StringLiteral) || Check(TokenKind.Identifier))
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

        var name = ReadWords();
        if (name is null)
        {
            _diagnostics.Error("E1103", "Expected a name.", _filePath, Peek().Span);
            return new LiteralExprAst(0L, start);
        }

        if (Match(TokenKind.Of))
        {
            var target = ParsePrimary();
            return new OfExprAst(name.Value.Camel, target, Combine(start, target.Span));
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
        while (Match(TokenKind.Comma))
        {
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

        if (!IsAt(i, TokenKind.Identifier))
        {
            return false;
        }

        while (IsAt(i, TokenKind.Identifier))
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

    private EnglishName? ReadWords()
    {
        if (!Check(TokenKind.Identifier))
        {
            return null;
        }

        var words = new List<string>();
        var start = Peek().Span;
        var end = start;
        while (Check(TokenKind.Identifier))
        {
            var tok = Advance();
            words.Add(tok.Text);
            end = tok.Span;
        }

        return new EnglishName(ToCamel(words), string.Join(' ', words), Combine(start, end), words.Count);
    }

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

        var found = Peek();
        _diagnostics.Error("E1104", $"{message} Found '{found.Text}'.", _filePath, found.Span);
        return found;
    }

    private bool Check(TokenKind kind) => !IsAtEnd() && Peek().Kind == kind;

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
