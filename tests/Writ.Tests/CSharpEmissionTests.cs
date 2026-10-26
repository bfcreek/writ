using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Writ.Compiler.Codegen;
using Writ.Compiler.Compilation;
using Writ.Compiler.Execution;
using Writ.Runtime;

namespace Writ.Tests;

public class CSharpEmissionTests
{
    [Theory]
    [InlineData("basics/countdown.writ", "Countdown", """{"n":3}""", 3L)]
    [InlineData("basics/fizzbuzz.writ", "FizzBuzz", """{"n":15}""", "FizzBuzz")]
    public void CSharp_matches_PowerShell(string sample, string entry, string input, object expected)
    {
        var path = TestPaths.Sample(sample);
        var source = File.ReadAllText(path);
        var powershell = new CompilationPipeline().Compile(source, path, emission: new EmissionOptions { ForcedId = EmissionCatalog.PowerShellScript.Id });
        var csharp = new CompilationPipeline().Compile(source, path, emission: new EmissionOptions { ForcedId = EmissionCatalog.CSharpLibrary.Id });
        Assert.True(powershell.Success, string.Join(Environment.NewLine, powershell.Diagnostics));
        Assert.True(csharp.Success, string.Join(Environment.NewLine, csharp.Diagnostics));
        Assert.Null(csharp.GeneratedPowerShell);
        Assert.Contains("WritProgram", csharp.GeneratedCSharp);

        var pwsh = new PowerShellExecutor().Execute(powershell.GeneratedPowerShell!, entry, entry, input);
        Assert.True(pwsh.Success, string.Join(Environment.NewLine, pwsh.Errors));

        var method = CompileGuest(csharp.GeneratedCSharp!).GetMethod(entry, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        Machine.Io = new QuietMachine();
        object? value;
        try
        {
            value = method!.Invoke(null, Arguments(method, input));
        }
        finally
        {
            Machine.Unbind();
        }

        Assert.Equal(Normalize(expected), Normalize(pwsh.Value));
        Assert.Equal(Normalize(expected), Normalize(value));
    }

    [Fact]
    public void A_terminal_kernel_emits_console_calls()
    {
        const string source = """
            When run
                clear the screen
                say "Hello"
                wait 2 seconds
                the line is read "os"
                say the line
            """;

        var result = Compile(source, EmissionCatalog.CSharpLibrary.Id);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var generated = result.GeneratedCSharp!;
        Assert.Contains("Machine.Clear()", generated);
        Assert.Contains("Machine.Say(", generated);
        Assert.Contains("Machine.Wait(", generated);
        Assert.Contains("Machine.Read(", generated);
        Assert.Contains("MachineEntry", generated);
    }

    [Fact]
    public void A_cmdlet_program_cannot_emit_csharp()
    {
        var path = TestPaths.Sample(Path.Combine("files", "folder-report.writ"));
        var result = new CompilationPipeline().Compile(
            File.ReadAllText(path),
            path,
            emission: new EmissionOptions { ForcedId = EmissionCatalog.CSharpLibrary.Id });
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "E5001" && diagnostic.Message.Contains("csharp-library"));
    }

    [Fact]
    public void Asking_for_csharp_selects_the_library()
    {
        var result = Compile("""
            To Double, given a value which is an integer, returning an integer
                return the value times 2
            """, null, """{ "language": "csharp" }""");
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(EmissionCatalog.CSharpLibrary.Id, result.Emission!.Id);
        Assert.Equal("csharp", result.Emission.Language);
        Assert.Equal("in-process", result.Emission.Architecture);
    }

    private static CompilationResult Compile(string source, string? forced, string? constraints = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "writ-csharp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            if (constraints is not null)
            {
                File.WriteAllText(Path.Combine(dir, "writ.constraints.json"), constraints);
            }

            var path = Path.Combine(dir, "program.writ");
            File.WriteAllText(path, source);
            return new CompilationPipeline().Compile(source, path, emission: new EmissionOptions { ForcedId = forced });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static Type CompileGuest(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToList();
        var runtime = MetadataReference.CreateFromFile(typeof(Machine).Assembly.Location);
        if (references.All(reference => !string.Equals(reference.Display, runtime.Display, StringComparison.OrdinalIgnoreCase)))
        {
            references.Add(runtime);
        }

        var compilation = CSharpCompilation.Create(
            "WritGuest_" + Guid.NewGuid().ToString("N"),
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var pe = new MemoryStream();
        var emit = compilation.Emit(pe);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var assembly = Assembly.Load(pe.ToArray());
        return assembly.GetType("WritProgram")!;
    }

    private static object?[] Arguments(MethodInfo method, string input)
    {
        using var document = System.Text.Json.JsonDocument.Parse(input);
        return method.GetParameters().Select(parameter =>
        {
            var property = document.RootElement.EnumerateObject()
                .First(candidate => candidate.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase));
            if (parameter.ParameterType == typeof(long))
            {
                return (object)property.Value.GetInt64();
            }

            if (parameter.ParameterType == typeof(string))
            {
                return property.Value.GetString()!;
            }

            throw new InvalidOperationException($"Unsupported parameter type {parameter.ParameterType}.");
        }).ToArray();
    }

    private sealed class QuietMachine : IMachineIo
    {
        public void Say(string text)
        {
        }

        public string ReadLine(string? prompt) => "";

        public void Clear()
        {
        }

        public void Wait(TimeSpan duration)
        {
        }
    }

    private static object? Normalize(object? value) => value switch
    {
        long number => number,
        int number => (long)number,
        decimal number when number == decimal.Truncate(number) && number <= long.MaxValue && number >= long.MinValue => (long)number,
        string text => text,
        _ => value
    };
}
