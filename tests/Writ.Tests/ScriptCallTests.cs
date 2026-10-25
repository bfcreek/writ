using Writ.Compiler.Compilation;
using Writ.Compiler.Execution;
using Writ.Compiler.Ir;

namespace Writ.Tests;

public class ScriptCallTests
{
    [Fact]
    public void Writ_and_powershell_scripts_both_run()
    {
        var path = TestPaths.Sample(Path.Combine("scripts", "call-both.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("Invoke-WritScript", result.GeneratedPowerShell);
        var calls = Walk(result.Ir!.Functions.SelectMany(f => f.Body)).OfType<IrScriptCall>().ToList();
        Assert.Contains(calls, c => c.Kind == "writ");
        Assert.Contains(calls, c => c.Kind == "powershell");

        var input = File.ReadAllText(TestPaths.Sample(Path.Combine("scripts", "input.json")));
        var pwsh = new PowerShellExecutor().Execute(result.GeneratedPowerShell!, "call-both", "Main", input);
        Assert.True(pwsh.Success, string.Join(Environment.NewLine, pwsh.Errors));
        Assert.Equal("Hello, Ada (42)", pwsh.Value);
    }

    [Fact]
    public void Check_reads_a_powershell_script_and_does_not_run_it()
    {
        var folder = Path.Combine(Path.GetTempPath(), "writ-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var marker = Path.Combine(folder, "ran.txt");
        try
        {
            File.WriteAllText(Path.Combine(folder, "touch.ps1"), $$"""
                <#
                .OUTPUTS
                    string
                #>
                param([string]$Name)
                Set-Content -LiteralPath '{{marker.Replace("\\", "/")}}' -Value ran
                "Hello, $Name"
                """);
            var source = """
                Allow running scripts

                When run, returning a string
                    return run "touch.ps1" with name set to "Ada"
                """;
            var writ = Path.Combine(folder, "call.writ");
            var result = new CompilationPipeline().Compile(source, writ);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.False(File.Exists(marker));

            var ran = new PowerShellExecutor().Execute(result.GeneratedPowerShell!, "call", "Main", "{}");
            Assert.True(ran.Success, string.Join(Environment.NewLine, ran.Errors));
            Assert.Equal("Hello, Ada", ran.Value);
            Assert.True(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Powershell_script_without_permission_is_rejected()
    {
        var path = TestPaths.Sample(Path.Combine("scripts", "call-both.writ"));
        var source = File.ReadAllText(path).Replace("Allow running scripts\n", "").Replace("Allow running scripts\r\n", "");
        var result = new CompilationPipeline().Compile(source, path);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E4003" && d.Message.Contains("running scripts"));
    }

    [Fact]
    public void Missing_script_is_rejected()
    {
        var source = """
            Allow running scripts

            When run, returning a string
                return run "missing.ps1"
            """;
        var result = new CompilationPipeline().Compile(source, TestPaths.Sample("missing-call.writ"));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E4010");
    }

    [Fact]
    public void Writ_script_without_an_entry_is_rejected()
    {
        var folder = Path.Combine(Path.GetTempPath(), "writ-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "helper.writ"), """
                To Helper, given a value which is an integer, returning an integer
                    return the value
                """);
            var source = """
                When run, given a value which is an integer, returning an integer
                    return run "helper.writ" with the value
                """;
            var result = new CompilationPipeline().Compile(source, Path.Combine(folder, "call.writ"));
            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, d => d.Code == "E4011");
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Caller_must_grant_what_the_writ_script_grants()
    {
        var folder = Path.Combine(Path.GetTempPath(), "writ-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "reader.writ"), """
                Allow reading files

                When run, returning a string
                    return "ok"
                """);
            var source = """
                When run, returning a string
                    return run "reader.writ"
                """;
            var result = new CompilationPipeline().Compile(source, Path.Combine(folder, "call.writ"));
            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, d => d.Code == "E4003" && d.Message.Contains("reading files"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Script_that_calls_itself_is_rejected()
    {
        var folder = Path.Combine(Path.GetTempPath(), "writ-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "a.writ"), """
                When run, returning an integer
                    return run "b.writ"
                """);
            File.WriteAllText(Path.Combine(folder, "b.writ"), """
                When run, returning an integer
                    return run "a.writ"
                """);
            var result = new CompilationPipeline().Compile(File.ReadAllText(Path.Combine(folder, "a.writ")), Path.Combine(folder, "a.writ"));
            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, d => d.Code == "E4012");
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static IEnumerable<IrNode> Walk(IEnumerable<IrNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Children(node))
            {
                foreach (var inner in Walk([child]))
                {
                    yield return inner;
                }
            }
        }
    }

    private static IEnumerable<IrNode> Children(IrNode node) => node switch
    {
        IrAssign a => [a.Value],
        IrReturn r => [r.Value],
        IrScriptCall s => s.Arguments.Select(a => a.Value),
        _ => []
    };
}
