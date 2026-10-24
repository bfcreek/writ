using Writ.Compiler.Compilation;
using Writ.Compiler.Execution;
using Writ.Compiler.Ir;
using Writ.Runtime;

namespace Writ.Tests;

public class FailureHandlingTests
{
    [Fact]
    public void Try_if_that_fails_finally_binds_and_agrees_across_targets()
    {
        var path = TestPaths.Sample("careful-retry.writ");
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var body = result.Ir!.Functions[0].Body;
        Assert.IsType<IrRetry>(Assert.IsType<IrAssign>(body[0]).Value);
        var loop = Assert.IsType<IrForEach>(body[2]);
        var tryNode = Assert.IsType<IrTry>(loop.Body[0]);
        Assert.NotNull(tryNode.OnFailure);
        Assert.NotNull(tryNode.Finally);

        var input = """{ "day": "2026-10-20" }""";
        var pwsh = new PowerShellExecutor().Execute(result.GeneratedPowerShell!, "careful-retry", "RecoverCarefully", input);
        Assert.True(pwsh.Success, string.Join(Environment.NewLine, pwsh.Errors));
        Assert.Equal("retried", pwsh.Value);
        Assert.Contains("Looked at p1", pwsh.Output);
        Assert.Contains("Looked at p2", pwsh.Output);

        CapabilityRuntime.Reset();
        var dotnet = new RoslynExecutor().Execute(result.GeneratedCSharp!, "careful-retry", "RecoverCarefully", input);
        Assert.True(dotnet.Success, string.Join(Environment.NewLine, dotnet.Errors));
        Assert.Equal("retried", dotnet.Value);
    }

    [Fact]
    public void Failure_handler_sees_the_error_message()
    {
        var path = TestPaths.Sample("careful-retry.writ");
        var source = File.ReadAllText(path).Replace("payments.capability.json", "payments-flaky.capability.json");
        var result = new CompilationPipeline().Compile(source, path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var pwsh = new PowerShellExecutor().Execute(result.GeneratedPowerShell!, "careful-retry", "RecoverCarefully", """{ "day": "2026-10-20" }""");
        Assert.True(pwsh.Success, string.Join(Environment.NewLine, pwsh.Errors));
        Assert.Equal("failed", pwsh.Value);
        Assert.Contains("Could not retry p1: payments.retry failed: Gateway timeout", pwsh.Output);
        Assert.Contains("Looked at p1", pwsh.Output);
    }

    [Fact]
    public void Unhandled_capability_failure_is_structured()
    {
        var path = TestPaths.Sample("recover-failures.writ");
        var source = File.ReadAllText(path).Replace("payments.capability.json", "payments-flaky.capability.json");
        var result = new CompilationPipeline().Compile(source, path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var pwsh = new PowerShellExecutor().Execute(result.GeneratedPowerShell!, "recover", "RecoverFailures", """{ "day": "2026-10-20" }""");
        Assert.False(pwsh.Success);
        Assert.StartsWith("payments.retry (mock): payments.retry failed: Gateway timeout", pwsh.Errors[0]);
    }

    [Fact]
    public void Retrying_a_write_is_rejected()
    {
        var path = TestPaths.Sample(Path.Combine("errors", "retry-a-write.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "E4006");
        Assert.Contains("try to", diagnostic.Message);
    }

    [Fact]
    public void Dry_run_describes_writes_instead_of_performing_them()
    {
        var path = TestPaths.Sample("careful-retry.writ");
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var pwsh = new PowerShellExecutor { DryRun = true }.Execute(result.GeneratedPowerShell!, "careful-retry", "RecoverCarefully", """{ "day": "2026-10-20" }""");
        Assert.True(pwsh.Success, string.Join(Environment.NewLine, pwsh.Errors));
        Assert.Contains("What if: would call payments.retry", pwsh.Output);
        Assert.Equal(1, pwsh.CapabilityCalls); // list_failed ran; the two retries were only described
        Assert.Equal("retried", pwsh.Value); // the manifest's sample result stands in for the skipped write
    }
}
