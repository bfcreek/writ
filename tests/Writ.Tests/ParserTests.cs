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
}
