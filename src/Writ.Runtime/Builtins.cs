namespace Writ.Runtime;

public static class Builtins
{
    public static long Len<T>(IReadOnlyList<T> values) => values.Count;

    public static long Len(string value) => value.Length;

    public static decimal Round(decimal value, long digits) =>
        Math.Round(value, (int)digits, MidpointRounding.AwayFromZero);

    public static decimal Round(long value, long digits) => Round((decimal)value, digits);

    public static long SumInt(IReadOnlyList<long> values)
    {
        long total = 0;
        foreach (var v in values)
        {
            total += v;
        }

        return total;
    }

    public static decimal SumDec(IReadOnlyList<decimal> values)
    {
        decimal total = 0;
        foreach (var v in values)
        {
            total += v;
        }

        return total;
    }

    public static decimal Sum(IReadOnlyList<long> values) => SumInt(values);

    public static decimal Sum(IReadOnlyList<decimal> values) => SumDec(values);

    public static List<T> Append<T>(IReadOnlyList<T> list, T element)
    {
        var result = new List<T>(list.Count + 1);
        result.AddRange(list);
        result.Add(element);
        return result;
    }

    public static string Str(object? value) => value?.ToString() ?? "";

    public static long Int(object? value) => value switch
    {
        long l => l,
        int i => i,
        decimal d => (long)d,
        string s => long.Parse(s),
        bool b => b ? 1L : 0L,
        _ => Convert.ToInt64(value)
    };

    public static decimal Dec(object? value) => value switch
    {
        decimal d => d,
        long l => l,
        int i => i,
        string s => decimal.Parse(s),
        _ => Convert.ToDecimal(value)
    };

    public static bool Bool(object? value) => value switch
    {
        bool b => b,
        string s => bool.Parse(s),
        long l => l != 0,
        decimal d => d != 0,
        _ => Convert.ToBoolean(value)
    };

    public static decimal SumMap<T>(IReadOnlyList<T> items, Func<T, decimal> projection)
    {
        decimal total = 0;
        foreach (var item in items)
        {
            total += projection(item);
        }

        return total;
    }

    public static decimal SumMap<T>(IReadOnlyList<T> items, Func<T, long> projection)
    {
        decimal total = 0;
        foreach (var item in items)
        {
            total += projection(item);
        }

        return total;
    }

    public static List<T> SortBy<T, TKey>(IReadOnlyList<T> items, Func<T, TKey> key, bool descending) =>
        descending ? items.OrderByDescending(key).ToList() : items.OrderBy(key).ToList();

    public static List<T> Take<T>(IReadOnlyList<T> items, long count) =>
        items.Take((int)Math.Max(0, count)).ToList();

    public static List<TOut> Select<T, TOut>(IReadOnlyList<T> items, Func<T, TOut> projection) =>
        items.Select(projection).ToList();

    public static object ParseJson(string text) =>
        System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();

    public static List<object> ParseCsv(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0)
        {
            return [];
        }

        var headers = lines[0].Split(',');
        return lines.Skip(1)
            .Select(line => (object)headers.Zip(line.Split(','), (h, v) => (h, v)).ToDictionary(p => p.h.Trim(), p => (object?)p.v.Trim()))
            .ToList();
    }

    public static T Shape<T>(object? value)
    {
        if (value is T typed)
        {
            return typed;
        }

        var json = value switch
        {
            System.Text.Json.JsonElement element => element.GetRawText(),
            null => "null",
            _ => System.Text.Json.JsonSerializer.Serialize(value)
        };
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return System.Text.Json.JsonSerializer.Deserialize<T>(json, options)
               ?? throw new InvalidOperationException($"Could not read the data as {typeof(T).Name}.");
    }

    public static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    public static string HomeFolder() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string CurrentFolder() => Directory.GetCurrentDirectory();

    public static string TempFolder() => Path.GetTempPath();

    public static T Retry<T>(int times, Func<T> action)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= Math.Max(1, times); attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException($"Gave up after {times} attempts: {last?.Message}", last);
    }

    public static void Say(params object?[] parts) =>
        Console.WriteLine(string.Join(" ", parts.Select(Str)));

    public static List<T> Filter<T>(IReadOnlyList<T> items, Func<T, bool> predicate)
    {
        var result = new List<T>();
        foreach (var item in items)
        {
            if (predicate(item))
            {
                result.Add(item);
            }
        }

        return result;
    }
}
