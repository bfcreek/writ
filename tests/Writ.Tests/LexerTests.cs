using Writ.Compiler.Diagnostics;
using Writ.Compiler.Syntax;

namespace Writ.Tests;

public class LexerTests
{
    [Fact]
    public void Emits_indent_and_dedent_for_blocks()
    {
        var source = """
            To Foo, returning an integer
                return 1
            """;
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(source, "test.writ", diagnostics).Tokenize();

        Assert.False(diagnostics.HasErrors);
        Assert.Contains(tokens, t => t.Kind == TokenKind.Indent);
        Assert.Contains(tokens, t => t.Kind == TokenKind.Dedent);
        Assert.Equal(TokenKind.EndOfFile, tokens[^1].Kind);
    }

    [Fact]
    public void Recognizes_english_keywords_and_literals()
    {
        var source = """
            the total is 1 plus 2.5
            the flag is true and false
            """;
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(source, "test.writ", diagnostics).Tokenize()
            .Where(t => t.Kind is not TokenKind.NewLine and not TokenKind.EndOfFile)
            .ToList();

        Assert.Contains(tokens, t => t.Kind == TokenKind.IntegerLiteral && (long)t.Literal! == 1);
        Assert.Contains(tokens, t => t.Kind == TokenKind.DecimalLiteral && (decimal)t.Literal! == 2.5m);
        Assert.Contains(tokens, t => t.Kind == TokenKind.Plus);
        Assert.Contains(tokens, t => t.Kind == TokenKind.And);
        Assert.Contains(tokens, t => t.Kind == TokenKind.True);
        Assert.Contains(tokens, t => t.Kind == TokenKind.Is);
    }
}
