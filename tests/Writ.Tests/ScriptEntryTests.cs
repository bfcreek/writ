using System.Diagnostics;
using Writ.Compiler.Codegen;
using Writ.Compiler.Compilation;
using Writ.Compiler.Execution;
using Writ.Compiler.Ir;

namespace Writ.Tests;

public class ScriptEntryTests
{
    [Fact]
    public void When_run_becomes_a_param_block_with_help_and_includes_helpers()
    {
        var path = TestPaths.Sample("largest-files.writ");
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var entry = Assert.Single(result.Ir!.Functions, f => f.IsEntry);
        Assert.Equal("Main", entry.Name);
        var helper = Assert.Single(result.Ir.Functions, f => f.Name == "Describe");
        Assert.EndsWith("folder-helpers.writ", helper.SourceFile);

        var script = result.GeneratedPowerShell!;
        Assert.Contains(".SYNOPSIS", script);
        Assert.Contains("Show the largest files in a folder.", script);
        Assert.Contains(".PARAMETER folder", script);
        Assert.Contains("param([string]$folder, [switch]$ListTools)", script);
        Assert.Contains(".PARAMETER ListTools", script);
        Assert.Contains("if ($MyInvocation.InvocationName -ne '.')", script);
    }

    [Fact]
    public void Entry_runs_through_the_executor_and_as_a_plain_script()
    {
        var path = TestPaths.Sample("largest-files.writ");
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var folder = Path.Combine(TestPaths.RepoRoot(), "samples");
        var input = System.Text.Json.JsonSerializer.Serialize(new { folder });
        var viaExecutor = new PowerShellExecutor().Execute(result.GeneratedPowerShell!, "largest-files", "Main", input);
        Assert.True(viaExecutor.Success, string.Join(Environment.NewLine, viaExecutor.Errors));
        var lines = Assert.IsType<List<object?>>(viaExecutor.Value);
        Assert.Equal(3, lines.Count);
        Assert.All(lines, line => Assert.Matches(@"\(\d+ bytes\)$", (string)line!));
        Assert.Contains("Home is", viaExecutor.Output);

        // The same file is an ordinary PowerShell script with its own parameters.
        var temp = Path.Combine(Path.GetTempPath(), "writ-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var scriptPath = Path.Combine(temp, "largest-files.ps1");
            File.WriteAllText(scriptPath, result.GeneratedPowerShell);
            File.WriteAllText(Path.Combine(temp, "Writ.psm1"), PowerShellGenerator.RuntimeModuleSource);
            var psi = new ProcessStartInfo("pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-folder", folder })
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, stderr);
            Assert.Equal(3, stdout.Split('\n').Count(l => l.Contains("bytes)")));
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public void Environment_and_places_agree_across_targets()
    {
        var source = """
            To Locate, given a name which is a string, returning a string
                the value is the environment variable "INTENT_TEST_VALUE"
                return "{the name}={the value} home={the home folder}"
            """;
        var result = new CompilationPipeline().Compile(source, "where.writ");
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        Environment.SetEnvironmentVariable("INTENT_TEST_VALUE", "42");
        try
        {
            var input = """{ "name": "x" }""";
            var dotnet = new RoslynExecutor().Execute(result.GeneratedCSharp!, "where", "Locate", input);
            var pwsh = new PowerShellExecutor().Execute(result.GeneratedPowerShell!, "where", "Locate", input);
            Assert.True(pwsh.Success, string.Join(Environment.NewLine, pwsh.Errors));
            Assert.Equal(dotnet.Value, pwsh.Value);
            Assert.StartsWith("x=42 home=", (string)pwsh.Value!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTENT_TEST_VALUE", null);
        }
    }

    [Fact]
    public void Helper_files_cannot_have_their_own_entry()
    {
        var folder = Path.Combine(Path.GetTempPath(), "writ-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "helper.writ"), """
                When run
                    say "hi"
                """);
            var source = """
                Also use "helper.writ"

                When run
                    say "main"
                """;
            var result = new CompilationPipeline().Compile(source, Path.Combine(folder, "main.writ"));
            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, d => d.Code == "E1108");
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
