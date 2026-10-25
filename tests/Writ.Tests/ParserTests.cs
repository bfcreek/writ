using Writ.Compiler.Diagnostics;
using Writ.Compiler.Syntax;

namespace Writ.Tests;

public class ParserTests
{
    [Fact]
    public void Parses_english_conditional()
    {
        var source = """
            To Classify, given n which is an integer, returning a string
                if n is equal to 0
                    return "zero"
                otherwise
                    return "other"
            """;
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(source, "t.writ", diagnostics).Tokenize();
        var ast = new Parser(tokens, "t.writ", diagnostics).ParseProgram();

        Assert.False(diagnostics.HasErrors, string.Join(Environment.NewLine, diagnostics.Items));
        var fn = Assert.IsType<FunctionAst>(Assert.Single(ast.Declarations));
        Assert.Equal("Classify", fn.Name);
        Assert.Equal("n", fn.Parameters[0].Name);
        var ifStmt = Assert.IsType<IfStmtAst>(Assert.Single(fn.Body));
        Assert.NotNull(ifStmt.OtherwiseBody);
    }

    [Fact]
    public void Parses_record_and_field_phrase()
    {
        var source = """
            An Item has
                a price which is a decimal
                and a quantity which is an integer

            To Price, given an item which is an Item, returning a decimal
                return the price of the item
            """;
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(source, "t.writ", diagnostics).Tokenize();
        var ast = new Parser(tokens, "t.writ", diagnostics).ParseProgram();

        Assert.False(diagnostics.HasErrors, string.Join(Environment.NewLine, diagnostics.Items));
        Assert.Equal(2, ast.Declarations.Count);
        var typeDecl = Assert.IsType<TypeDeclAst>(ast.Declarations[0]);
        Assert.Equal(2, typeDecl.Fields.Count);
        var fn = Assert.IsType<FunctionAst>(ast.Declarations[1]);
        var ret = Assert.IsType<ReturnStmtAst>(Assert.Single(fn.Body));
        Assert.IsType<OfExprAst>(ret.Value);
    }

    [Fact]
    public void A_file_with_no_trailing_newline_parses()
    {
        var source = "when run, display \"hello, world!\"";
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(source, "hello.writ", diagnostics).Tokenize();
        var ast = new Parser(tokens, "hello.writ", diagnostics).ParseProgram();

        Assert.False(diagnostics.HasErrors, string.Join(Environment.NewLine, diagnostics.Items));
        var entry = Assert.IsType<FunctionAst>(Assert.Single(ast.Declarations));
        Assert.True(entry.IsEntry);
        var say = Assert.IsType<SayStmtAst>(Assert.Single(entry.Body));
        var text = Assert.IsType<LiteralExprAst>(Assert.Single(say.Parts));
        Assert.Equal("hello, world!", text.Value);
    }

    [Fact]
    public void Sentences_in_the_file_are_the_script()
    {
        var source = """
            say "hello"
            say "there"
            """;
        var diagnostics = new DiagnosticBag();
        var ast = new Parser(new Lexer(source, "hello.writ", diagnostics).Tokenize(), "hello.writ", diagnostics).ParseProgram();

        Assert.False(diagnostics.HasErrors, string.Join(Environment.NewLine, diagnostics.Items));
        var entry = Assert.IsType<FunctionAst>(Assert.Single(ast.Declarations));
        Assert.True(entry.IsEntry);
        Assert.Empty(entry.Parameters);
        Assert.Equal(2, entry.Body.Count);
    }

    [Fact]
    public void A_sentence_and_a_When_run_block_cannot_both_be_the_script()
    {
        var source = """
            say "hello"

            When run
                say "there"
            """;
        var diagnostics = new DiagnosticBag();
        new Parser(new Lexer(source, "hello.writ", diagnostics).Tokenize(), "hello.writ", diagnostics).ParseProgram();

        Assert.Contains(diagnostics.Items, diagnostic => diagnostic.Code == "E1109");
    }
}
