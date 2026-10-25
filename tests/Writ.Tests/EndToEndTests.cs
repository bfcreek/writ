using System.Text.Json;
using Writ.Compiler.Compilation;
using Writ.Compiler.Execution;

namespace Writ.Tests;

public class EndToEndTests
{
    [Fact]
    public void CalculateTotal_compiles_and_runs()
    {
        var path = SamplePath("calculate-total.writ");
        var source = File.ReadAllText(path);
        var pipeline = new CompilationPipeline();
        var result = pipeline.Compile(source, path);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.False(string.IsNullOrWhiteSpace(result.GeneratedPowerShell));
        Assert.Contains("CalculateTotal", result.GeneratedPowerShell!);

        var input = File.ReadAllText(SamplePath("input-calculate-total.json"));
        var exec = new PowerShellExecutor().Execute(result.GeneratedPowerShell!, "calculate-total", "CalculateTotal", input);
        Assert.True(exec.Success, string.Join(Environment.NewLine, exec.Errors));
        Assert.Equal(27.54m, Assert.IsType<decimal>(exec.Value));
    }

    [Fact]
    public void FizzBuzz_loop_compiles_and_runs()
    {
        var path = SamplePath("fizzbuzz-loop.writ");
        var source = File.ReadAllText(path);
        var result = new CompilationPipeline().Compile(source, path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var input = File.ReadAllText(SamplePath("input-fizzbuzz.json"));
        var exec = new PowerShellExecutor().Execute(result.GeneratedPowerShell!, "fizzbuzz-loop", "ClassifyAll", input);
        Assert.True(exec.Success, string.Join(Environment.NewLine, exec.Errors));

        var list = Assert.IsAssignableFrom<System.Collections.IList>(exec.Value);
        Assert.Equal(new[] { "1", "Fizz", "Buzz", "FizzBuzz", "7" }, list.Cast<object>().Select(x => x.ToString()));
    }

    [Fact]
    public void Compile_emits_ir_json()
    {
        var path = SamplePath("fizzbuzz.writ");
        var source = File.ReadAllText(path);
        var pipeline = new CompilationPipeline();
        var result = pipeline.Compile(source, path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var json = pipeline.SerializeIr(result.Ir!);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("module", doc.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public void Codegen_snapshot_contains_stable_shapes()
    {
        var path = SamplePath("fizzbuzz.writ");
        var source = File.ReadAllText(path);
        var result = new CompilationPipeline().Compile(source, path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var script = result.GeneratedPowerShell!;
        Assert.Contains("function FizzBuzz {", script);
        Assert.Contains("param([long]$n)", script);
        Assert.Contains("'FizzBuzz'", script);
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
