using Writ.Compiler.Compilation;
using Writ.Compiler.Execution;
using Writ.Compiler.Ir;

namespace Writ.Tests;

public class CommandTests
{
    [Fact]
    public void Folder_report_binds_cmdlets_and_runs()
    {
        var path = TestPaths.Sample("folder-report.writ");
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var body = result.Ir!.Functions[0].Body;
        var command = Assert.IsType<IrCommand>(Assert.IsType<IrAssign>(body[0]).Value);
        Assert.Equal("Get-ChildItem", command.Name);
        Assert.Contains(command.Arguments, a => a.Name == "Path" && a.Value is IrVar { Name: "folder" });
        Assert.Contains(command.Arguments, a => a.Name == "File");
        Assert.IsType<IrFilter>(Assert.IsType<IrAssign>(body[1]).Value);
        Assert.IsType<IrSort>(Assert.IsType<IrAssign>(body[2]).Value);
        Assert.IsType<IrTake>(Assert.IsType<IrAssign>(body[3]).Value);

        var script = result.GeneratedPowerShell!;
        Assert.Contains("Get-ChildItem -Path $folder -File", script);
        Assert.Contains("Sort-Object -Property 'length' -Descending", script);
        Assert.Contains("Select-Object -First 3L", script);

        var folder = Path.Combine(TestPaths.RepoRoot(), "samples");
        var input = System.Text.Json.JsonSerializer.Serialize(new { folder });
        var exec = new PowerShellExecutor().Execute(script, "folder-report", "Report", input);
        Assert.True(exec.Success, string.Join(Environment.NewLine, exec.Errors));

        var names = Assert.IsAssignableFrom<System.Collections.IList>(exec.Value).Cast<object>().Select(x => x.ToString()!).ToList();
        Assert.Equal(3, names.Count);
        var expected = new DirectoryInfo(folder).GetFiles().OrderByDescending(f => f.Length).Take(3).Select(f => f.Name).ToList();
        Assert.Equal(expected.OrderBy(x => x), names.OrderBy(x => x));
        Assert.Contains(names[0], exec.Output);
    }

    [Fact]
    public void Cmdlet_without_permission_fails_with_E4003()
    {
        var path = TestPaths.Sample(Path.Combine("errors", "no-permission.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "E4003");
        Assert.Contains("Remove-Item", diagnostic.Message);
        Assert.Contains("Allow writing files", diagnostic.Message);
    }

    [Fact]
    public void Compile_never_runs_a_command()
    {
        var marker = Path.Combine(Path.GetTempPath(), "writ-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(marker);
        try
        {
            var source = $$"""
                Allow writing files

                To Wipe, returning an integer
                    the target is "{{marker.Replace("\\", "\\\\")}}"
                    remove the target
                    return 0
                """;
            var result = new CompilationPipeline().Compile(source, "wipe.writ");
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.Contains("Remove-Item -Path $target", result.GeneratedPowerShell!);
            Assert.True(Directory.Exists(marker));
        }
        finally
        {
            Directory.Delete(marker, true);
        }
    }

    [Theory]
    [InlineData("countdown.writ", "Countdown", "input-countdown.json")]
    public void While_and_say_agree_across_targets(string sample, string entry, string inputFile)
    {
        var path = TestPaths.Sample(sample);
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var input = File.ReadAllText(TestPaths.Sample(inputFile));

        var dotnet = new RoslynExecutor().Execute(result.GeneratedCSharp!, "countdown", entry, input);
        Assert.True(dotnet.Success, string.Join(Environment.NewLine, dotnet.Errors));
        var pwsh = new PowerShellExecutor().Execute(result.GeneratedPowerShell!, "countdown", entry, input);
        Assert.True(pwsh.Success, string.Join(Environment.NewLine, pwsh.Errors));
        Assert.Equal(3L, Convert.ToInt64(dotnet.Value));
        Assert.Equal(3L, Convert.ToInt64(pwsh.Value));
        Assert.Contains("tick 3", pwsh.Output);
    }
}
