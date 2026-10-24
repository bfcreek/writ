using Writ.Compiler.Compilation;
using Writ.Compiler.Execution;

namespace Writ.Tests;

public class EmissionTests
{
    private const string Pure = """
        To Double, given a value which is an integer, returning an integer
            return the value times 2
        """;

    [Fact]
    public void Without_constraints_the_default_emission_is_a_powershell_script()
    {
        var path = TestPaths.Sample("calculate-total.writ");
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(EmissionCatalog.PowerShellScript.Id, result.Emission!.Id);
        Assert.Contains("default", result.Emission.Reason);
        Assert.Empty(result.Emission.Considered);
    }

    [Fact]
    public void Project_preferences_select_the_in_process_library()
    {
        var path = TestPaths.Sample(Path.Combine("emission", "total.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var emission = result.Emission!;
        Assert.Equal("csharp", emission.Language);
        Assert.Equal("library", emission.Approach);
        Assert.Equal("in-process", emission.Architecture);
        Assert.Equal(EmissionCatalog.CSharpLibrary.Id, emission.Id);
        Assert.Contains("scored 4", emission.Reason);
        Assert.Contains(emission.Considered, item => item.Id == "powershell-script" && item.Reason.Contains("scored -2"));

        var exec = new RoslynExecutor().Execute(result.GeneratedCSharp!, "total", "Double", """{ "value": 21 }""");
        Assert.True(exec.Success, string.Join(Environment.NewLine, exec.Errors));
        Assert.Equal(42L, exec.Value);

        var explained = Explainer.Explain(result, path);
        Assert.Contains("emission: csharp-library", explained);
        Assert.Contains("scored 4", explained);
    }

    [Fact]
    public void A_cmdlet_keeps_the_powershell_script_when_the_library_cannot_run_it()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "report.writ");
            File.WriteAllText(path, File.ReadAllText(TestPaths.Sample("folder-report.writ")));
            File.WriteAllText(Path.Combine(dir, "writ.constraints.json"), """
                { "prefer": ["in-process", "compiled"] }
                """);

            var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.Equal(EmissionCatalog.PowerShellScript.Id, result.Emission!.Id);
            Assert.Contains("only emission that can compile this program", result.Emission.Reason);
            Assert.Contains(result.Emission.Considered, item =>
                item.Id == EmissionCatalog.CSharpLibrary.Id && item.Reason.Contains("Get-ChildItem"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Forbidding_every_architecture_is_an_error()
    {
        var result = Compile(Pure, """{ "forbid": ["external-process", "in-process"] }""");
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E5001" && diagnostic.Message.Contains("powershell-script") && diagnostic.Message.Contains("csharp-library"));
    }

    [Fact]
    public void Requiring_and_forbidding_the_same_trait_is_an_error()
    {
        var dir = TempDir();
        try
        {
            var user = Path.Combine(dir, "user.json");
            var project = Path.Combine(dir, "proj");
            Directory.CreateDirectory(project);
            File.WriteAllText(user, """{ "require": ["dry-run"] }""");
            File.WriteAllText(Path.Combine(project, "writ.constraints.json"), """{ "forbid": ["dry-run"] }""");
            var path = Path.Combine(project, "double.writ");
            File.WriteAllText(path, Pure);

            var result = new CompilationPipeline().Compile(Pure, path, emission: new EmissionOptions
            {
                ReadUserFile = true,
                UserFilePath = user
            });
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E5002" && diagnostic.Message.Contains("dry-run"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Unknown_traits_and_broken_json_fail_closed()
    {
        var unknown = Compile(Pure, """{ "prefer": ["python"] }""");
        Assert.Contains(unknown.Diagnostics, diagnostic => diagnostic.Code == "E5002" && diagnostic.Message.Contains("python"));

        var broken = Compile(Pure, "{ not json");
        Assert.Contains(broken.Diagnostics, diagnostic => diagnostic.Code == "E5002");

        var extra = Compile(Pure, """{ "prefer": ["compiled"], "speed": "fast" }""");
        Assert.Contains(extra.Diagnostics, diagnostic => diagnostic.Code == "E5002");
    }

    [Fact]
    public void Project_preferences_outweigh_user_preferences()
    {
        var dir = TempDir();
        try
        {
            var user = Path.Combine(dir, "user.json");
            File.WriteAllText(user, """{ "prefer": ["compiled"] }""");
            var project = Path.Combine(dir, "app");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "writ.constraints.json"), """{ "prefer": ["script"] }""");
            var path = Path.Combine(project, "double.writ");
            File.WriteAllText(path, Pure);

            var result = new CompilationPipeline().Compile(Pure, path, emission: new EmissionOptions
            {
                ReadUserFile = true,
                UserFilePath = user
            });
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.Equal(EmissionCatalog.PowerShellScript.Id, result.Emission!.Id);
            Assert.Contains("scored 2", result.Emission.Reason);
            Assert.Contains(result.Emission.Considered, item => item.Id == EmissionCatalog.CSharpLibrary.Id && item.Reason.Contains("scored 1"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_nearest_project_file_wins()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "writ.constraints.json"), """{ "prefer": ["script"] }""");
            var child = Path.Combine(dir, "child");
            Directory.CreateDirectory(child);
            File.WriteAllText(Path.Combine(child, "writ.constraints.json"), """{ "language": "csharp" }""");
            var path = Path.Combine(child, "double.writ");
            File.WriteAllText(path, Pure);

            var result = new CompilationPipeline().Compile(Pure, path);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.Equal(EmissionCatalog.CSharpLibrary.Id, result.Emission!.Id);

            File.Delete(Path.Combine(child, "writ.constraints.json"));
            var fromParent = new CompilationPipeline().Compile(Pure, path);
            Assert.Equal(EmissionCatalog.PowerShellScript.Id, fromParent.Emission!.Id);
            Assert.Contains("prefers script", fromParent.Emission.Reason);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void An_empty_constraints_file_ties_and_keeps_the_default()
    {
        var result = Compile(Pure, "{ }");
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(EmissionCatalog.PowerShellScript.Id, result.Emission!.Id);
        Assert.Contains("tied", result.Emission.Reason);
    }

    [Fact]
    public void A_forced_target_skips_scoring_and_still_has_to_fit_the_program()
    {
        var forced = Compile(Pure, """{ "prefer": ["compiled"] }""", new EmissionOptions { ForcedId = "powershell-script" });
        Assert.True(forced.Success, string.Join(Environment.NewLine, forced.Diagnostics));
        Assert.Equal(EmissionCatalog.PowerShellScript.Id, forced.Emission!.Id);
        Assert.Contains("forced", forced.Emission.Reason);

        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "report.writ");
            File.WriteAllText(path, File.ReadAllText(TestPaths.Sample("folder-report.writ")));
            var result = new CompilationPipeline().Compile(File.ReadAllText(path), path, emission: new EmissionOptions { ForcedId = "csharp-library" });
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E5001" && diagnostic.Message.Contains("Get-ChildItem"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Frozen_lock_catches_an_emission_change_and_accepts_a_lock_that_never_recorded_one()
    {
        var path = TestPaths.Sample("innate-total.writ");
        var source = File.ReadAllText(path);
        var pipeline = new CompilationPipeline();
        var plain = pipeline.Compile(source, path);
        var forced = pipeline.Compile(source, path, emission: new EmissionOptions { ForcedId = "csharp-library" });
        Assert.True(plain.Success && forced.Success);

        var locked = LockFile.Build(source, plain, pipeline.Cmdlets);
        var current = LockFile.Build(source, forced, pipeline.Cmdlets);
        Assert.Contains(LockFile.Verify(locked, current, path, frozen: true), diagnostic => diagnostic.Code == "E4009" && diagnostic.Message.Contains("emission"));
        Assert.Empty(LockFile.Verify(locked, current, path, frozen: false));
        Assert.Contains("emission: powershell-script -> csharp-library", Explainer.Diff(locked, current));

        var legacy = new WritLock
        {
            SourceHash = locked.SourceHash,
            CompilerVersion = locked.CompilerVersion,
            IrSchema = locked.IrSchema,
            CmdletCatalogHash = locked.CmdletCatalogHash,
            Capabilities = locked.Capabilities,
            Readings = locked.Readings
        };
        Assert.Empty(LockFile.Verify(legacy, locked, path, frozen: true));
    }

    private static CompilationResult Compile(string source, string constraints, EmissionOptions? emission = null)
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "writ.constraints.json"), constraints);
            var path = Path.Combine(dir, "double.writ");
            File.WriteAllText(path, source);
            return new CompilationPipeline().Compile(source, path, emission: emission);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "writ-emission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
