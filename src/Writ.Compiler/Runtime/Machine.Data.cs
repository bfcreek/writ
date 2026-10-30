using System.Collections;
using System.Dynamic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Writ.Runtime;

public sealed class WritData : DynamicObject, IReadOnlyDictionary<string, object?>
{
    private readonly Dictionary<string, object?> _values;

    public WritData() : this(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase))
    {
    }

    public WritData(IDictionary<string, object?> values) =>
        _values = new Dictionary<string, object?>(values, StringComparer.OrdinalIgnoreCase);

    public object? this[string key] => _values[key];

    public IEnumerable<string> Keys => _values.Keys;

    public IEnumerable<object?> Values => _values.Values;

    public int Count => _values.Count;

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public bool TryGetValue(string key, out object? value) => _values.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override bool TryGetMember(GetMemberBinder binder, out object? result) =>
        _values.TryGetValue(binder.Name, out result);

    public override bool TrySetMember(SetMemberBinder binder, object? value)
    {
        _values[binder.Name] = value;
        return true;
    }

    public Dictionary<string, object?> ToDictionary() => new(_values, StringComparer.OrdinalIgnoreCase);
}

public sealed class WritCapabilityException : Exception
{
    public WritCapabilityException(string message, string alias, string operation, string provider)
        : base(message)
    {
        Alias = alias;
        Operation = operation;
        Provider = provider;
    }

    public string Alias { get; }

    public string Operation { get; }

    public string Provider { get; }
}

public static partial class Machine
{
    public static object? ParseJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        using var document = JsonDocument.Parse(text);
        return ToClr(document.RootElement);
    }

    public static List<WritData> ParseCsv(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
        {
            return [];
        }

        var headers = SplitCsvLine(lines[0]);
        var rows = new List<WritData>();
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var fields = SplitCsvLine(line);
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Count; i++)
            {
                row[headers[i]] = i < fields.Count ? fields[i] : "";
            }

            rows.Add(new WritData(row));
        }

        return rows;
    }

    public static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    public static string HomeFolder() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string CurrentFolder() => Directory.GetCurrentDirectory();

    public static string TempFolder() => Path.GetTempPath();

    public static string MachineName() => Environment.MachineName;

    public static string UserName() => Environment.UserName;

    public static T Validate<T>(object? value, string typeName, params string[] fields) => ConvertTo<T>(Validate(value, typeName, fields));

    public static object? Validate(object? value, string typeName, params string[] fields)
    {
        if (value is IEnumerable enumerable and not string and not WritData and not IDictionary)
        {
            foreach (var item in enumerable)
            {
                Validate(item, typeName, fields);
            }

            return value;
        }

        foreach (var field in fields)
        {
            AssertField(value, field, typeName);
        }

        return value;
    }

    public static void AssertField(object? value, string field, string typeName)
    {
        if (!TryField(value, field, out _))
        {
            throw new InvalidOperationException($"Expected a '{field}' field to read a {typeName}, and it is missing.");
        }
    }

    public static T Field<T>(object? target, string field, bool optional = false)
    {
        if (TryField(target, field, out var value))
        {
            return ConvertTo<T>(value);
        }

        if (optional)
        {
            return default!;
        }

        throw new InvalidOperationException($"Field '{field}' was not found.");
    }

    public static object? Field(object? target, string field, bool optional = false) => Field<object?>(target, field, optional);

    public static decimal SumMap<T>(IEnumerable<T>? items, Func<T, object?> projection)
    {
        decimal total = 0;
        if (items is null)
        {
            return total;
        }

        foreach (var item in items)
        {
            total += ToDecimal(projection(item));
        }

        return total;
    }

    public static List<T> Where<T>(IEnumerable<T>? items, Func<T, bool> predicate) =>
        items is null ? [] : items.Where(predicate).ToList();

    public static List<T> Filter<T>(IEnumerable<T>? items, Func<T, bool> predicate) => Where(items, predicate);

    public static List<T> Sort<T>(IEnumerable<T>? items, string field, bool descending)
    {
        var list = items?.ToList() ?? [];
        return descending
            ? list.OrderByDescending(item => SortValue(Field(item, field, optional: true))).ToList()
            : list.OrderBy(item => SortValue(Field(item, field, optional: true))).ToList();
    }

    public static List<T> Take<T>(IEnumerable<T>? items, long count) =>
        items is null ? [] : items.Take((int)Math.Max(0, count)).ToList();

    public static List<TOut> SelectField<TOut>(IEnumerable? items, string field)
    {
        var result = new List<TOut>();
        if (items is null)
        {
            return result;
        }

        foreach (var item in items)
        {
            result.Add(Field<TOut>(item, field));
        }

        return result;
    }

    public static List<T> Repeat<T>(IEnumerable<T>? items, long count)
    {
        var result = new List<T>();
        var source = items?.ToList() ?? [];
        for (var i = 0; i < count; i++)
        {
            result.AddRange(source);
        }

        return result;
    }

    public static string Repeat(string text, long count) =>
        string.Concat(Enumerable.Repeat(text, (int)Math.Max(0, count)));

    public static object? InvokeCapability(
        string alias,
        string operation,
        string manifestPath,
        Dictionary<string, object?>? arguments,
        string provider = "mock",
        string? endpoint = null,
        string transport = "stdio",
        string? workingDirectory = null,
        bool writes = false)
    {
        arguments ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (writes && DryRun)
        {
            Say($"What if: would call {alias}.{operation} with {JsonSerializer.Serialize(arguments)}");
            if (provider == "mock" && TryMockResult(manifestPath, operation, out var dryResult))
            {
                return dryResult;
            }

            return null;
        }

        Ambient.Value?.CallLog.Add($"{alias}.{operation}");
        try
        {
            return provider switch
            {
                "mcp" => InvokeMcpCapability(alias, operation, arguments, endpoint ?? "", transport, workingDirectory),
                "http" => InvokeHttpCapability(operation, arguments, endpoint ?? "", workingDirectory),
                "sqlite" => InvokeSqliteCapability(operation, arguments, endpoint ?? "", workingDirectory, writes),
                "powershell-module" => InvokePowerShellModuleCapability(operation, arguments, endpoint ?? ""),
                "mock" => TryMockResult(manifestPath, operation, out var result)
                    ? result
                    : throw new InvalidOperationException($"No mock result is declared for {alias}.{operation}."),
                _ => throw new NotSupportedException($"Provider '{provider}' for '{alias}' is not supported yet.")
            };
        }
        catch (Exception ex) when (ex is not WritCapabilityException)
        {
            throw new WritCapabilityException($"{alias}.{operation} failed: {ex.Message}", alias, operation, provider);
        }
    }

    public static T Retry<T>(Func<T> action, int times)
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

    public static object? InvokeScript(string path, string kind, string? compiled, Dictionary<string, object?> arguments, bool writes)
    {
        if (writes && DryRun)
        {
            Say($"What if: would run {path}");
            return null;
        }

        Ambient.Value?.CallLog.Add("script:" + Path.GetFileName(path));
        return kind.Equals("writ", StringComparison.OrdinalIgnoreCase)
            ? InvokeCompiledWritScript(path, compiled, arguments)
            : InvokePowerShellFile(path, arguments);
    }

    private static object? InvokeCompiledWritScript(string path, string? compiled, Dictionary<string, object?> arguments)
    {
        if (string.IsNullOrWhiteSpace(compiled))
        {
            throw new InvalidOperationException($"No compiled script was provided for '{path}'.");
        }

        var payload = JsonSerializer.Serialize(Plain(arguments));
        return InvokeGuest(compiled, "Main", payload);
    }

    private static string InvokePowerShellFile(string path, Dictionary<string, object?> arguments)
    {
        var psi = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory()
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path })
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in arguments)
        {
            psi.ArgumentList.Add("-" + key);
            if (value is null)
            {
                continue;
            }

            psi.ArgumentList.Add(value is bool flag ? (flag ? "true" : "false") : Text(value));
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start pwsh.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort cleanup after timeout.
            }

            throw new TimeoutException("Script did not finish within 120 seconds.");
        }

        var output = stdout.GetAwaiter().GetResult();
        var errors = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Script '{path}' failed ({process.ExitCode}). {errors}".Trim());
        }

        return output.Trim();
    }

    internal static object? ToClr(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) && !element.GetRawText().Contains('.') ? l : element.GetDecimal(),
        JsonValueKind.Array => element.EnumerateArray().Select(ToClr).ToList(),
        JsonValueKind.Object => new WritData(element.EnumerateObject()
            .ToDictionary(p => p.Name, p => ToClr(p.Value), StringComparer.OrdinalIgnoreCase)),
        _ => element.GetRawText()
    };

    public static bool Compare(object? left, string op, object? right)
    {
        if (op is "==" or "!=")
        {
            var equal = string.Equals(Text(left), Text(right), StringComparison.Ordinal);
            return op == "==" ? equal : !equal;
        }

        var comparison = ToDecimal(left).CompareTo(ToDecimal(right));
        return op switch
        {
            "<" => comparison < 0,
            "<=" => comparison <= 0,
            ">" => comparison > 0,
            ">=" => comparison >= 0,
            _ => throw new InvalidOperationException($"Unknown comparison operator '{op}'.")
        };
    }

    public static T ConvertTo<T>(object? value)
    {
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (value is null)
        {
            return default!;
        }

        if (target.IsInstanceOfType(value))
        {
            return (T)value;
        }

        if (target == typeof(string))
        {
            return (T)(object)Text(value);
        }

        if (target == typeof(long))
        {
            return (T)(object)ToInteger(value);
        }

        if (target == typeof(decimal))
        {
            return (T)(object)ToDecimal(value);
        }

        if (target == typeof(bool))
        {
            return (T)(object)ToBoolean(value);
        }

        if (target.IsGenericType && target.GetGenericTypeDefinition() == typeof(List<>))
        {
            var elementType = target.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(target)!;
            if (value is IEnumerable enumerable and not string)
            {
                foreach (var item in enumerable)
                {
                    list.Add(ConvertValue(item, elementType));
                }
            }

            return (T)list;
        }

        if (target.IsClass)
        {
            var instance = Activator.CreateInstance(target)!;
            foreach (var property in target.GetProperties().Where(property => property.CanWrite))
            {
                if (TryField(value, property.Name, out var member))
                {
                    property.SetValue(instance, ConvertValue(member, property.PropertyType));
                }
            }

            return (T)instance;
        }

        return (T)value;
    }

    private static object? ConvertValue(object? value, Type target)
    {
        var method = typeof(Machine).GetMethod(nameof(ConvertTo), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
            .MakeGenericMethod(target);
        return method.Invoke(null, [value]);
    }

    private static bool TryField(object? target, string field, out object? value)
    {
        value = null;
        if (target is null)
        {
            return false;
        }

        if (target is WritData data)
        {
            return data.TryGetValue(field, out value);
        }

        if (target is IReadOnlyDictionary<string, object?> readOnly && readOnly.TryGetValue(field, out value))
        {
            return true;
        }

        if (target is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is string key && key.Equals(field, StringComparison.OrdinalIgnoreCase))
                {
                    value = entry.Value;
                    return true;
                }
            }
        }

        if (target is IEnumerable enumerable and not string &&
            (field.Equals("length", StringComparison.OrdinalIgnoreCase) || field.Equals("count", StringComparison.OrdinalIgnoreCase)))
        {
            value = enumerable.Cast<object?>().LongCount();
            return true;
        }

        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.IgnoreCase;
        var property = target.GetType().GetProperty(field, flags);
        if (property is not null)
        {
            value = property.GetValue(target);
            return true;
        }

        var member = target.GetType().GetField(field, flags);
        if (member is not null)
        {
            value = member.GetValue(target);
            return true;
        }

        return false;
    }

    private static object? SortValue(object? value) => value is IComparable ? value : Text(value);

    private static bool TryMockResult(string manifestPath, string operation, out object? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            throw new FileNotFoundException("Manifest was not found.", manifestPath);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!document.RootElement.TryGetProperty("mock", out var mock) ||
            !mock.TryGetProperty(operation, out var entry))
        {
            return false;
        }

        if (entry.TryGetProperty("failure", out var failure))
        {
            throw new InvalidOperationException(failure.GetString() ?? "Mock failure.");
        }

        if (!entry.TryGetProperty("result", out var resultElement))
        {
            throw new InvalidOperationException($"Mock for {operation} has no result.");
        }

        result = ToClr(resultElement);
        return true;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (ch == ',' && !quoted)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        result.Add(current.ToString());
        return result;
    }
}
