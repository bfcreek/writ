using Writ.Compiler.Compilation;
using Writ.Compiler.Diagnostics;

namespace Writ.Tests;

public class BinderTests
{
    [Theory]
    [InlineData("undeclared-var.writ", "E2007")]
    [InlineData("type-mismatch.writ", "E2006")]
    [InlineData("bad-if-condition.writ", "E2004")]
    [InlineData("missing-return.writ", "E3001")]
    [InlineData("unknown-type.writ", "E2026")]
    [InlineData("bad-arity.writ", "E2009")]
    public void Negative_samples_produce_expected_diagnostics(string fileName, string code)
    {
        var path = SamplePath(Path.Combine("errors", fileName));
        var source = File.ReadAllText(path);
        var result = new CompilationPipeline().Compile(source, path);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == code);
        Assert.All(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error),
            d =>
            {
                Assert.True(d.Span.StartLine >= 1);
                Assert.False(string.IsNullOrEmpty(d.FilePath));
            });
    }

    private static string SamplePath(string relative) =>
        Path.GetFullPath(Path.Combine(RepoRoot(), "samples", relative));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Writ.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root.");
    }
}
