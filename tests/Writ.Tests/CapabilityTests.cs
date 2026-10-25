using Writ.Compiler.Capabilities;
using Writ.Compiler.Compilation;
using Writ.Compiler.Execution;
using Writ.Compiler.Ir;

namespace Writ.Tests;

public class CapabilityTests
{
    [Fact]
    public void Mock_payment_recovery_runs_without_compiling_invocations()
    {
        var loader = new FileCapabilityLoader();
        var path = SamplePath(Path.Combine("payments", "recover-failures.writ"));
        var result = new CompilationPipeline(capabilities: loader).Compile(File.ReadAllText(path), path);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.True(loader.Discoveries > 0);
        Assert.Contains(result.Ir!.Functions[0].Body, n => n is IrAssign { Value: IrCapabilityCall });

        var exec = new PowerShellExecutor().Execute(
            result.GeneratedPowerShell!,
            "recover-failures",
            "RecoverFailures",
            File.ReadAllText(SamplePath(Path.Combine("payments", "input-recover.json"))));
        Assert.True(exec.Success, string.Join(Environment.NewLine, exec.Errors));
        Assert.Equal(1L, Convert.ToInt64(exec.Value));
        Assert.Equal(2, exec.CapabilityCalls);
    }

    [Fact]
    public void Unknown_operation_fails()
    {
        var path = SamplePath(Path.Combine("errors", "unknown-operation.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E4002" && d.Message.Contains("refund"));
    }

    [Fact]
    public void Missing_permission_fails()
    {
        var path = SamplePath(Path.Combine("errors", "missing-permission.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E4003");
    }

    [Fact]
    public void Invalid_schema_fails()
    {
        var path = SamplePath(Path.Combine("errors", "invalid-capability.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E4001" && d.Message.Contains("NoSuchType"));
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
