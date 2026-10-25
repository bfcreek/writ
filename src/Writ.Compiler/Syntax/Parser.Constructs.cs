using Writ.Compiler.Diagnostics;

namespace Writ.Compiler.Syntax;

public sealed partial class Parser
{
    private StmtAst? TryParseConstruct()
    {
        var mark = _current;
        var statement = TryConstructStatement();
        if (statement is null)
        {
            _current = mark;
        }

        return statement;
    }

    private StmtAst? TryConstructStatement()
    {
        if (IsBreakOrContinue("break") || IsBreakOrContinue("continue"))
        {
            return ParseBreakOrContinue();
        }

        if (IsWord(_current, "exit") && IsWord(_current + 1, "the") && IsWord(_current + 2, "process"))
        {
            return ParseExit();
        }

        if (IsWord(_current, "repeat") && CheckNext(TokenKind.NewLine))
        {
            return ParseRepeat();
        }

        if (IsWord(_current, "switch") && IsWord(_current + 1, "on"))
        {
            return ParseSwitch();
        }

        if (IsWord(_current, "trap") && IsWord(_current + 1, "errors"))
        {
            return ParseTrapOrData("trap");
        }

        if (IsWord(_current, "data") && CheckNext(TokenKind.NewLine))
        {
            return ParseTrapOrData("data");
        }

        if (IsWord(_current, "mark") && IsAt(_current + 1, TokenKind.Identifier))
        {
            return ParseMark();
        }

        if (IsWord(_current, "increase", "decrease", "multiply", "divide", "remainder"))
        {
            return ParseUpdate();
        }

        if (IsWord(_current, "redirect"))
        {
            return ParseRedirect();
        }

        if (IsWord(_current, "import") && IsWord(_current + 1, "the") && IsWord(_current + 2, "module"))
        {
            return ParseModuleStatement("import");
        }

        if (IsWord(_current, "remove") && IsWord(_current + 1, "the") && IsWord(_current + 2, "module"))
        {
            return ParseModuleStatement("remove");
        }

        if (IsWord(_current, "source") && IsWord(_current + 1, "the") && IsWord(_current + 2, "script"))
        {
            return ParseModuleStatement("source");
        }

        if (IsWord(_current, "before") && CheckNext(TokenKind.NewLine))
        {
            return ParseSection("begin");
        }

        if (IsWord(_current, "after") && CheckNext(TokenKind.NewLine))
        {
            return ParseSection("end");
        }

        if (IsWord(_current, "on") && CheckNext(TokenKind.Each))
        {
            return ParseSection("process");
        }

        if (Check(TokenKind.When) && IsWord(_current + 1, "binding") && IsWord(_current + 2, "parameters"))
        {
            return ParseSection("dynamicparam");
        }

        return null;
    }

    private bool IsBreakOrContinue(string word)
    {
        if (!IsWord(_current, word))
        {
            return false;
        }

        if (CheckNext(TokenKind.NewLine) || CheckNext(TokenKind.Dedent) || IsAt(_current + 1, TokenKind.EndOfFile))
        {
            return true;
        }

        if (IsWord(_current + 1, "the") && IsWord(_current + 2, "loop"))
        {
            return true;
        }

        return IsWord(_current + 1, "to");
    }

    private FlowStmtAst ParseBreakOrContinue()
    {
        var keyword = Advance();
        string? label = null;
        if (IsWord(_current, "the") && IsWord(_current + 1, "loop"))
        {
            Advance();
            Advance();
        }
        else if (IsWord(_current, "to"))
        {
            Advance();
            label = ParseName().Camel;
        }

        var end = label is null ? keyword.Span : Previous().Span;
        Match(TokenKind.NewLine);
        return new FlowStmtAst(keyword.Text.ToLowerInvariant(), label, null, Combine(keyword.Span, end));
    }

    private FlowStmtAst ParseExit()
    {
        var start = Advance().Span;
        Advance();
        Advance();
        ExprAst? code = null;
        var end = Previous().Span;
        if (IsWord(_current, "with"))
        {
            Advance();
            code = ParseExpression();
            end = code.Span;
        }

        Match(TokenKind.NewLine);
        return new FlowStmtAst("exit", null, code, Combine(start, end));
    }

    private DoStmtAst ParseRepeat()
    {
        var start = Advance().Span;
        Consume(TokenKind.NewLine, "Expected a newline after 'repeat'.");
        var body = ParseBlock();
        var until = IsWord(_current, "until");
        if (!until && !Check(TokenKind.While))
        {
            _diagnostics.Error("E1104", "Expected 'while' or 'until' after 'repeat'.", _filePath, Peek().Span);
        }
        else
        {
            Advance();
        }

        var condition = ParseExpression();
        Match(TokenKind.NewLine);
        return new DoStmtAst(body, condition, until, Combine(start, condition.Span));
    }

    private ForStmtAst ParseCountedFor()
    {
        var start = Advance().Span;
        var name = ParseName();
        Consume(TokenKind.From, "Expected 'from'.");
        var from = ParseExpression();
        Consume(TokenKind.To, "Expected 'to'.");
        var to = ParseExpression();
        Consume(TokenKind.NewLine, "Expected a newline after the range.");
        var body = ParseBlock();
        var end = body.Count > 0 ? body[^1].Span : to.Span;
        return new ForStmtAst(name.Camel, from, to, body, Combine(start, end));
    }

    private SwitchStmtAst ParseSwitch()
    {
        var start = Advance().Span;
        Advance();
        var value = ParseExpression();
        Consume(TokenKind.NewLine, "Expected a newline after 'switch on'.");
        Consume(TokenKind.Indent, "Expected an indented switch.");
        var arms = new List<SwitchArmAst>();
        IReadOnlyList<StmtAst>? otherwise = null;
        while (!Check(TokenKind.Dedent) && !IsAtEnd())
        {
            SkipNewLines();
            if (Check(TokenKind.Dedent) || IsAtEnd())
            {
                break;
            }

            if (Check(TokenKind.When))
            {
                var armStart = Advance().Span;
                var values = new List<ExprAst> { ParseComparison() };
                while (Match(TokenKind.Or))
                {
                    values.Add(ParseComparison());
                }

                Consume(TokenKind.NewLine, "Expected a newline after 'when'.");
                var body = ParseBlock();
                arms.Add(new SwitchArmAst(values, body, Combine(armStart, body.Count > 0 ? body[^1].Span : armStart)));
                continue;
            }

            if (Check(TokenKind.Otherwise))
            {
                Advance();
                Consume(TokenKind.NewLine, "Expected a newline after 'otherwise'.");
                otherwise = ParseBlock();
                continue;
            }

            _diagnostics.Error("E1104", "Expected 'when' or 'otherwise' in a switch.", _filePath, Peek().Span);
            Advance();
        }

        var end = Consume(TokenKind.Dedent, "Expected the end of the switch.").Span;
        return new SwitchStmtAst(value, arms, otherwise, Combine(start, end));
    }

    private BlockStmtAst ParseTrapOrData(string kind)
    {
        var start = Advance().Span;
        if (kind == "trap")
        {
            Advance();
        }

        Consume(TokenKind.NewLine, "Expected a newline.");
        var body = ParseBlock();
        var end = body.Count > 0 ? body[^1].Span : start;
        return new BlockStmtAst(kind, body, Combine(start, end));
    }

    private BlockStmtAst ParseSection(string kind)
    {
        var start = Peek().Span;
        if (kind == "process")
        {
            Advance();
            Advance();
        }
        else if (kind == "dynamicparam")
        {
            Advance();
            Advance();
            Advance();
        }
        else
        {
            Advance();
        }

        Consume(TokenKind.NewLine, "Expected a newline.");
        var body = ParseBlock();
        var end = body.Count > 0 ? body[^1].Span : start;
        return new BlockStmtAst(kind, body, Combine(start, end));
    }

    private LabeledStmtAst ParseMark()
    {
        var start = Advance().Span;
        var label = ParseName();
        Match(TokenKind.NewLine);
        var statement = ParseStatement();
        return new LabeledStmtAst(label.Camel, statement, Combine(start, statement.Span));
    }

    private AssignStmtAst ParseUpdate()
    {
        var word = Advance();
        var which = word.Text.ToLowerInvariant();
        Match(TokenKind.The, TokenKind.A, TokenKind.An);
        string? scope = null;
        if (IsWord(_current, "global", "local", "private")
            || (IsWord(_current, "script") && !IsWord(_current + 1, "folder", "directory")))
        {
            scope = Advance().Text.ToLowerInvariant();
        }

        var name = ReadWords();
        if (name is null)
        {
            _diagnostics.Error("E1104", "Expected a name.", _filePath, Peek().Span);
            name = new EnglishName("_error", "_error", word.Span, 1);
        }

        string op;
        ExprAst amount;
        if (IsWord(_current, "by"))
        {
            Advance();
            amount = ParseExpression();
            op = which switch
            {
                "decrease" => "-=",
                "multiply" => "*=",
                "divide" => "/=",
                "remainder" => "%=",
                _ => "+="
            };
        }
        else if (which is "increase" or "decrease")
        {
            op = which == "increase" ? "++" : "--";
            amount = new LiteralExprAst(1L, word.Span);
        }
        else
        {
            _diagnostics.Error("E1104", $"Expected 'by' after '{which}'.", _filePath, Peek().Span);
            op = "+=";
            amount = new LiteralExprAst(1L, word.Span);
        }

        Match(TokenKind.NewLine);
        return new AssignStmtAst(name.Value.Camel, amount, Combine(word.Span, amount.Span))
        {
            Scope = scope,
            Operator = op
        };
    }

    private RedirectStmtAst ParseRedirect()
    {
        var start = Advance().Span;
        var append = IsWord(_current, "and") && IsWord(_current + 1, "append");
        if (append)
        {
            Advance();
            Advance();
        }

        var errors = false;
        var all = false;
        if (IsWord(_current, "all") && IsWord(_current + 1, "output"))
        {
            all = true;
            Advance();
            Advance();
        }
        else
        {
            Match(TokenKind.The);
            if (IsWord(_current, "errors"))
            {
                errors = true;
                Advance();
            }
            else if (IsWord(_current, "output"))
            {
                Advance();
            }

            if (IsWord(_current, "of"))
            {
                Advance();
            }
        }

        if (IsWord(_current, "of"))
        {
            Advance();
        }

        var command = ParseExpression();
        var op = all ? "*>" : errors ? "2>" : append ? ">>" : ">";
        ExprAst? target = null;
        if (IsWord(_current, "to"))
        {
            Advance();
            if (errors && IsWord(_current, "the") && IsWord(_current + 1, "output"))
            {
                Advance();
                Advance();
                op = "2>&1";
            }
            else
            {
                target = ParseExpression();
                if (append && errors)
                {
                    op = "2>>";
                }
            }
        }

        var end = target?.Span ?? command.Span;
        Match(TokenKind.NewLine);
        return new RedirectStmtAst(op, command, target, Combine(start, end));
    }

    private ModuleStmtAst ParseModuleStatement(string kind)
    {
        var start = Advance().Span;
        Advance();
        Advance();
        if (!Check(TokenKind.StringLiteral))
        {
            _diagnostics.Error("E1104", "Expected a quoted name.", _filePath, Peek().Span);
        }

        var name = Advance();
        Match(TokenKind.NewLine);
        return new ModuleStmtAst(kind, name.Literal as string ?? name.Text, Combine(start, name.Span));
    }

    private UsingDeclAst ParseUsingDecl()
    {
        var start = Advance().Span;
        Match(TokenKind.The);
        var kind = "namespace";
        if (IsWord(_current, "module"))
        {
            kind = "module";
            Advance();
        }
        else if (IsWord(_current, "namespace"))
        {
            Advance();
        }

        string name;
        SourceSpan end;
        if (Check(TokenKind.StringLiteral))
        {
            var token = Advance();
            name = token.Literal as string ?? token.Text;
            end = token.Span;
        }
        else
        {
            var words = new List<string>();
            end = Peek().Span;
            while (Check(TokenKind.Identifier) || Check(TokenKind.Dot))
            {
                var token = Advance();
                words.Add(token.Text);
                end = token.Span;
            }

            name = string.Join("", words);
        }

        Match(TokenKind.NewLine);
        return new UsingDeclAst(kind, name, Combine(start, end));
    }

    private ExportDeclAst ParseExportDecl()
    {
        var start = Advance().Span;
        Match(TokenKind.The);
        if (IsWord(_current, "function"))
        {
            Advance();
        }

        var name = ParseName();
        Match(TokenKind.NewLine);
        return new ExportDeclAst(name.Camel, Combine(start, name.Span));
    }

    private TypeDeclAst ParseEnum(SourceSpan start)
    {
        Advance();
        var name = ParseName();
        if (!Match(TokenKind.Of, TokenKind.Has))
        {
            _diagnostics.Error("E1104", "Expected 'of' after the enumeration name.", _filePath, Peek().Span);
        }

        var members = new List<string>();
        do
        {
            Match(TokenKind.And);
            if (!Check(TokenKind.Identifier))
            {
                break;
            }

            members.Add(Advance().Text);
        }
        while (Match(TokenKind.Comma) || (Check(TokenKind.And) && IsAt(_current + 1, TokenKind.Identifier)));

        Match(TokenKind.NewLine);
        var end = members.Count > 0 ? Previous().Span : name.Span;
        return new TypeDeclAst(name.Camel, [], Combine(start, end))
        {
            IsEnum = true,
            EnumMembers = members
        };
    }

    private ParameterAst ParseParameterTail(ParameterAst parameter)
    {
        while (true)
        {
            if (IsWord(_current, "defaulting") && IsWord(_current + 1, "to"))
            {
                Advance();
                Advance();
                var value = ParseComparison();
                parameter = parameter with { Default = value, Span = Combine(parameter.Span, value.Span) };
                continue;
            }

            if (Check(TokenKind.From) && IsWord(_current + 1, "the") && IsWord(_current + 2, "pipeline"))
            {
                Advance();
                Advance();
                Advance();
                parameter = parameter with { FromPipeline = true };
                continue;
            }

            if (IsWord(_current, "also") && IsWord(_current + 1, "called"))
            {
                Advance();
                Advance();
                if (!Check(TokenKind.Identifier))
                {
                    _diagnostics.Error("E1104", "Expected an alias.", _filePath, Peek().Span);
                    continue;
                }

                var alias = Advance();
                parameter = parameter with { Alias = alias.Text, Span = Combine(parameter.Span, alias.Span) };
                continue;
            }

            if (IsWord(_current, "one") && IsWord(_current + 1, "of"))
            {
                Advance();
                Advance();
                var values = new List<string>();
                do
                {
                    Match(TokenKind.And);
                    if (!Check(TokenKind.StringLiteral) && !Check(TokenKind.IntegerLiteral))
                    {
                        break;
                    }

                    var token = Advance();
                    values.Add(token.Literal?.ToString() ?? token.Text);
                }
                while (Match(TokenKind.Comma) || Check(TokenKind.And));

                parameter = parameter with { OneOf = values };
                continue;
            }

            if (IsWord(_current, "matching") && CheckNext(TokenKind.StringLiteral))
            {
                Advance();
                var pattern = Advance();
                parameter = parameter with { Pattern = pattern.Literal as string ?? pattern.Text };
                continue;
            }

            if (Check(TokenKind.With) && IsWord(_current + 1, "length") && IsAt(_current + 2, TokenKind.From))
            {
                Advance();
                Advance();
                var (min, max) = ReadSpan();
                parameter = parameter with { MinLength = min, MaxLength = max };
                continue;
            }

            if (Check(TokenKind.With) && IsWord(_current + 1, "count") && IsAt(_current + 2, TokenKind.From))
            {
                Advance();
                Advance();
                var (min, max) = ReadSpan();
                parameter = parameter with { MinCount = min, MaxCount = max };
                continue;
            }

            if (Check(TokenKind.From) && CheckNext(TokenKind.IntegerLiteral))
            {
                var (min, max) = ReadSpan();
                parameter = parameter with { Min = min, Max = max };
                continue;
            }

            if (IsWord(_current, "allowing"))
            {
                Advance();
                if (IsWord(_current, "nothing"))
                {
                    Advance();
                    parameter = parameter with { AllowNull = true };
                }
                else if (IsWord(_current, "empty") && IsWord(_current + 1, "text"))
                {
                    Advance();
                    Advance();
                    parameter = parameter with { AllowEmptyString = true };
                }
                else if (IsWord(_current, "an", "a") && IsWord(_current + 1, "empty"))
                {
                    Advance();
                    Advance();
                    Match(TokenKind.List);
                    parameter = parameter with { AllowEmptyCollection = true };
                }
                else if (IsWord(_current, "wildcards"))
                {
                    Advance();
                    parameter = parameter with { SupportsWildcards = true };
                }

                continue;
            }

            if (IsWord(_current, "rejecting"))
            {
                Advance();
                if (IsWord(_current, "nothing"))
                {
                    Advance();
                    parameter = parameter with { RejectNull = true };
                }
                else if (IsWord(_current, "empty"))
                {
                    Advance();
                    parameter = parameter with { RejectEmpty = true };
                }

                continue;
            }

            if (IsWord(_current, "passing") && IsWord(_current + 1, "the") && IsWord(_current + 2, "test"))
            {
                Advance();
                Advance();
                Advance();
                if (IsWord(_current, "that"))
                {
                    Advance();
                }

                var test = ParseComparison();
                parameter = parameter with { ValidateTest = test, Span = Combine(parameter.Span, test.Span) };
                continue;
            }

            break;
        }

        return parameter;
    }

    private (long? Min, long? Max) ReadSpan()
    {
        Consume(TokenKind.From, "Expected 'from'.");
        long? min = Check(TokenKind.IntegerLiteral) ? PreviousLiteral() : null;
        if (Check(TokenKind.IntegerLiteral))
        {
            Advance();
        }

        Consume(TokenKind.To, "Expected 'to'.");
        long? max = Check(TokenKind.IntegerLiteral) ? Convert.ToInt64(Peek().Literal) : null;
        if (Check(TokenKind.IntegerLiteral))
        {
            Advance();
        }

        return (min, max);
    }

    private long PreviousLiteral() => Convert.ToInt64(Peek().Literal);

    private ExprAst ParseThrough()
    {
        var expr = ParseUnary();
        if (IsWord(_current, "through"))
        {
            Advance();
            var right = ParseUnary();
            expr = new ConstructExprAst("range", [expr, right], Combine(expr.Span, right.Span));
        }

        return expr;
    }

    private ExprAst? TryParseIs(ExprAst left)
    {
        if (IsWord(_current, "not") && (IsAt(_current + 1, TokenKind.EqualWord) || IsWord(_current + 1, "equal")))
        {
            return null;
        }

        if (IsWord(_current, "nothing", "null"))
        {
            var token = Advance();
            return new BinaryExprAst(left, TokenKind.EqualEqual, new LiteralExprAst(null, token.Span), Combine(left.Span, token.Span));
        }

        if (IsWord(_current, "not") && IsWord(_current + 1, "nothing", "null"))
        {
            Advance();
            var token = Advance();
            return new BinaryExprAst(left, TokenKind.BangEqual, new LiteralExprAst(null, token.Span), Combine(left.Span, token.Span));
        }

        if (IsWord(_current, "like") || (IsWord(_current, "not") && IsWord(_current + 1, "like")))
        {
            var negated = IsWord(_current, "not");
            if (negated)
            {
                Advance();
            }

            Advance();
            var right = ParseSum();
            return new ConstructExprAst(negated ? "notlike" : "like", [left, right], Combine(left.Span, right.Span));
        }

        if (Check(TokenKind.In) || (IsWord(_current, "not") && CheckNext(TokenKind.In)))
        {
            var negated = IsWord(_current, "not");
            if (negated)
            {
                Advance();
            }

            Advance();
            var right = ParseSum();
            return new ConstructExprAst(negated ? "notin" : "in", [left, right], Combine(left.Span, right.Span));
        }

        if (LooksLikeTypeTest())
        {
            var negated = IsWord(_current, "not");
            if (negated)
            {
                Advance();
            }

            Match(TokenKind.A, TokenKind.An);
            var type = ReadTypeName();
            return new ConstructExprAst(negated ? "isnot" : "is", [left, new LiteralExprAst(type, Previous().Span)], Combine(left.Span, Previous().Span));
        }

        return null;
    }

    private bool LooksLikeTypeTest()
    {
        var index = _current;
        if (IsWord(index, "not"))
        {
            index++;
        }

        if (IsAt(index, TokenKind.A) || IsAt(index, TokenKind.An))
        {
            index++;
        }
        else
        {
            return false;
        }

        return IsAt(index, TokenKind.String) || IsAt(index, TokenKind.Integer) || IsAt(index, TokenKind.Decimal)
            || IsAt(index, TokenKind.Boolean) || IsAt(index, TokenKind.List) || IsAt(index, TokenKind.Identifier);
    }

    private string ReadTypeName()
    {
        if (Match(TokenKind.String))
        {
            return "string";
        }

        if (Match(TokenKind.Integer))
        {
            return "integer";
        }

        if (Match(TokenKind.Decimal))
        {
            return "decimal";
        }

        if (Match(TokenKind.Boolean))
        {
            return "boolean";
        }

        if (Check(TokenKind.List))
        {
            return ParseType() switch
            {
                NamedTypeAst named => named.Name,
                ListTypeAst => "list",
                _ => "object"
            };
        }

        return Advance().Text;
    }

    private ExprAst ParseConstructInfix(ExprAst expr)
    {
        while (true)
        {
            if (Check(TokenKind.When) && !IsWord(_current + 1, "run"))
            {
                Advance();
                var condition = ParseAnd();
                Consume(TokenKind.Otherwise, "Expected 'otherwise'.");
                var other = ParseAnd();
                expr = new ConstructExprAst("ternary", [expr, condition, other], Combine(expr.Span, other.Span));
                continue;
            }

            if (IsWord(_current, "contains") || (IsWord(_current, "does") && IsWord(_current + 1, "not") && IsWord(_current + 2, "contain")))
            {
                var negated = IsWord(_current, "does");
                Advance();
                if (negated)
                {
                    Advance();
                    Advance();
                }

                var right = ParseSum();
                expr = new ConstructExprAst(negated ? "notcontains" : "contains", [expr, right], Combine(expr.Span, right.Span));
                continue;
            }

            if (IsWord(_current, "matches") || (IsWord(_current, "does") && IsWord(_current + 1, "not") && IsWord(_current + 2, "match")))
            {
                var negated = IsWord(_current, "does");
                Advance();
                if (negated)
                {
                    Advance();
                    Advance();
                }

                var right = ParseSum();
                expr = new ConstructExprAst(negated ? "notmatch" : "match", [expr, right], Combine(expr.Span, right.Span));
                continue;
            }

            if (IsWord(_current, "joined"))
            {
                Advance();
                if (IsWord(_current, "by"))
                {
                    Advance();
                    var right = ParseSum();
                    expr = new ConstructExprAst("join", [expr, right], Combine(expr.Span, right.Span));
                }
                else
                {
                    expr = new ConstructExprAst("join", [expr], Combine(expr.Span, Previous().Span));
                }

                continue;
            }

            if (IsWord(_current, "split") && IsWord(_current + 1, "on"))
            {
                Advance();
                Advance();
                var right = ParseSum();
                expr = new ConstructExprAst("split", [expr, right], Combine(expr.Span, right.Span));
                continue;
            }

            if (IsWord(_current, "formatted") && IsWord(_current + 1, "with"))
            {
                Advance();
                Advance();
                var args = new List<ExprAst> { expr, ParseComparison() };
                while (Match(TokenKind.And))
                {
                    args.Add(ParseComparison());
                }

                expr = new ConstructExprAst("format", args, Combine(expr.Span, args[^1].Span));
                continue;
            }

            if (IsWord(_current, "piped") && IsWord(_current + 1, "to"))
            {
                Advance();
                Advance();
                var right = ParseComparison();
                expr = new ConstructExprAst("pipe", [expr, right], Combine(expr.Span, right.Span));
                continue;
            }

            if (IsWord(_current, "replacing"))
            {
                Advance();
                var pattern = ParseSum();
                Consume(TokenKind.With, "Expected 'with'.");
                var replacement = ParseSum();
                expr = new ConstructExprAst("replace", [expr, pattern, replacement], Combine(expr.Span, replacement.Span));
                continue;
            }

            if (IsWord(_current, "bitwise") && IsWord(_current + 1, "and", "or", "xor"))
            {
                Advance();
                var op = Advance().Text.ToLowerInvariant() switch
                {
                    "or" => "bor",
                    "xor" => "bxor",
                    _ => "band"
                };
                var right = ParseSum();
                expr = new ConstructExprAst(op, [expr, right], Combine(expr.Span, right.Span));
                continue;
            }

            if (IsWord(_current, "shifted") && IsWord(_current + 1, "left", "right"))
            {
                Advance();
                var op = IsWord(_current, "left") ? "shl" : "shr";
                Advance();
                if (IsWord(_current, "by"))
                {
                    Advance();
                }

                var right = ParseSum();
                expr = new ConstructExprAst(op, [expr, right], Combine(expr.Span, right.Span));
                continue;
            }

            break;
        }

        return expr;
    }

    private ExprAst? TryParseConstructNoun(SourceSpan start, bool article)
    {
        var mark = _current;
        var expr = ParseConstructNoun(start, article);
        if (expr is null)
        {
            _current = mark;
        }

        return expr;
    }

    private ExprAst? ParseConstructNoun(SourceSpan start, bool article)
    {
        if (article && IsWord(_current, "static"))
        {
            Advance();
            var member = ReadWords();
            if (member is null || !Match(TokenKind.Of) || !TryReadTypeName(out var typeName, out var typeSpan))
            {
                return null;
            }

            var args = new List<ExprAst> { new LiteralExprAst(typeName, typeSpan), new LiteralExprAst(member.Value.Raw, member.Value.Span) };
            if (Match(TokenKind.With))
            {
                args.Add(ParseComparison());
                while (Match(TokenKind.And))
                {
                    args.Add(ParseComparison());
                }
            }

            return new ConstructExprAst("static", args, Combine(start, args[^1].Span));
        }

        if (article && (IsWord(_current, "table") || (IsWord(_current, "ordered") && IsWord(_current + 1, "table"))))
        {
            var ordered = IsWord(_current, "ordered");
            if (ordered)
            {
                Advance();
            }

            if (!IsWord(_current, "table") || !IsWord(_current + 1, "of"))
            {
                return null;
            }

            Advance();
            Advance();
            var pairs = new List<ExprAst> { new LiteralExprAst(ordered, start) };
            do
            {
                Match(TokenKind.And);
                var key = ReadWords();
                if (key is null || !Match(TokenKind.Set) || !Match(TokenKind.To))
                {
                    return null;
                }

                var value = ParseComparison();
                pairs.Add(new LiteralExprAst(key.Value.Raw, key.Value.Span));
                pairs.Add(value);
            }
            while (Check(TokenKind.And) || Check(TokenKind.Comma) && Match(TokenKind.Comma));

            return new ConstructExprAst("hash", pairs, Combine(start, pairs[^1].Span));
        }

        if (article && IsWord(_current, "block") && IsWord(_current + 1, "of"))
        {
            Advance();
            Advance();
            var body = ParseComparison();
            var closed = IsWord(_current, "closed");
            if (closed)
            {
                Advance();
            }

            return new ConstructExprAst("block", [body, new LiteralExprAst(closed, body.Span)], Combine(start, Previous().Span));
        }

        if (article && ((IsWord(_current, "collected") && IsWord(_current + 1, "output")) || IsWord(_current, "output"))
            && (IsWord(_current + 1, "of") || IsWord(_current + 2, "of")))
        {
            var collected = IsWord(_current, "collected");
            Advance();
            if (collected)
            {
                Advance();
            }

            if (!Match(TokenKind.Of))
            {
                return null;
            }

            var inner = ParseComparison();
            return new ConstructExprAst(collected ? "collected" : "output", [inner], Combine(start, inner.Span));
        }

        if (article && IsWord(_current, "character") && CheckNext(TokenKind.StringLiteral))
        {
            Advance();
            var text = Advance();
            return new ConstructExprAst("cast", [new LiteralExprAst(text.Literal, text.Span), new LiteralExprAst("character", text.Span)], Combine(start, text.Span));
        }

        if (article && IsWord(_current, "item") && IsWord(_current + 1, "at"))
        {
            Advance();
            Advance();
            var index = ParseComparison();
            Consume(TokenKind.Of, "Expected 'of'.");
            var target = ParseComparison();
            return new ConstructExprAst("index", [target, index], Combine(start, target.Span));
        }

        if (article && IsWord(_current, "last") && IsWord(_current + 1, "item") && IsWord(_current + 2, "of"))
        {
            Advance();
            Advance();
            Advance();
            var target = ParseComparison();
            return new ConstructExprAst("index", [target, new LiteralExprAst(-1L, start)], Combine(start, target.Span));
        }

        if (article && IsWord(_current, "items") && IsWord(_current + 1, "from"))
        {
            Advance();
            Advance();
            var from = ParseComparison();
            Consume(TokenKind.To, "Expected 'to'.");
            var to = ParseComparison();
            Consume(TokenKind.Of, "Expected 'of'.");
            var target = ParseComparison();
            return new ConstructExprAst("slice", [target, from, to], Combine(start, target.Span));
        }

        if (article && IsWord(_current, "grid") && IsWord(_current + 1, "of") && IsAt(_current + 2, TokenKind.IntegerLiteral))
        {
            Advance();
            Advance();
            var rows = Advance();
            if (!IsWord(_current, "by"))
            {
                return null;
            }

            Advance();
            var columns = Check(TokenKind.IntegerLiteral) ? Advance() : rows;
            if (IsWord(_current, "integers", "integer"))
            {
                Advance();
            }

            return new ConstructExprAst("grid", [
                new LiteralExprAst(rows.Literal, rows.Span),
                new LiteralExprAst(columns.Literal, columns.Span)
            ], Combine(start, Previous().Span));
        }

        if (!article && IsWord(_current, "whether") && IsWord(_current + 1, "it") && IsWord(_current + 2, "succeeded"))
        {
            Advance();
            Advance();
            var end = Advance().Span;
            return new ConstructExprAst("auto", [new LiteralExprAst("succeeded", end)], Combine(start, end));
        }

        if (article && IsWord(_current, "pipeline") && IsWord(_current + 1, "item"))
        {
            Advance();
            var end = Advance().Span;
            return new ConstructExprAst("auto", [new LiteralExprAst("pipelineItem", end)], Combine(start, end));
        }

        if (article && IsWord(_current, "arguments") && !IsAt(_current + 1, TokenKind.Identifier))
        {
            var end = Advance().Span;
            return new ConstructExprAst("auto", [new LiteralExprAst("arguments", end)], Combine(start, end));
        }

        if (article && IsWord(_current, "errors") && !IsAt(_current + 1, TokenKind.Identifier))
        {
            var end = Advance().Span;
            return new ConstructExprAst("auto", [new LiteralExprAst("errors", end)], Combine(start, end));
        }

        if (article && (IsWord(_current, "global", "local", "private")
                || (IsWord(_current, "script") && !IsWord(_current + 1, "folder", "directory")))
            && IsAt(_current + 1, TokenKind.Identifier))
        {
            var scope = Advance().Text.ToLowerInvariant();
            var name = ReadWords();
            if (name is null)
            {
                return null;
            }

            return new ConstructExprAst("scope", [
                new LiteralExprAst(scope, start),
                new LiteralExprAst(name.Value.Camel, name.Value.Span)
            ], Combine(start, name.Value.Span));
        }

        return null;
    }

    private ExprAst ApplyCalling(ExprAst expr)
    {
        while (IsWord(_current, "calling"))
        {
            Advance();
            var member = ReadFieldWord();
            var args = new List<ExprAst> { expr, new LiteralExprAst(member, Previous().Span) };
            if (Match(TokenKind.With))
            {
                args.Add(ParseComparison());
                while (Match(TokenKind.And))
                {
                    args.Add(ParseComparison());
                }
            }

            expr = new ConstructExprAst("member", args, Combine(expr.Span, args[^1].Span));
        }

        if (Check(TokenKind.When) && IsWord(_current + 1, "present"))
        {
            Advance();
            Advance();
            expr = new ConstructExprAst("optional", [expr], Combine(expr.Span, Previous().Span));
        }

        if (IsWord(_current, "spread", "splatted"))
        {
            Advance();
            expr = new ConstructExprAst("splat", [expr], Combine(expr.Span, Previous().Span));
        }

        return expr;
    }

    private ExprAst ApplySoftCast(ExprAst expr)
    {
        if (!IsWord(_current, "if") || !IsWord(_current + 1, "possible"))
        {
            return expr;
        }

        Advance();
        Advance();
        return expr switch
        {
            CallExprAst { Name: "str" } => Soft("string", expr),
            CallExprAst { Name: "int" } => Soft("integer", expr),
            CallExprAst { Name: "decimal" } => Soft("decimal", expr),
            CallExprAst { Name: "bool" } => Soft("boolean", expr),
            CallExprAst { Name: "json" } => Soft("object", expr),
            TreatExprAst treat => new ConstructExprAst("as", [treat.Target, new LiteralExprAst(TypeText(treat.Type), treat.Span)], treat.Span),
            _ => expr
        };

        ExprAst Soft(string type, ExprAst call) =>
            new ConstructExprAst("as", [((CallExprAst)call).Arguments[0], new LiteralExprAst(type, call.Span)], Combine(call.Span, Previous().Span));
    }

    private static string TypeText(TypeAst type) => type switch
    {
        NamedTypeAst named => named.Name,
        ListTypeAst list => "list:" + TypeText(list.ElementType),
        _ => "object"
    };

    private TypeAst FinishCompositeType(Token name)
    {
        var kind = name.Text.ToLowerInvariant();
        if (kind == "dictionary")
        {
            Consume(TokenKind.Of, "Expected 'of'.");
            var key = ParseType();
            Consume(TokenKind.To, "Expected 'to'.");
            var value = ParseType();
            return new NamedTypeAst($"dictionary:{TypeText(key)}:{TypeText(value)}", Combine(name.Span, value.Span));
        }

        if (kind == "grid")
        {
            Consume(TokenKind.Of, "Expected 'of'.");
            var element = ParseType();
            return new NamedTypeAst($"grid:{TypeText(element)}", Combine(name.Span, element.Span));
        }

        if (IsWord(_current, "to"))
        {
            Advance();
        }

        var target = ParseType();
        return new NamedTypeAst($"ref:{TypeText(target)}", Combine(name.Span, target.Span));
    }

    private ExprAst FinishLiteral(LiteralExprAst literal)
    {
        if (literal.Value is long number && IsWord(_current, "kb", "mb", "gb", "tb", "pb"))
        {
            var unit = Advance().Text.ToLowerInvariant();
            var factor = unit switch
            {
                "kb" => 1024L,
                "mb" => 1024L * 1024,
                "gb" => 1024L * 1024 * 1024,
                "tb" => 1024L * 1024 * 1024 * 1024,
                _ => 1024L * 1024 * 1024 * 1024 * 1024
            };
            return new LiteralExprAst(number * factor, Combine(literal.Span, Previous().Span));
        }

        return literal;
    }

    private bool TryReadTypeName(out string name, out SourceSpan span)
    {
        if (Check(TokenKind.Identifier) || Check(TokenKind.String) || Check(TokenKind.Integer)
            || Check(TokenKind.Decimal) || Check(TokenKind.Boolean))
        {
            var token = Advance();
            name = token.Text;
            span = token.Span;
            return true;
        }

        name = "";
        span = default;
        return false;
    }

    private bool IsWord(int index, params string[] words) =>
        index < _tokens.Count
        && words.Any(word => _tokens[index].Text.Equals(word, StringComparison.OrdinalIgnoreCase));

    private static bool IsPrimitiveName(string text) =>
        text.Equals("character", StringComparison.OrdinalIgnoreCase)
        || text.Equals("char", StringComparison.OrdinalIgnoreCase)
        || text.Equals("float", StringComparison.OrdinalIgnoreCase)
        || text.Equals("double", StringComparison.OrdinalIgnoreCase)
        || text.Equals("number", StringComparison.OrdinalIgnoreCase)
        || text.Equals("pattern", StringComparison.OrdinalIgnoreCase)
        || text.Equals("regex", StringComparison.OrdinalIgnoreCase)
        || text.Equals("xml", StringComparison.OrdinalIgnoreCase)
        || text.Equals("object", StringComparison.OrdinalIgnoreCase)
        || text.Equals("table", StringComparison.OrdinalIgnoreCase)
        || text.Equals("hashtable", StringComparison.OrdinalIgnoreCase)
        || text.Equals("block", StringComparison.OrdinalIgnoreCase)
        || text.Equals("scriptblock", StringComparison.OrdinalIgnoreCase);

    private bool IsCallCommand() =>
        IsWord(_current, "call") && IsAt(_current + 1, TokenKind.The) && IsWord(_current + 2, "command");

    private ExprAst ParseCallCommand()
    {
        var start = Advance().Span;
        Advance();
        Advance();
        ExprAst command;
        if (Check(TokenKind.StringLiteral))
        {
            var token = Advance();
            command = new LiteralExprAst(token.Literal, token.Span);
        }
        else
        {
            var name = ParseName();
            command = new LiteralExprAst(name.Raw, name.Span);
        }

        return new ConstructExprAst("call", [command], Combine(start, command.Span));
    }
}
