using System.Collections;
using System.Text.RegularExpressions;

namespace Writ.Runtime;

public static partial class Machine
{
    public static List<long> Range(object? from, object? to)
    {
        var start = ToInteger(from);
        var end = ToInteger(to);
        var result = new List<long>();
        var step = start <= end ? 1 : -1;
        for (var value = start; step > 0 ? value <= end : value >= end; value += step)
        {
            result.Add(value);
        }

        return result;
    }

    public static object? Index(object? value, object? index)
    {
        var items = AsList(value);
        var i = (int)ToInteger(index);
        if (i < 0)
        {
            i = items.Count + i;
        }

        return i >= 0 && i < items.Count ? items[i] : null;
    }

    public static List<object?> Slice(object? value, object? from, object? to)
    {
        var items = AsList(value);
        var start = Math.Max(0, (int)ToInteger(from));
        var end = Math.Min(items.Count - 1, (int)ToInteger(to));
        return end < start ? [] : items.Skip(start).Take(end - start + 1).ToList();
    }

    public static string Join(object? value, string separator = "") =>
        string.Join(separator, AsList(value).Select(Text));

    public static List<string> Split(object? value, object? separator) =>
        Text(value).Split(Text(separator)).ToList();

    public static string Format(object? format, params object?[] args) =>
        string.Format(Text(format), args);

    public static string Replace(object? value, object? pattern, object? replacement) =>
        Regex.Replace(Text(value), Text(pattern), Text(replacement));

    public static bool Like(object? value, object? pattern)
    {
        var regex = "^" + Regex.Escape(Text(pattern)).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(Text(value), regex, RegexOptions.IgnoreCase);
    }

    public static bool Match(object? value, object? pattern) =>
        Regex.IsMatch(Text(value), Text(pattern));

    public static bool Contains(object? collection, object? value) =>
        AsList(collection).Any(item => string.Equals(Text(item), Text(value), StringComparison.Ordinal));

    public static bool In(object? value, object? collection) => Contains(collection, value);

    public static bool Xor(object? left, object? right) => ToBoolean(left) ^ ToBoolean(right);

    public static long BitwiseAnd(object? left, object? right) => ToInteger(left) & ToInteger(right);

    public static long BitwiseOr(object? left, object? right) => ToInteger(left) | ToInteger(right);

    public static long BitwiseXor(object? left, object? right) => ToInteger(left) ^ ToInteger(right);

    public static long BitwiseNot(object? value) => ~ToInteger(value);

    public static long ShiftLeft(object? left, object? right) => ToInteger(left) << (int)ToInteger(right);

    public static long ShiftRight(object? left, object? right) => ToInteger(left) >> (int)ToInteger(right);

    public static bool Is(object? value, string typeName) => TypeName(typeName) switch
    {
        "string" => value is string,
        "integer" => value is long or int,
        "decimal" => value is decimal or double or float,
        "boolean" => value is bool,
        "nothing" => value is null,
        _ => value is not null && value.GetType().Name.Equals(typeName, StringComparison.OrdinalIgnoreCase)
    };

    public static object? As(object? value, string typeName)
    {
        try
        {
            return Cast(value, typeName);
        }
        catch
        {
            return null;
        }
    }

    public static object? Cast(object? value, string typeName) => TypeName(typeName) switch
    {
        "string" => Text(value),
        "integer" => ToInteger(value),
        "decimal" => ToDecimal(value),
        "boolean" => ToBoolean(value),
        "character" or "char" => Text(value).FirstOrDefault(),
        _ => value
    };

    public static object? Coalesce(object? left, object? right) => left ?? right;

    public static WritData Hash(params object?[] pairs)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < pairs.Length; i += 2)
        {
            values[Text(pairs[i])] = pairs[i + 1];
        }

        return new WritData(values);
    }

    public static List<object?> Pipe(object? value, Func<object?, object?> block)
    {
        var result = new List<object?>();
        foreach (var item in AsList(value))
        {
            result.Add(block(item));
        }

        return result;
    }

    public static object? Static(string typeName, string member, params object?[] args)
    {
        var type = ResolveType(typeName);
        if (args.Length == 0 && type.GetProperty(member) is { } property)
        {
            return property.GetValue(null);
        }

        var method = type.GetMethods().FirstOrDefault(candidate =>
            candidate.Name.Equals(member, StringComparison.Ordinal) &&
            candidate.GetParameters().Length == args.Length);
        return method is null ? null : method.Invoke(null, BindReflectionArguments(method.GetParameters(), args));
    }

    public static object? Member(object? target, string member, params object?[] args)
    {
        if (target is null)
        {
            return null;
        }

        var type = target.GetType();
        if (args.Length == 0 && type.GetProperty(member) is { } property)
        {
            return property.GetValue(target);
        }

        var method = type.GetMethods().FirstOrDefault(candidate =>
            candidate.Name.Equals(member, StringComparison.Ordinal) &&
            candidate.GetParameters().Length == args.Length);
        return method is null ? null : method.Invoke(target, BindReflectionArguments(method.GetParameters(), args));
    }

    public static long[,] Grid(object? rows, object? columns) =>
        new long[(int)ToInteger(rows), (int)ToInteger(columns)];

    private static object?[] BindReflectionArguments(System.Reflection.ParameterInfo[] parameters, object?[] args) =>
        parameters.Select((parameter, index) => ConvertValue(args[index], parameter.ParameterType)).ToArray();

    private static Type ResolveType(string typeName) => typeName switch
    {
        "String" or "string" => typeof(string),
        "Math" => typeof(Math),
        _ => Type.GetType(typeName) ?? Type.GetType("System." + typeName) ?? typeof(object)
    };

    private static string TypeName(string typeName) => typeName.Trim().ToLowerInvariant();

    private static List<object?> AsList(object? value)
    {
        if (value is null)
        {
            return [];
        }

        if (value is string text)
        {
            return [text];
        }

        if (value is IEnumerable enumerable)
        {
            return enumerable.Cast<object?>().ToList();
        }

        return [value];
    }
}
