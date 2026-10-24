using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Writ.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Writ.Compiler.Execution;

public sealed class RoslynExecutor
{
    public ExecutionResult Execute(
        string generatedCSharp,
        string moduleName,
        string entryFunction,
        string? inputJson)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(generatedCSharp);
        var references = GetReferences();
        var compilation = CSharpCompilation.Create(
            assemblyName: $"Writ.Generated.{Guid.NewGuid():N}",
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);
        if (!emitResult.Success)
        {
            var errors = emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToList();
            return ExecutionResult.Failed(errors);
        }

        peStream.Position = 0;
        var assembly = AssemblyLoadContext.Default.LoadFromStream(peStream);
        var typeName = $"Writ.Generated.{Sanitize(moduleName)}Module";
        var type = assembly.GetType(typeName)
                   ?? throw new InvalidOperationException($"Generated type '{typeName}' not found.");
        var method = type.GetMethod(Sanitize(entryFunction), BindingFlags.Public | BindingFlags.Static)
                     ?? throw new InvalidOperationException($"Entry function '{entryFunction}' not found.");

        var args = BindArguments(method, inputJson);
        try
        {
            var result = method.Invoke(null, args);
            return ExecutionResult.Ok(result);
        }
        catch (TargetInvocationException ex)
        {
            return ExecutionResult.Failed([ex.InnerException?.ToString() ?? ex.ToString()]);
        }
    }

    private static object?[] BindArguments(MethodInfo method, string? inputJson)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == 0)
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(inputJson))
        {
            throw new InvalidOperationException(
                $"Entry function '{method.Name}' requires arguments; provide --input JSON.");
        }

        using var doc = JsonDocument.Parse(inputJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Input JSON must be an object of parameter names to values.");
        }

        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            if (!doc.RootElement.TryGetProperty(param.Name!, out var value))
            {
                // also try case-insensitive
                value = doc.RootElement.EnumerateObject()
                    .FirstOrDefault(p => string.Equals(p.Name, param.Name, StringComparison.OrdinalIgnoreCase))
                    .Value;
                if (value.ValueKind == JsonValueKind.Undefined)
                {
                    throw new InvalidOperationException($"Missing input argument '{param.Name}'.");
                }
            }

            args[i] = Deserialize(value, param.ParameterType);
        }

        return args;
    }

    private static object? Deserialize(JsonElement element, Type targetType)
    {
        if (targetType == typeof(string))
        {
            return element.GetString();
        }

        if (targetType == typeof(long))
        {
            return element.GetInt64();
        }

        if (targetType == typeof(decimal))
        {
            return element.GetDecimal();
        }

        if (targetType == typeof(bool))
        {
            return element.GetBoolean();
        }

        if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(List<>))
        {
            var elementType = targetType.GetGenericArguments()[0];
            var listType = typeof(List<>).MakeGenericType(elementType);
            var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
            foreach (var item in element.EnumerateArray())
            {
                list.Add(Deserialize(item, elementType));
            }

            return list;
        }

        // Record / complex type via JSON
        return JsonSerializer.Deserialize(element.GetRawText(), targetType, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    private static List<MetadataReference> GetReferences()
    {
        var refs = new List<MetadataReference>();
        var trusted = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator)
                      ?? [];

        foreach (var path in trusted)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name is "System.Runtime" or "System.Collections" or "System.Linq" or "System.Private.CoreLib"
                or "System.Console" or "netstandard" or "System.Text.Json" or "System.Memory"
                or "System.Numerics" or "System.Collections.Concurrent")
            {
                refs.Add(MetadataReference.CreateFromFile(path));
            }
        }

        // Ensure core assemblies if TRUSTED list filtering was too aggressive
        void Ensure(Type t)
        {
            var loc = t.Assembly.Location;
            if (!string.IsNullOrEmpty(loc) && refs.All(r => r.Display != loc))
            {
                refs.Add(MetadataReference.CreateFromFile(loc));
            }
        }

        Ensure(typeof(object));
        Ensure(typeof(Console));
        Ensure(typeof(List<>));
        Ensure(typeof(Enumerable));
        Ensure(typeof(decimal));
        Ensure(typeof(Builtins));

        // Reference all assemblies currently loaded that might be needed
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location))
            {
                continue;
            }

            if (refs.All(r => r.Display != asm.Location))
            {
                var fileName = Path.GetFileName(asm.Location);
                if (fileName.StartsWith("System.", StringComparison.Ordinal) ||
                    fileName.StartsWith("mscorlib", StringComparison.Ordinal) ||
                    fileName.StartsWith("netstandard", StringComparison.Ordinal) ||
                    fileName.StartsWith("Writ.Runtime", StringComparison.Ordinal))
                {
                    refs.Add(MetadataReference.CreateFromFile(asm.Location));
                }
            }
        }

        return refs;
    }

    private static string Sanitize(string name)
    {
        var chars = name.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray();
        if (chars.Length == 0)
        {
            return "_";
        }

        if (char.IsDigit(chars[0]))
        {
            return "_" + new string(chars);
        }

        return new string(chars);
    }
}

public sealed record ExecutionResult(bool Success, object? Value, IReadOnlyList<string> Errors)
{
    /// <summary>Capability invocations observed while running (PowerShell target).</summary>
    public int CapabilityCalls { get; init; }

    /// <summary>Text the program printed before its result (PowerShell target).</summary>
    public string Output { get; init; } = "";

    public static ExecutionResult Ok(object? value) => new(true, value, []);
    public static ExecutionResult Failed(IReadOnlyList<string> errors) => new(false, null, errors);

    public string FormatValue()
    {
        if (Value is null)
        {
            return "null";
        }

        return JsonSerializer.Serialize(Value, new JsonSerializerOptions { WriteIndented = true });
    }
}
