using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Writ.Compiler.Compilation;
using Writ.Compiler.Capabilities;
using Writ.Compiler.Semantics;
using YamlDotNet.Serialization;

namespace Writ.Runtime;

public static partial class Machine
{
    private static readonly object StoreGate = new();

    internal static void RegisterNativeTools()
    {
        void R(string name, Func<Dictionary<string, object?>, object?> handler, bool writes = false) =>
            ToolRegistry.Register(name, handler, writes);

        R("text.trim", args => Text(Arg(args, "Value")).Trim());
        R("text.upper", args => Text(Arg(args, "Value")).ToUpperInvariant());
        R("text.lower", args => Text(Arg(args, "Value")).ToLowerInvariant());
        R("text.slug", args => Slugify(Text(Arg(args, "Value"))));
        R("text.base64encode", args => Convert.ToBase64String(Encoding.UTF8.GetBytes(Text(Arg(args, "Value")))));
        R("text.base64decode", args => Encoding.UTF8.GetString(Convert.FromBase64String(Text(Arg(args, "Value")))));
        R("text.urlencode", args => Uri.EscapeDataString(Text(Arg(args, "Value"))));
        R("text.urldecode", args => Uri.UnescapeDataString(Text(Arg(args, "Value"))));
        R("date.add", DateAdd);
        R("date.daysbetween", args => DateDaysBetween(args));
        R("date.utcnow", _ => DateTime.UtcNow.ToString("o"));
        R("list.distinct", args => AsEnumerable(Arg(args, "InputObject")).Cast<object?>().Distinct().ToList());
        R("list.reverse", args => AsEnumerable(Arg(args, "InputObject")).Cast<object?>().Reverse().ToList());
        R("list.flatten", FlattenList);
        R("list.skip", args => AsEnumerable(Arg(args, "InputObject")).Cast<object?>().Skip((int)ToInteger(Arg(args, "Count"))).ToList());
        R("list.min", args => AsEnumerable(Arg(args, "InputObject")).Cast<object?>().Select(ToDecimal).DefaultIfEmpty(0).Min());
        R("list.max", args => AsEnumerable(Arg(args, "InputObject")).Cast<object?>().Select(ToDecimal).DefaultIfEmpty(0).Max());
        R("list.average", args =>
        {
            var values = AsEnumerable(Arg(args, "InputObject")).Cast<object?>().Select(ToDecimal).ToList();
            return values.Count == 0 ? 0m : values.Average();
        });
        R("json.path", JsonPath);
        R("secret.get", SecretGet);
        R("http.post", args => HttpSend(HttpMethod.Post, args), writes: true);
        R("http.put", args => HttpSend(HttpMethod.Put, args), writes: true);
        R("log.write", LogWrite);
        R("obs.calls", _ => CallLog.ToList());
        R("obs.elapsed", _ => Ambient.Value is null ? 0L : (long)Ambient.Value.Stopwatch.ElapsedMilliseconds);
        R("store.remember", StoreRemember, writes: true);
        R("store.recall", StoreRecall);
        R("notify.webhook", NotifyWebhook, writes: true);
        R("notify.email", NotifyEmail, writes: true);
        R("notify.toast", NotifyToast, writes: true);
        R("compiler.check", CompilerCheck);
        R("compiler.explain", CompilerExplain);
        R("compiler.lock-info", CompilerLockInfo);
        R("ai.infer", AiInfer, writes: true);
    }

    public static object? ParseYaml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var deserialized = new DeserializerBuilder().Build().Deserialize<object?>(text);
        return ToClrFromYaml(deserialized);
    }

    public static object? ParseXml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var document = XDocument.Parse(text);
        return XmlToData(document.Root);
    }

    private static object? DateAdd(Dictionary<string, object?> arguments)
    {
        var date = DateTime.Parse(Text(Arg(arguments, "Date")), null, System.Globalization.DateTimeStyles.RoundtripKind);
        if (arguments.ContainsKey("Days"))
        {
            date = date.AddDays(ToInteger(Arg(arguments, "Days")));
        }

        if (arguments.ContainsKey("Hours"))
        {
            date = date.AddHours(ToInteger(Arg(arguments, "Hours")));
        }

        return date.ToString("o");
    }

    private static long DateDaysBetween(Dictionary<string, object?> arguments)
    {
        var start = DateTime.Parse(Text(Arg(arguments, "Start")), null, System.Globalization.DateTimeStyles.RoundtripKind);
        var end = DateTime.Parse(Text(Arg(arguments, "End")), null, System.Globalization.DateTimeStyles.RoundtripKind);
        return (long)(end.Date - start.Date).TotalDays;
    }

    private static List<object?> FlattenList(Dictionary<string, object?> arguments)
    {
        var result = new List<object?>();
        void Walk(object? value)
        {
            if (value is IEnumerable enumerable and not string and not WritData)
            {
                foreach (var item in enumerable)
                {
                    Walk(item);
                }
            }
            else
            {
                result.Add(value);
            }
        }

        Walk(Arg(arguments, "InputObject"));
        return result;
    }

    private static object? JsonPath(Dictionary<string, object?> arguments)
    {
        var path = Text(Arg(arguments, "Path")).Trim();
        var current = Arg(arguments, "InputObject");
        if (path.StartsWith('$'))
        {
            path = path[1..];
        }

        foreach (Match match in Regex.Matches(path, @"\.([A-Za-z_][A-Za-z0-9_]*)|\[(\d+)\]"))
        {
            if (match.Groups[1].Success)
            {
                current = Field(current, match.Groups[1].Value, optional: true);
            }
            else if (match.Groups[2].Success)
            {
                var index = int.Parse(match.Groups[2].Value);
                var list = AsEnumerable(current).Cast<object?>().ToList();
                current = index >= 0 && index < list.Count ? list[index] : null;
            }
        }

        return current;
    }

    private static string SecretGet(Dictionary<string, object?> arguments)
    {
        var name = Text(Arg(arguments, "Name"));
        var fromEnv = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(fromEnv))
        {
            return fromEnv;
        }

        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".writ");
        var jsonPath = Path.Combine(home, "secrets.json");
        if (File.Exists(jsonPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? "";
            }
        }

        var dpapiPath = Path.Combine(home, "secrets.dpapi");
        if (OperatingSystem.IsWindows() && File.Exists(dpapiPath))
        {
            var protectedBytes = File.ReadAllBytes(dpapiPath);
            var plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(plain));
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? "";
            }
        }

        throw new InvalidOperationException($"Secret '{name}' was not found.");
    }

    private static object? HttpSend(HttpMethod method, Dictionary<string, object?> arguments)
    {
        var uri = Text(Arg(arguments, "Uri"));
        using var request = new HttpRequestMessage(method, uri);
        var body = Arg(arguments, "Body");
        if (body is not null)
        {
            var json = body is string text ? text : JsonSerializer.Serialize(Plain(body));
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (Arg(arguments, "Headers") is { } headersObj)
        {
            foreach (var key in FieldNames(headersObj))
            {
                request.Headers.TryAddWithoutValidation(key, Text(Field(headersObj, key, optional: true)));
            }
        }

        using var response = SharedHttp.Send(request);
        var content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        try
        {
            return ParseJson(content);
        }
        catch
        {
            return new WritData(new Dictionary<string, object?>
            {
                ["StatusCode"] = (long)response.StatusCode,
                ["Content"] = content
            });
        }
    }

    private static object? LogWrite(Dictionary<string, object?> arguments)
    {
        var level = Text(Arg(arguments, "Level"));
        if (string.IsNullOrWhiteSpace(level))
        {
            level = "info";
        }

        Say($"{level.ToUpperInvariant()}: {Text(Arg(arguments, "Message"))}");
        return null;
    }

    private static string StateDbPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".writ", "state.db");

    private static object? StoreRemember(Dictionary<string, object?> arguments)
    {
        var key = Text(Arg(arguments, "Key"));
        var value = JsonSerializer.Serialize(Plain(Arg(arguments, "Value")));
        lock (StoreGate)
        {
            var path = StateDbPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            using (var create = connection.CreateCommand())
            {
                create.CommandText = "CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, value TEXT NOT NULL)";
                create.ExecuteNonQuery();
            }

            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO kv(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            command.Parameters.AddWithValue("$k", key);
            command.Parameters.AddWithValue("$v", value);
            command.ExecuteNonQuery();
        }

        return null;
    }

    private static object? StoreRecall(Dictionary<string, object?> arguments)
    {
        var key = Text(Arg(arguments, "Key"));
        lock (StoreGate)
        {
            var path = StateDbPath();
            if (!File.Exists(path))
            {
                return null;
            }

            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM kv WHERE key = $k";
            command.Parameters.AddWithValue("$k", key);
            var result = command.ExecuteScalar() as string;
            return result is null ? null : ParseJson(result);
        }
    }

    private static object? NotifyWebhook(Dictionary<string, object?> arguments)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, string> { ["text"] = Text(Arg(arguments, "Message")) });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = SharedHttp.PostAsync(Text(Arg(arguments, "Uri")), content).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        return null;
    }

    private static object? NotifyEmail(Dictionary<string, object?> arguments)
    {
        var host = Environment.GetEnvironmentVariable("WRIT_SMTP_HOST")
            ?? throw new InvalidOperationException("Set WRIT_SMTP_HOST to send email.");
        var port = int.TryParse(Environment.GetEnvironmentVariable("WRIT_SMTP_PORT"), out var parsed) ? parsed : 25;
        var from = Environment.GetEnvironmentVariable("WRIT_SMTP_FROM") ?? "writ@localhost";
        using var client = new SmtpClient(host, port);
        var user = Environment.GetEnvironmentVariable("WRIT_SMTP_USER");
        if (!string.IsNullOrEmpty(user))
        {
            client.Credentials = new NetworkCredential(user, Environment.GetEnvironmentVariable("WRIT_SMTP_PASSWORD") ?? "");
        }

        client.Send(from, Text(Arg(arguments, "To")), Text(Arg(arguments, "Subject")), Text(Arg(arguments, "Body")));
        return null;
    }

    private static object? NotifyToast(Dictionary<string, object?> arguments)
    {
        var message = Text(Arg(arguments, "Message")).Replace("'", "''");
        if (!OperatingSystem.IsWindows())
        {
            Say("TOAST: " + message);
            return null;
        }

        var script = $@"
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
$template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText01)
$template.GetElementsByTagName('text').Item(0).AppendChild($template.CreateTextNode('{message}')) | Out-Null
$toast = [Windows.UI.Notifications.ToastNotification]::new($template)
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Writ').Show($toast)
";
        var psi = new ProcessStartInfo("powershell", "-NoProfile -Command -")
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi);
        if (process is null)
        {
            Say("TOAST: " + message);
            return null;
        }

        process.StandardInput.Write(script);
        process.StandardInput.Close();
        process.WaitForExit();
        return null;
    }

    private static List<object?> CompilerCheck(Dictionary<string, object?> arguments)
    {
        var path = Path.GetFullPath(Text(Arg(arguments, "Path")));
        var pipeline = new CompilationPipeline();
        var result = pipeline.Compile(File.ReadAllText(path), path);
        return result.Diagnostics.Select(diagnostic => (object?)new WritData(new Dictionary<string, object?>
        {
            ["Code"] = diagnostic.Code,
            ["Severity"] = diagnostic.Severity.ToString(),
            ["Message"] = diagnostic.Message,
            ["Line"] = (long)diagnostic.Span.StartLine,
            ["Column"] = (long)diagnostic.Span.StartColumn
        })).ToList();
    }

    private static string CompilerExplain(Dictionary<string, object?> arguments)
    {
        var path = Path.GetFullPath(Text(Arg(arguments, "Path")));
        var pipeline = new CompilationPipeline();
        var result = pipeline.Compile(File.ReadAllText(path), path);
        return Explainer.Explain(result, path);
    }

    private static object? CompilerLockInfo(Dictionary<string, object?> arguments)
    {
        var path = Path.GetFullPath(Text(Arg(arguments, "Path")));
        var lockPath = LockFile.PathFor(path);
        if (!File.Exists(lockPath))
        {
            var pipeline = new CompilationPipeline();
            var result = pipeline.Compile(File.ReadAllText(path), path);
            if (!result.Success)
            {
                throw new InvalidOperationException("Program did not check; cannot build lock info.");
            }

            return ParseJson(JsonSerializer.Serialize(LockFile.Build(File.ReadAllText(path), result, CmdletCatalog.Default)));
        }

        return ParseJson(File.ReadAllText(lockPath));
    }

    private static object? AiInfer(Dictionary<string, object?> arguments)
    {
        var prompt = Text(Arg(arguments, "Prompt"));
        var baseUrl = (Environment.GetEnvironmentVariable("WRIT_AI_BASE_URL") ?? "https://api.openai.com/v1").TrimEnd('/');
        var model = Environment.GetEnvironmentVariable("WRIT_AI_MODEL") ?? "gpt-4o-mini";
        var apiKey = Environment.GetEnvironmentVariable("WRIT_AI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                apiKey = SecretGet(new Dictionary<string, object?> { ["Name"] = "WRIT_AI_API_KEY" });
            }
            catch
            {
                throw new InvalidOperationException("Set WRIT_AI_API_KEY (or a secret with that name) to use ai.infer.");
            }
        }

        var payload = JsonSerializer.Serialize(new
        {
            model,
            messages = new[] { new { role = "user", content = prompt } }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = SharedHttp.Send(request);
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content))
        {
            var text = content.GetString() ?? "";
            try
            {
                return ParseJson(text);
            }
            catch
            {
                return text;
            }
        }

        return ParseJson(body);
    }

    private static string Slugify(string text)
    {
        var lower = text.Trim().ToLowerInvariant();
        var slug = Regex.Replace(lower, @"[^a-z0-9]+", "-").Trim('-');
        return slug;
    }

    private static object? ToClrFromYaml(object? value) => value switch
    {
        null => null,
        string or bool or long or int or double or float or decimal => value,
        IDictionary<object, object?> map => new WritData(map.ToDictionary(pair => Text(pair.Key), pair => ToClrFromYaml(pair.Value), StringComparer.OrdinalIgnoreCase)),
        IDictionary<string, object?> stringMap => new WritData(stringMap.ToDictionary(pair => pair.Key, pair => ToClrFromYaml(pair.Value), StringComparer.OrdinalIgnoreCase)),
        IList list => list.Cast<object?>().Select(ToClrFromYaml).ToList(),
        _ => value
    };

    private static object? XmlToData(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        if (!element.HasElements && !element.HasAttributes)
        {
            return element.Value;
        }

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["#name"] = element.Name.LocalName
        };
        foreach (var attribute in element.Attributes())
        {
            values["@" + attribute.Name.LocalName] = attribute.Value;
        }

        var children = element.Elements().GroupBy(child => child.Name.LocalName, StringComparer.OrdinalIgnoreCase);
        foreach (var group in children)
        {
            var items = group.Select(XmlToData).ToList();
            values[group.Key] = items.Count == 1 ? items[0] : items;
        }

        if (!element.HasElements && !string.IsNullOrWhiteSpace(element.Value))
        {
            values["#text"] = element.Value;
        }

        return new WritData(values);
    }
}
