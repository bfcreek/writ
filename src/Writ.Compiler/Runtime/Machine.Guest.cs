using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Writ.Runtime;

public static partial class Machine
{
    internal static Assembly CompileGuest(string source)
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
        if (!emit.Success)
        {
            var errors = emit.Diagnostics
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(diagnostic => diagnostic.ToString());
            throw new InvalidOperationException("Generated C# did not compile:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }

        return Assembly.Load(pe.ToArray());
    }

    internal static object? InvokeGuest(string source, string entryFunction, string? inputJson)
    {
        var program = CompileGuest(source).GetType("WritProgram")
            ?? throw new InvalidOperationException("Generated assembly did not contain WritProgram.");
        var method = program.GetMethod(entryFunction, BindingFlags.Public | BindingFlags.Static)
            ?? EntryMethod(program, entryFunction)
            ?? throw new InvalidOperationException($"Entry function '{entryFunction}' was not found.");

        try
        {
            return method.Invoke(null, BindArguments(method, inputJson));
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    internal static MethodInfo? EntryMethod(Type program, string entryFunction) =>
        program.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method => method.GetCustomAttribute<MachineEntryAttribute>() is not null &&
                                      method.Name.Equals(entryFunction, StringComparison.OrdinalIgnoreCase));

    internal static object?[] BindArguments(MethodInfo method, string? inputJson)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson);
        var root = document.RootElement;
        return method.GetParameters().Select(parameter =>
        {
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(parameter.Name!, out var property))
            {
                return BindJson(property, parameter.ParameterType);
            }

            return parameter.HasDefaultValue ? parameter.DefaultValue : Default(parameter.ParameterType);
        }).ToArray();
    }

    internal static object? BindJson(JsonElement element, Type type)
    {
        if (type == typeof(string))
        {
            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
        }

        if (type == typeof(long))
        {
            return element.GetInt64();
        }

        if (type == typeof(decimal))
        {
            return element.GetDecimal();
        }

        if (type == typeof(bool))
        {
            return element.GetBoolean();
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var elementType = type.GetGenericArguments()[0];
            var list = (System.Collections.IList)Activator.CreateInstance(type)!;
            foreach (var item in element.EnumerateArray())
            {
                list.Add(BindJson(item, elementType));
            }

            return list;
        }

        if (type == typeof(object) || type == typeof(WritData))
        {
            return ToClr(element);
        }

        return JsonSerializer.Deserialize(element.GetRawText(), type, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    internal static object? Default(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;
}
