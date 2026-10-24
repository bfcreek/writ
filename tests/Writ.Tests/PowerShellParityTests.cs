using System.Text.Json;
using System.Text.Json.Nodes;
using Writ.Compiler.Compilation;
using Writ.Compiler.Execution;

namespace Writ.Tests;

public class PowerShellParityTests
{
    [Theory]
    [InlineData("calculate-total.writ", "CalculateTotal", "input-calculate-total.json")]
    [InlineData("intent-sum.writ", "CalculateTotal", "input-calculate-total.json")]
    [InlineData("innate-total.writ", "CalculateTotal", "input-calculate-total.json")]
    [InlineData("intent-filter.writ", "PositiveQuantity", "input-intent-filter.json")]
    [InlineData("fizzbuzz-loop.writ", "ClassifyAll", "input-fizzbuzz.json")]
    [InlineData("recover-failures.writ", "RecoverFailures", "input-recover.json")]
    [InlineData("but-first.writ", "Recover", "input-but-first.json")]
    public void PowerShell_and_dotnet_agree(string sample, string entry, string inputFile)
    {
        var path = TestPaths.Sample(sample);
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var input = File.ReadAllText(TestPaths.Sample(inputFile));
        var module = Path.GetFileNameWithoutExtension(sample);

        var dotnet = new RoslynExecutor().Execute(result.GeneratedCSharp!, module, entry, input);
        Assert.True(dotnet.Success, string.Join(Environment.NewLine, dotnet.Errors));
        var pwsh = new PowerShellExecutor().Execute(result.GeneratedPowerShell!, module, entry, input);
        Assert.True(pwsh.Success, string.Join(Environment.NewLine, pwsh.Errors));

        Assert.Equal(Canonical(JsonSerializer.Serialize(dotnet.Value)), Canonical(JsonSerializer.Serialize(pwsh.Value)));
    }

    [Fact]
    public void PowerShell_capability_calls_happen_only_at_run_and_in_order()
    {
        var path = TestPaths.Sample("but-first.writ");
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var script = result.GeneratedPowerShell!;
        Assert.True(script.IndexOf("'list_failed'", StringComparison.Ordinal) < script.IndexOf("'retry'", StringComparison.Ordinal));

        var exec = new PowerShellExecutor().Execute(script, "but-first", "Recover", File.ReadAllText(TestPaths.Sample("input-but-first.json")));
        Assert.True(exec.Success, string.Join(Environment.NewLine, exec.Errors));
        Assert.Equal(2, exec.CapabilityCalls);
        Assert.True(Assert.IsType<bool>(exec.Value));
    }

    private static string Canonical(string json)
    {
        var node = JsonNode.Parse(json);
        return Normalize(node);
    }

    private static string Normalize(JsonNode? node) => node switch
    {
        null => "null",
        JsonArray array => "[" + string.Join(",", array.Select(Normalize)) + "]",
        JsonObject obj => "{" + string.Join(",", obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"\"{p.Key}\":{Normalize(p.Value)}")) + "}",
        JsonValue value when value.TryGetValue<decimal>(out var number) => number.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture),
        JsonValue value => value.ToJsonString(),
        _ => node.ToJsonString()
    };
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
