using System.Collections;
using System.Globalization;

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
public static class Machine
{
    private static readonly AsyncLocal<IMachineIo?> Current = new();

    public static IMachineIo Io
    {
        get => Current.Value ?? throw new InvalidOperationException("No machine is bound.");
        set => Current.Value = value;
    }

    public static void Unbind() => Current.Value = null;

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
