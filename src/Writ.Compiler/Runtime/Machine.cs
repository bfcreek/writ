using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace Writ.Runtime;

/// <summary>
/// What generated C# calls instead of the host console.
/// The process that boots an image binds <see cref="Machine.Io"/> before the startup hook runs.
/// </summary>
public interface IMachineIo
{
    void Say(string text);

    string ReadLine(string? prompt);

    void Clear();

    void Wait(TimeSpan duration);
}

/// <summary>Marks the <c>When run</c> method firmware transfers to.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class MachineEntryAttribute : Attribute;

/// <summary>The guest asked to stop, or the monitor halted the machine.</summary>
public sealed class MachineHaltException : Exception
{
    public MachineHaltException(string message) : base(message)
    {
    }
}

/// <summary>
/// Runtime for a C# image. Built-ins match the PowerShell module's conversions.
/// Console words (<c>say</c>, <c>read</c>, <c>clear</c>, <c>wait</c>) go through <see cref="Io"/>.
/// </summary>
public static partial class Machine
{
    private static readonly AsyncLocal<IMachineIo?> Current = new();
    private static readonly AsyncLocal<AmbientState?> Ambient = new();

    private sealed class AmbientState
    {
        public bool DryRun { get; init; }
        public string ScriptDirectory { get; init; } = "";
        public string PreviousDirectory { get; init; } = "";
        public List<string> CallLog { get; } = [];
        public System.Diagnostics.Stopwatch Stopwatch { get; } = System.Diagnostics.Stopwatch.StartNew();
        public Dictionary<string, object?> Mocks { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static IMachineIo Io
    {
        get => Current.Value ?? throw new InvalidOperationException("No machine is bound.");
        set => Current.Value = value;
    }

    public static bool DryRun => Ambient.Value?.DryRun ?? false;

    public static string ScriptDirectory => Ambient.Value?.ScriptDirectory ?? AppContext.BaseDirectory;

    public static int CapabilityCalls => Ambient.Value?.CallLog.Count ?? 0;

    public static IReadOnlyList<string> CallLog => Ambient.Value?.CallLog ?? [];

    public static void BindAmbient(bool dryRun = false, string? scriptDirectory = null)
    {
        var directory = string.IsNullOrWhiteSpace(scriptDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(scriptDirectory);
        var state = new AmbientState
        {
            DryRun = dryRun,
            ScriptDirectory = directory,
            PreviousDirectory = Directory.GetCurrentDirectory()
        };
        LoadMocks(state, directory);
        Ambient.Value = state;
    }

    private static void LoadMocks(AmbientState state, string scriptDirectory)
    {
        foreach (var path in new[]
                 {
                     Path.Combine(scriptDirectory, "writ.mocks.json"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".writ", "writ.mocks.json")
                 })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                continue;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    property.Value.TryGetProperty("result", out var result))
                {
                    state.Mocks[property.Name] = ToClr(result);
                }
            }
        }
    }

    internal static bool TryMockTool(string name, out object? result)
    {
        if (Ambient.Value?.Mocks.TryGetValue(name, out result) == true)
        {
            return true;
        }

        result = null;
        return false;
    }

    public static void Unbind()
    {
        if (!string.IsNullOrWhiteSpace(Ambient.Value?.PreviousDirectory) && Directory.Exists(Ambient.Value.PreviousDirectory))
        {
            Directory.SetCurrentDirectory(Ambient.Value.PreviousDirectory);
        }

        Current.Value = null;
        Ambient.Value = null;
    }

    public static void Say(string text) => Io.Say(text);

    public static string Read(string? prompt) => Io.ReadLine(prompt);

    public static void Clear() => Io.Clear();

    /// <param name="milliseconds">True when the Writ sentence asked for milliseconds. Otherwise <paramref name="amount"/> is seconds.</param>
    public static void Wait(long amount, bool milliseconds)
    {
        var duration = milliseconds ? TimeSpan.FromMilliseconds(amount) : TimeSpan.FromSeconds(amount);
        Io.Wait(duration);
    }

    public static string Text(object? value) => value switch
    {
        null => "",
        bool flag => flag ? "True" : "False",
        WritData data => JsonSerializer.Serialize(data.ToDictionary()),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString() ?? ""
    };

    public static long Len(object? value) => value switch
    {
        null => 0,
        string text => text.Length,
        ICollection collection => collection.Count,
        IEnumerable enumerable => enumerable.Cast<object?>().LongCount(),
        _ => 0
    };

    public static decimal Round(object? value, long digits) =>
        Math.Round(ToDecimal(value), (int)digits, MidpointRounding.AwayFromZero);

    public static decimal Sum(IEnumerable? values)
    {
        decimal total = 0;
        if (values is null)
        {
            return total;
        }

        foreach (var value in values)
        {
            total += ToDecimal(value);
        }

        return total;
    }

    public static List<T> Append<T>(IEnumerable<T>? list, T item)
    {
        var copy = list is null ? new List<T>() : new List<T>(list);
        copy.Add(item);
        return copy;
    }

    public static List<T> Concat<T>(IEnumerable<T>? left, IEnumerable<T>? right)
    {
        var copy = left is null ? new List<T>() : new List<T>(left);
        if (right is not null)
        {
            copy.AddRange(right);
        }

        return copy;
    }

    public static long ToInteger(object? value) => value switch
    {
        null => 0,
        bool flag => flag ? 1 : 0,
        long number => number,
        string text => long.Parse(text, CultureInfo.InvariantCulture),
        _ => (long)Math.Truncate(ToDecimal(value))
    };

    public static decimal ToDecimal(object? value) => value switch
    {
        null => 0,
        decimal number => number,
        string text => decimal.Parse(text, CultureInfo.InvariantCulture),
        bool flag => flag ? 1 : 0,
        IConvertible convertible => convertible.ToDecimal(CultureInfo.InvariantCulture),
        _ => 0
    };

    public static bool ToBoolean(object? value) => value switch
    {
        null => false,
        bool flag => flag,
        string text => bool.Parse(text),
        _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture)
    };
}
