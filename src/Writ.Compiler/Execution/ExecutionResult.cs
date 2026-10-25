using System.Text.Json;

namespace Writ.Compiler.Execution;

public sealed record ExecutionResult(bool Success, object? Value, IReadOnlyList<string> Errors)
{
    /// <summary>Capability invocations observed while running.</summary>
    public int CapabilityCalls { get; init; }

    /// <summary>Text the program printed before its result.</summary>
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
