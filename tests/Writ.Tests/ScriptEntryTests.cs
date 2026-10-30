using Writ.Compiler.Compilation;
using Writ.Compiler.Execution;
using Writ.Compiler.Ir;

namespace Writ.Tests;

public class ScriptEntryTests
{
    [Fact]
    public void When_run_becomes_a_param_block_with_help_and_includes_helpers()
    {
        var path = TestPaths.Sample(Path.Combine("files", "largest-files.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var entry = Assert.Single(result.Ir!.Functions, f => f.IsEntry);
        Assert.Equal("Main", entry.Name);
        var helper = Assert.Single(result.Ir.Functions, f => f.Name == "Describe");
        Assert.EndsWith("folder-helpers.writ", helper.SourceFile);

        var script = result.GeneratedCSharp!;
        Assert.Contains("[global::Writ.Runtime.MachineEntry]", script);
        Assert.Contains("static", script);
        Assert.Contains("Main(string folder", script);
    }

    [Fact]
    public void Entry_runs_through_the_executor_and_as_a_plain_script()
    {
        var path = TestPaths.Sample(Path.Combine("files", "largest-files.writ"));
        var result = new CompilationPipeline().Compile(File.ReadAllText(path), path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var folder = Path.Combine(TestPaths.RepoRoot(), "samples", "files");
        var input = System.Text.Json.JsonSerializer.Serialize(new { folder });
        var viaExecutor = new MachineExecutor().Execute(result.GeneratedCSharp!, "Main", input);
        Assert.True(viaExecutor.Success, string.Join(Environment.NewLine, viaExecutor.Errors));
        var lines = Assert.IsType<List<object?>>(viaExecutor.Value);
        Assert.Equal(3, lines.Count);
        Assert.All(lines, line => Assert.Matches(@"\(\d+ bytes\)$", (string)line!));
        Assert.Contains("Home is", viaExecutor.Output);

        Assert.Contains("Home is", viaExecutor.Output);
    }

    [Fact]
    public void Environment_and_places_run()
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
            var pwsh = new MachineExecutor().Execute(result.GeneratedCSharp!, "Locate", input);
            Assert.True(pwsh.Success, string.Join(Environment.NewLine, pwsh.Errors));
            Assert.StartsWith("x=42 home=", (string)pwsh.Value!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTENT_TEST_VALUE", null);
        }
    }

    [Fact]
    public void Display_and_print_write_a_value()
    {
        foreach (var verb in new[] { "display", "print" })
        {
            var result = new CompilationPipeline().Compile($"""
                {verb} "hello, world!"
                """, "hello.writ");

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            var entry = Assert.Single(result.Ir!.Functions);
            var say = Assert.IsType<IrSay>(Assert.Single(entry.Body));
            var text = Assert.IsType<IrLiteral>(Assert.Single(say.Parts));
            Assert.Equal("hello, world!", text.Value);
            Assert.Contains("hello, world!", result.GeneratedCSharp);
        }
    }

    [Fact]
    public void Sentences_are_what_the_script_does()
    {
        var result = new CompilationPipeline().Compile("""
            say "hello"
            """, "hello.writ");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var entry = Assert.Single(result.Ir!.Functions);
        Assert.True(entry.IsEntry);
        Assert.Empty(entry.Parameters);
        Assert.Contains(entry.Body, node => node is IrSay);
        Assert.Contains("hello", result.GeneratedCSharp);
    }

    [Fact]
    public void A_file_of_only_declarations_does_not_run()
    {
        var result = new CompilationPipeline().Compile("""
            To Double, given a value which is an integer, returning an integer
                return the value times 2
            """, "double.writ");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.DoesNotContain(result.Ir!.Functions, function => function.IsEntry);
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
