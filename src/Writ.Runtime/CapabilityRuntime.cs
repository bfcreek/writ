using System.Text.Json;

namespace Writ.Runtime;

public sealed class CapabilityException : Exception
{
    public CapabilityException(string message) : base(message)
    {
    }
}

/// <summary>
/// Runtime invocation of a capability. The compiler never calls this type.
/// Mock results come from the manifest and are not discovered by executing the operation.
/// </summary>
public static class CapabilityRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly AsyncLocal<int> CallCount = new();

    public static int Calls => CallCount.Value;

    public static void Reset() => CallCount.Value = 0;

    public static T Call<T>(
        string alias,
        string operation,
        string manifestPath,
        string provider = "mock",
        string endpoint = "",
        string workingDirectory = "",
        string transport = "stdio",
        IReadOnlyDictionary<string, object>? arguments = null)
    {
        CallCount.Value++;
        arguments ??= new Dictionary<string, object>();
        if (provider == "mcp")
        {
            var raw = McpClient.CallTool(endpoint, workingDirectory, operation, arguments, transport);
            return Shape<T>(raw, alias, operation);
        }

        if (provider != "mock")
        {
            throw new CapabilityException($"Provider '{provider}' for '{alias}' is not supported by the .NET runtime yet.");
        }

        if (!File.Exists(manifestPath))
        {
            throw new CapabilityException($"Manifest for '{alias}' was not found at '{manifestPath}'.");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!document.RootElement.TryGetProperty("mock", out var mock) ||
            !TryGetOperation(mock, operation, out var entry))
        {
            throw new CapabilityException($"No mock result is declared for {alias}.{operation}.");
        }

        if (entry.TryGetProperty("failure", out var failure))
        {
            throw new CapabilityException(failure.GetString() ?? $"{alias}.{operation} failed.");
        }

        if (!entry.TryGetProperty("result", out var result))
        {
            throw new CapabilityException($"Mock for {alias}.{operation} has no result.");
        }

        return Shape<T>(result, alias, operation);
    }

    private static T Shape<T>(JsonElement result, string alias, string operation)
    {
        if (typeof(T) == typeof(object))
        {
            return (T)(object)result.Clone();
        }

        var value = result.Deserialize<T>(JsonOptions);
        if (value is null)
        {
            throw new CapabilityException($"Result for {alias}.{operation} was empty.");
        }

        return value;
    }

    private static bool TryGetOperation(JsonElement mock, string operation, out JsonElement entry)
    {
        if (mock.TryGetProperty(operation, out entry))
        {
            return true;
        }

        foreach (var property in mock.EnumerateObject())
        {
            if (string.Equals(property.Name, operation, StringComparison.OrdinalIgnoreCase))
            {
                entry = property.Value;
                return true;
            }
        }

        entry = default;
        return false;
    }
}
