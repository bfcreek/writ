using Writ.Compiler.Compilation;
using Writ.Compiler.Execution;
using Writ.Runtime;

namespace Writ.Tests;

public class NativeToolsTests
{
    [Fact]
    public void Ensure_halts_when_condition_is_false()
    {
        var source = """
            When run, returning a string
                ensure 1 is equal to 2
                return "no"
            """;
        var result = new CompilationPipeline().Compile(source, "ensure.writ");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var exec = new MachineExecutor().Execute(result.GeneratedCSharp!, "Main", "{}");
        Assert.False(exec.Success);
        Assert.Contains("ensure", string.Join(" ", exec.Errors), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Uptime_runs_on_Machine()
    {
        var source = """
            When run, returning a string
                the up is the uptime
                return the up as a string
            """;
        var result = new CompilationPipeline().Compile(source, "uptime.writ");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var exec = new MachineExecutor().Execute(result.GeneratedCSharp!, "Main", "{}");
        Assert.True(exec.Success, string.Join("; ", exec.Errors));
        Assert.Contains("TotalSeconds", exec.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Text_slug_runs()
    {
        var source = """
            When run, returning a string
                the slug is the slug of "Hello World"
                return the slug
            """;
        var result = new CompilationPipeline().Compile(source, "slug.writ");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var exec = new MachineExecutor().Execute(result.GeneratedCSharp!, "Main", "{}");
        Assert.True(exec.Success, string.Join("; ", exec.Errors));
        Assert.Equal("hello-world", exec.Value?.ToString());
    }

    [Fact]
    public void Yaml_parse_builtin_works()
    {
        Machine.BindAmbient();
        try
        {
            Machine.Io = new CollectingIo();
            var parsed = Machine.ParseYaml("name: Ada\nage: 36");
            Assert.NotNull(parsed);
            Assert.Equal("Ada", Machine.Text(Machine.Field(parsed, "name")));
        }
        finally
        {
            Machine.Unbind();
        }
    }

    private sealed class CollectingIo : IMachineIo
    {
        public void Say(string text) { }
        public string ReadLine(string? prompt) => "";
        public void Clear() { }
        public void Wait(TimeSpan duration) { }
    }
}
