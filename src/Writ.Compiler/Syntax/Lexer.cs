using Writ.Compiler.Diagnostics;

namespace Writ.Compiler.Syntax;

public sealed class Lexer
{
    private static readonly Dictionary<string, TokenKind> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["if"] = TokenKind.If,
        ["otherwise"] = TokenKind.Otherwise,
        ["for"] = TokenKind.For,
        ["each"] = TokenKind.Each,
        ["in"] = TokenKind.In,
        ["return"] = TokenKind.Return,
        ["give"] = TokenKind.Give,
        ["back"] = TokenKind.Back,
        ["to"] = TokenKind.To,
        ["an"] = TokenKind.An,
        ["a"] = TokenKind.A,
        ["the"] = TokenKind.The,
        ["has"] = TokenKind.Has,
        ["which"] = TokenKind.Which,
        ["is"] = TokenKind.Is,
        ["are"] = TokenKind.Are,
        ["given"] = TokenKind.Given,
        ["returning"] = TokenKind.Returning,
        ["let"] = TokenKind.Let,
        ["be"] = TokenKind.Be,
        ["of"] = TokenKind.Of,
        ["where"] = TokenKind.Where,
        ["with"] = TokenKind.With,
        ["added"] = TokenKind.Added,
        ["as"] = TokenKind.As,
        ["sum"] = TokenKind.Sum,
        ["length"] = TokenKind.Length,
        ["empty"] = TokenKind.Empty,
        ["list"] = TokenKind.List,
        ["containing"] = TokenKind.Containing,
        ["rounded"] = TokenKind.Rounded,
        ["places"] = TokenKind.Places,
        ["divided"] = TokenKind.Divided,
        ["by"] = TokenKind.By,
        ["modulo"] = TokenKind.Modulo,
        ["equal"] = TokenKind.EqualWord,
        ["than"] = TokenKind.Than,
        ["at"] = TokenKind.At,
        ["least"] = TokenKind.Least,
        ["most"] = TokenKind.Most,
        ["sorted"] = TokenKind.Sorted,
        ["first"] = TokenKind.First,
        ["count"] = TokenKind.Count,
        ["say"] = TokenKind.Say,
        ["try"] = TokenKind.Try,
        ["finally"] = TokenKind.Finally,
        ["while"] = TokenKind.While,
        ["until"] = TokenKind.Until,
        ["largest"] = TokenKind.Largest,
        ["smallest"] = TokenKind.Smallest,
        ["when"] = TokenKind.When,
        ["allow"] = TokenKind.Allow,
        ["use"] = TokenKind.Use,
        ["from"] = TokenKind.From,
        ["ask"] = TokenKind.Ask,
        ["set"] = TokenKind.Set,
        ["integer"] = TokenKind.Integer,
        ["decimal"] = TokenKind.Decimal,
        ["string"] = TokenKind.String,
        ["boolean"] = TokenKind.Boolean,
        ["plus"] = TokenKind.Plus,
        ["minus"] = TokenKind.Minus,
        ["times"] = TokenKind.Star,
        ["greater"] = TokenKind.Greater,
        ["less"] = TokenKind.Less,
        ["and"] = TokenKind.And,
        ["or"] = TokenKind.Or,
        ["not"] = TokenKind.Not,
        ["true"] = TokenKind.True,
        ["false"] = TokenKind.False,
    };

    private readonly string _source;
    private readonly string? _filePath;
    private readonly DiagnosticBag _diagnostics;
    private int _index;
    private int _line = 1;
    private int _column = 1;
    private readonly Stack<int> _indents = new();
    private int _pendingDedents;
    private bool _atLineStart = true;

    public Lexer(string source, string? filePath, DiagnosticBag diagnostics)
    {
        _source = source.Replace("\r\n", "\n").Replace('\r', '\n');
        _filePath = filePath;
        _diagnostics = diagnostics;
        _indents.Push(0);
    }

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            var token = NextToken();
            tokens.Add(token);
            if (token.Kind == TokenKind.EndOfFile)
            {
                break;
            }
        }

        return tokens;
    }

    private Token NextToken()
    {
        if (_pendingDedents > 0)
        {
            _pendingDedents--;
            return Make(TokenKind.Dedent, "", _line, _column, 0);
        }

        if (_index >= _source.Length)
        {
            if (_indents.Count > 1)
            {
                _indents.Pop();
                return Make(TokenKind.Dedent, "", _line, _column, 0);
            }

            return Make(TokenKind.EndOfFile, "", _line, _column, 0);
        }

        if (_atLineStart)
        {
            _atLineStart = false;
            var indentToken = HandleIndentation();
            if (indentToken is not null)
            {
                return indentToken;
            }
        }

        SkipSpacesAndComments();

        if (_index >= _source.Length)
        {
            return NextToken();
        }

        var ch = Peek();
        if (ch == '\n')
        {
            var line = _line;
            var col = _column;
            Advance();
            _atLineStart = true;
            return Make(TokenKind.NewLine, "\n", line, col, 1);
        }

        if (char.IsLetter(ch) || ch == '_')
        {
            return ReadIdentifierOrKeyword();
        }

        if (char.IsDigit(ch))
        {
            return ReadNumber();
        }

        if (ch == '"')
        {
            return ReadString();
        }

        if (ch == '@' && _index + 1 < _source.Length && (_source[_index + 1] == '"' || _source[_index + 1] == '\''))
        {
            return ReadHereString();
        }

        return ReadOperatorOrPunctuation();
    }

    private Token? HandleIndentation()
    {
        var startIndex = _index;
        var startCol = _column;
        var indent = 0;

        while (_index < _source.Length)
        {
            var ch = Peek();
            if (ch == ' ')
            {
                indent++;
                Advance();
            }
            else if (ch == '\t')
            {
                _diagnostics.Error("E1001", "Tabs are not allowed for indentation; use spaces.", _filePath,
                    SourceSpan.FromToken(_line, _column, 1));
                Advance();
                indent += 4;
            }
            else
            {
                break;
            }
        }

        // Blank line or comment-only line: ignore indentation.
        if (_index < _source.Length && (Peek() == '\n' || Peek() == '#' || IsBlockCommentStart()))
        {
            // Rewind spaces so SkipSpacesAndComments / newline handling can proceed normally
            // after we consume nothing for indent purposes. Actually we've already advanced.
            // For blank lines, just let the rest of the lexer handle newline/comment.
            // Reset indent handling: don't emit indent/dedent for blank lines.
            // We've consumed leading spaces — that's fine for blank/comment lines.
            return null;
        }

        var current = _indents.Peek();
        if (indent == current)
        {
            return null;
        }

        if (indent > current)
        {
            _indents.Push(indent);
            return Make(TokenKind.Indent, new string(' ', indent), _line, startCol, indent);
        }

        // Dedent one or more levels
        while (_indents.Count > 1 && _indents.Peek() > indent)
        {
            _indents.Pop();
            _pendingDedents++;
        }

        if (_indents.Peek() != indent)
        {
            _diagnostics.Error("E1002", $"Inconsistent indentation of {indent} spaces.", _filePath,
                SourceSpan.FromToken(_line, startCol, Math.Max(indent, 1)));
        }

        if (_pendingDedents > 0)
        {
            _pendingDedents--;
            return Make(TokenKind.Dedent, "", _line, startCol, 0);
        }

        _ = startIndex;
        return null;
    }

    private void SkipSpacesAndComments()
    {
        while (_index < _source.Length)
        {
            var ch = Peek();
            if (ch == ' ' || ch == '\t')
            {
                Advance();
                continue;
            }

            if (ch == '#' || IsBlockCommentStart())
            {
                if (ch == '#')
                {
                    while (_index < _source.Length && Peek() != '\n')
                    {
                        Advance();
                    }
                }
                else
                {
                    SkipBlockComment();
                }

                continue;
            }

            break;
        }
    }

    private Token ReadIdentifierOrKeyword()
    {
        var startLine = _line;
        var startCol = _column;
        var start = _index;
        while (_index < _source.Length && (char.IsLetterOrDigit(Peek()) || Peek() == '_'))
        {
            Advance();
        }

        var text = _source[start.._index];
        if (Keywords.TryGetValue(text, out var kind))
        {
            object? literal = kind switch
            {
                TokenKind.True => true,
                TokenKind.False => false,
                _ => null
            };
            return Make(kind, text, startLine, startCol, text.Length, literal);
        }

        return Make(TokenKind.Identifier, text, startLine, startCol, text.Length);
    }

    private Token ReadNumber()
    {
        var startLine = _line;
        var startCol = _column;
        var start = _index;
        while (_index < _source.Length && char.IsDigit(Peek()))
        {
            Advance();
        }

        if (_index < _source.Length && Peek() == '.' && _index + 1 < _source.Length && char.IsDigit(_source[_index + 1]))
        {
            Advance(); // '.'
            while (_index < _source.Length && char.IsDigit(Peek()))
            {
                Advance();
            }

            var text = _source[start.._index];
            return Make(TokenKind.DecimalLiteral, text, startLine, startCol, text.Length, decimal.Parse(text));
        }

        var intText = _source[start.._index];
        var number = long.Parse(intText);
        if (TryReadSuffix(out var suffix))
        {
            if (suffix is "d")
            {
                return Make(TokenKind.DecimalLiteral, intText + suffix, startLine, startCol, intText.Length + 1, (decimal)number);
            }

            if (suffix is "l")
            {
                return Make(TokenKind.IntegerLiteral, intText + suffix, startLine, startCol, intText.Length + 1, number);
            }

            number = Scale(number, suffix);
            var text = _source[start.._index];
            return Make(TokenKind.IntegerLiteral, text, startLine, startCol, text.Length, number);
        }

        return Make(TokenKind.IntegerLiteral, intText, startLine, startCol, intText.Length, number);
    }

    private Token ReadString()
    {
        var startLine = _line;
        var startCol = _column;
        Advance(); // opening "
        var sb = new System.Text.StringBuilder();
        while (_index < _source.Length && Peek() != '"' && Peek() != '\n')
        {
            if (Peek() == '\\' || Peek() == '`')
            {
                var mark = Peek();
                Advance();
                if (_index >= _source.Length)
                {
                    break;
                }

                sb.Append(mark == '`' ? ExpandBacktick(Peek()) : Peek() switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '"' => '"',
                    '\\' => '\\',
                    var c => c
                });
                Advance();
            }
            else
            {
                sb.Append(Peek());
                Advance();
            }
        }

        if (_index >= _source.Length || Peek() != '"')
        {
            _diagnostics.Error("E1003", "Unterminated string literal.", _filePath,
                SourceSpan.FromToken(startLine, startCol, 1));
            return Make(TokenKind.StringLiteral, sb.ToString(), startLine, startCol, _column - startCol, sb.ToString());
        }

        Advance(); // closing "
        var value = sb.ToString();
        return Make(TokenKind.StringLiteral, value, startLine, startCol, _column - startCol, value);
    }

    private Token ReadOperatorOrPunctuation()
    {
        var startLine = _line;
        var startCol = _column;
        var ch = Peek();
        Advance();

        TokenKind kind;
        var text = ch.ToString();

        switch (ch)
        {
            case '+': kind = TokenKind.Plus; break;
            case '-':
                if (Match('>'))
                {
                    kind = TokenKind.Arrow;
                    text = "->";
                }
                else
                {
                    kind = TokenKind.Minus;
                }
                break;
            case '*': kind = TokenKind.Star; break;
            case '/': kind = TokenKind.Slash; break;
            case '%': kind = TokenKind.Percent; break;
            case '=':
                if (Match('='))
                {
                    kind = TokenKind.EqualEqual;
                    text = "==";
                }
                else
                {
                    kind = TokenKind.Equal;
                }
                break;
            case '!':
                if (Match('='))
                {
                    kind = TokenKind.BangEqual;
                    text = "!=";
                }
                else
                {
                    _diagnostics.Error("E1004", $"Unexpected character '{ch}'.", _filePath,
                        SourceSpan.FromToken(startLine, startCol, 1));
                    kind = TokenKind.BangEqual;
                    text = "!";
                }
                break;
            case '<':
                if (Match('='))
                {
                    kind = TokenKind.LessEqual;
                    text = "<=";
                }
                else
                {
                    kind = TokenKind.Less;
                }
                break;
            case '>':
                if (Match('='))
                {
                    kind = TokenKind.GreaterEqual;
                    text = ">=";
                }
                else
                {
                    kind = TokenKind.Greater;
                }
                break;
            case '(': kind = TokenKind.LeftParen; break;
            case ')': kind = TokenKind.RightParen; break;
            case '[': kind = TokenKind.LeftBracket; break;
            case ']': kind = TokenKind.RightBracket; break;
            case '{': kind = TokenKind.LeftBrace; break;
            case '}': kind = TokenKind.RightBrace; break;
            case ',': kind = TokenKind.Comma; break;
            case ':': kind = TokenKind.Colon; break;
            case '.': kind = TokenKind.Dot; break;
            default:
                _diagnostics.Error("E1004", $"Unexpected character '{ch}'.", _filePath,
                    SourceSpan.FromToken(startLine, startCol, 1));
                kind = TokenKind.Identifier;
                break;
        }

        return Make(kind, text, startLine, startCol, text.Length);
    }

    private bool Match(char expected)
    {
        if (_index < _source.Length && Peek() == expected)
        {
            Advance();
            return true;
        }

        return false;
    }

    private char Peek() => _source[_index];

    private void Advance()
    {
        if (_index < _source.Length)
        {
            if (_source[_index] == '\n')
            {
                _line++;
                _column = 1;
            }
            else
            {
                _column++;
            }

            _index++;
        }
    }

    private static Token Make(TokenKind kind, string text, int line, int column, int length, object? literal = null) =>
        new(kind, text, SourceSpan.FromToken(line, column, Math.Max(length, 1)), literal);

    private bool IsBlockCommentStart() =>
        _index + 1 < _source.Length && Peek() == '<' && _source[_index + 1] == '#';

    private void SkipBlockComment()
    {
        var startLine = _line;
        var startCol = _column;
        Advance(); // <
        Advance(); // #
        while (_index + 1 < _source.Length && !(Peek() == '#' && _source[_index + 1] == '>'))
        {
            Advance();
        }

        if (_index + 1 >= _source.Length)
        {
            _diagnostics.Error("E1003", "Unterminated block comment.", _filePath,
                SourceSpan.FromToken(startLine, startCol, 2));
            return;
        }

        Advance();
        Advance();
    }

    private bool TryReadSuffix(out string suffix)
    {
        suffix = "";
        if (_index >= _source.Length || !char.IsLetter(Peek()))
        {
            return false;
        }

        var end = _index;
        while (end < _source.Length && char.IsLetter(_source[end]))
        {
            end++;
        }

        var text = _source[_index..end].ToLowerInvariant();
        if (text is not ("kb" or "mb" or "gb" or "tb" or "pb" or "d" or "l"))
        {
            return false;
        }

        while (_index < end)
        {
            Advance();
        }

        suffix = text;
        return true;
    }

    private static long Scale(long number, string suffix)
    {
        var factor = suffix switch
        {
            "kb" => 1024L,
            "mb" => 1024L * 1024,
            "gb" => 1024L * 1024 * 1024,
            "tb" => 1024L * 1024 * 1024 * 1024,
            _ => 1024L * 1024 * 1024 * 1024 * 1024
        };
        return number * factor;
    }

    private static char ExpandBacktick(char ch) => ch switch
    {
        'n' => '\n',
        't' => '\t',
        'r' => '\r',
        'a' => '\a',
        'b' => '\b',
        'f' => '\f',
        'v' => '\v',
        '0' => '\0',
        var c => c
    };

    private Token ReadHereString()
    {
        var startLine = _line;
        var startCol = _column;
        var expandable = _source[_index + 1] == '"';
        var quote = expandable ? '"' : '\'';
        Advance(); // @
        Advance(); // quote
        if (_index >= _source.Length || Peek() != '\n')
        {
            _diagnostics.Error("E1003", "A here-string must break the line after the opening quote.", _filePath,
                SourceSpan.FromToken(startLine, startCol, 2));
        }
        else
        {
            Advance();
        }

        var sb = new System.Text.StringBuilder();
        while (_index < _source.Length)
        {
            if (_column == 1 && _index + 1 < _source.Length && Peek() == quote && _source[_index + 1] == '@')
            {
                Advance();
                Advance();
                if (sb.Length > 0 && sb[^1] == '\n')
                {
                    sb.Length--;
                }

                var value = sb.ToString();
                return Make(TokenKind.StringLiteral, value, startLine, startCol, value.Length, value);
            }

            sb.Append(Peek());
            Advance();
        }

        _diagnostics.Error("E1003", "Unterminated here-string.", _filePath,
            SourceSpan.FromToken(startLine, startCol, 2));
        return Make(TokenKind.StringLiteral, sb.ToString(), startLine, startCol, 2, sb.ToString());
    }
}
