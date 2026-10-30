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
    public void Without_constraints_the_default_emission_is_csharp()
    {
        var path = TestPaths.Sample(Path.Combine("basics", "calculate-total.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(EmissionCatalog.CSharpLibrary.Id, result.Emission!.Id);
        Assert.Contains("default", result.Emission.Reason);
        Assert.Empty(result.Emission.Considered);
    }

    [Fact]
    public void Project_preferences_still_select_csharp()
    {
        var path = TestPaths.Sample(Path.Combine("emission", "total.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var emission = result.Emission!;
        Assert.Equal("csharp", emission.Language);
        Assert.Equal("library", emission.Approach);
        Assert.Equal("in-process", emission.Architecture);
        Assert.Equal(EmissionCatalog.CSharpLibrary.Id, emission.Id);

        var exec = new MachineExecutor().Execute(result.GeneratedCSharp!, "Double", """{ "value": 21 }""");
        Assert.True(exec.Success, string.Join(Environment.NewLine, exec.Errors));
        Assert.Equal(42L, Convert.ToInt64(exec.Value));

        var explained = Explainer.Explain(result, path);
        Assert.Contains("emission: csharp-library", explained);
    }

    [Fact]
    public void A_cmdlet_program_emits_csharp()
    {
        var path = TestPaths.Sample(Path.Combine("files", "folder-report.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(EmissionCatalog.CSharpLibrary.Id, result.Emission!.Id);
        Assert.Contains("InvokeCommand(\"Get-ChildItem\"", result.GeneratedCSharp);
    }

    [Fact]
    public void Forbidding_the_script_architecture_selects_the_in_process_library()
    {
        var result = Compile(Pure, """{ "prefer": ["in-process"] }""");
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(EmissionCatalog.CSharpLibrary.Id, result.Emission!.Id);
        Assert.Contains("static long Double", result.GeneratedCSharp);
    }

    [Fact]
    public void Forbidding_every_architecture_is_an_error()
    {
        var result = Compile(Pure, """{ "forbid": ["in-process"] }""");
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E5001" && diagnostic.Message.Contains("csharp-library") && diagnostic.Message.Contains("in-process"));
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

        var extra = Compile(Pure, """{ "prefer": ["script"], "speed": "fast" }""");
        Assert.Contains(extra.Diagnostics, diagnostic => diagnostic.Code == "E5002");
    }

    [Fact]
    public void Project_and_user_preferences_both_apply()
    {
        var dir = TempDir();
        try
        {
            var user = Path.Combine(dir, "user.json");
            File.WriteAllText(user, """{ "avoid": ["dry-run"] }""");
            var project = Path.Combine(dir, "app");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "writ.constraints.json"), """{ "prefer": ["library"] }""");
            var path = Path.Combine(project, "double.writ");
            File.WriteAllText(path, Pure);

            var result = new CompilationPipeline().Compile(Pure, path, emission: new EmissionOptions
            {
                ReadUserFile = true,
                UserFilePath = user
            });
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.Equal(EmissionCatalog.CSharpLibrary.Id, result.Emission!.Id);
            Assert.Contains("user user.json", result.Emission.Reason);
            Assert.Contains("project writ.constraints.json", result.Emission.Reason);
            Assert.Contains("prefers library", result.Emission.Reason);
            Assert.Contains("avoids dry-run", result.Emission.Reason);
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
            File.WriteAllText(Path.Combine(dir, "writ.constraints.json"), """{ "prefer": ["in-process"] }""");
            var child = Path.Combine(dir, "child");
            Directory.CreateDirectory(child);
            File.WriteAllText(Path.Combine(child, "writ.constraints.json"), """{ "require": ["dry-run"] }""");
            var path = Path.Combine(child, "double.writ");
            File.WriteAllText(path, Pure);

            var result = new CompilationPipeline().Compile(Pure, path);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.Equal(EmissionCatalog.CSharpLibrary.Id, result.Emission!.Id);
            Assert.Contains("requires dry-run", result.Emission.Reason);

            File.Delete(Path.Combine(child, "writ.constraints.json"));
            var fromParent = new CompilationPipeline().Compile(Pure, path);
            Assert.True(fromParent.Success, string.Join(Environment.NewLine, fromParent.Diagnostics));
            Assert.Equal(EmissionCatalog.CSharpLibrary.Id, fromParent.Emission!.Id);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void An_empty_constraints_file_keeps_csharp()
    {
        var result = Compile(Pure, "{ }");
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(EmissionCatalog.CSharpLibrary.Id, result.Emission!.Id);
        Assert.Contains("no preferences", result.Emission.Reason);
    }

    [Fact]
    public void A_forced_target_skips_scoring_and_still_has_to_fit_the_program()
    {
        var forced = Compile(Pure, """{ "prefer": ["library"] }""", new EmissionOptions { ForcedId = "csharp-library" });
        Assert.True(forced.Success, string.Join(Environment.NewLine, forced.Diagnostics));
        Assert.Equal(EmissionCatalog.CSharpLibrary.Id, forced.Emission!.Id);
        Assert.Contains("forced", forced.Emission.Reason);

        var library = Compile(Pure, "{ }", new EmissionOptions { ForcedId = "csharp-library" });
        Assert.True(library.Success, string.Join(Environment.NewLine, library.Diagnostics));
        Assert.Equal(EmissionCatalog.CSharpLibrary.Id, library.Emission!.Id);
        Assert.Contains("forced", library.Emission.Reason);
        Assert.Contains("static long Double", library.GeneratedCSharp);

        var unknown = Compile(Pure, "{ }", new EmissionOptions { ForcedId = "python-script" });
        Assert.Contains(unknown.Diagnostics, diagnostic => diagnostic.Code == "E5003" && diagnostic.Message.Contains("python-script"));
    }

    [Fact]
    public void Frozen_lock_catches_an_emission_change_and_accepts_a_lock_that_never_recorded_one()
    {
        var path = TestPaths.Sample(Path.Combine("basics", "innate-total.writ"));
        var source = File.ReadAllText(path);
        var pipeline = new CompilationPipeline();
        var plain = pipeline.Compile(source, path);
        Assert.True(plain.Success, string.Join(Environment.NewLine, plain.Diagnostics));

        var current = LockFile.Build(source, plain, pipeline.Cmdlets);
        var locked = new WritLock
        {
            SourceHash = current.SourceHash,
            CompilerVersion = current.CompilerVersion,
            IrSchema = current.IrSchema,
            CmdletCatalogHash = current.CmdletCatalogHash,
            Capabilities = current.Capabilities,
            Readings = current.Readings,
            Emission = "csharp-library"
        };
        Assert.Empty(LockFile.Verify(locked, current, path, frozen: true));
        Assert.Empty(LockFile.Verify(locked, current, path, frozen: false));
        Assert.DoesNotContain("emission:", Explainer.Diff(locked, current));

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
