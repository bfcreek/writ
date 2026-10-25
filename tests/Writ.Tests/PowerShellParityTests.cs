using Writ.Compiler.Compilation;
using Writ.Compiler.Execution;

namespace Writ.Tests;

public class PowerShellParityTests
{
    [Theory]
    [InlineData("basics/calculate-total.writ", "CalculateTotal", "basics/input-calculate-total.json")]
    [InlineData("basics/intent-sum.writ", "CalculateTotal", "basics/input-calculate-total.json")]
    [InlineData("basics/innate-total.writ", "CalculateTotal", "basics/input-calculate-total.json")]
    [InlineData("basics/intent-filter.writ", "PositiveQuantity", "basics/input-intent-filter.json")]
    [InlineData("basics/fizzbuzz-loop.writ", "ClassifyAll", "basics/input-fizzbuzz.json")]
    [InlineData("payments/recover-failures.writ", "RecoverFailures", "payments/input-recover.json")]
    [InlineData("payments/but-first.writ", "Recover", "payments/input-but-first.json")]
    public void PowerShell_runs_the_sample(string sample, string entry, string inputFile)
    {
        var path = TestPaths.Sample(sample);
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var input = File.ReadAllText(TestPaths.Sample(inputFile));
        var module = Path.GetFileNameWithoutExtension(sample);

        var pwsh = new PowerShellExecutor().Execute(result.GeneratedPowerShell!, module, entry, input);
        Assert.True(pwsh.Success, string.Join(Environment.NewLine, pwsh.Errors));
        Assert.NotNull(pwsh.Value);
    }

    [Fact]
    public void PowerShell_capability_calls_happen_only_at_run_and_in_order()
    {
        var path = TestPaths.Sample(Path.Combine("payments", "but-first.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var script = result.GeneratedPowerShell!;
        Assert.True(script.IndexOf("'list_failed'", StringComparison.Ordinal) < script.IndexOf("'retry'", StringComparison.Ordinal));

        var exec = new PowerShellExecutor().Execute(script, "but-first", "Recover", File.ReadAllText(TestPaths.Sample(Path.Combine("payments", "input-but-first.json"))));
        Assert.True(exec.Success, string.Join(Environment.NewLine, exec.Errors));
        Assert.Equal(2, exec.CapabilityCalls);
        Assert.True(Assert.IsType<bool>(exec.Value));
    }
}

internal static class TestPaths
{
    public static string Sample(string relative) => Path.GetFullPath(Path.Combine(RepoRoot(), "samples", relative));

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Writ.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root.");
    }
}
