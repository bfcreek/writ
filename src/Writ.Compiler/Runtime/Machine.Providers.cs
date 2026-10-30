using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Writ.Runtime;

public static partial class Machine
{
    private static object? InvokeHttpCapability(
        string operation,
        Dictionary<string, object?> arguments,
        string endpoint,
        string? workingDirectory)
    {
        var (method, pathTemplate) = ParseHttpOperation(operation);
        var path = pathTemplate;
        foreach (var pair in arguments)
        {
            path = path.Replace("{" + pair.Key + "}", Uri.EscapeDataString(Text(pair.Value)), StringComparison.OrdinalIgnoreCase);
        }

        var baseUrl = endpoint.TrimEnd('/');
        if (!baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var openApiPath = ResolveCapabilityPath(endpoint, workingDirectory);
            baseUrl = InferBaseUrlFromOpenApi(openApiPath);
        }

        using var request = new HttpRequestMessage(method, baseUrl + path);
        if (method != HttpMethod.Get && arguments.TryGetValue("body", out var body) && body is not null)
        {
            request.Content = new StringContent(
                body is string text ? text : JsonSerializer.Serialize(Plain(body)),
                Encoding.UTF8,
                "application/json");
        }

        using var response = SharedHttp.Send(request);
        var content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        try
        {
            return ParseJson(content);
        }
        catch
        {
            return content;
        }
    }

    private static (HttpMethod Method, string Path) ParseHttpOperation(string operation)
    {
        var parts = operation.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && Enum.TryParse<HttpMethodName>(parts[0], true, out var named))
        {
            return (named switch
            {
                HttpMethodName.Get => HttpMethod.Get,
                HttpMethodName.Post => HttpMethod.Post,
                HttpMethodName.Put => HttpMethod.Put,
                HttpMethodName.Delete => HttpMethod.Delete,
                _ => HttpMethod.Get
            }, parts[1]);
        }

        if (operation.Contains('/'))
        {
            return (HttpMethod.Get, operation.StartsWith('/') ? operation : "/" + operation);
        }

        return (HttpMethod.Post, "/" + operation.TrimStart('/'));
    }

    private enum HttpMethodName { Get, Post, Put, Delete }

    private static string InferBaseUrlFromOpenApi(string openApiPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(openApiPath));
        if (document.RootElement.TryGetProperty("servers", out var servers) &&
            servers.ValueKind == JsonValueKind.Array &&
            servers.GetArrayLength() > 0 &&
            servers[0].TryGetProperty("url", out var url))
        {
            return url.GetString()?.TrimEnd('/') ?? "http://127.0.0.1";
        }

        return "http://127.0.0.1";
    }

    private static object? InvokeSqliteCapability(
        string operation,
        Dictionary<string, object?> arguments,
        string endpoint,
        string? workingDirectory,
        bool writes)
    {
        var dbPath = ResolveCapabilityPath(endpoint, workingDirectory);
        if (writes && DryRun)
        {
            Say($"What if: would run sqlite {operation} on {dbPath}");
            return null;
        }

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        if (operation.Equals("query", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("execute", StringComparison.OrdinalIgnoreCase))
        {
            var sql = Text(Arg(arguments, "sql") ?? Arg(arguments, "Sql") ?? Arg(arguments, "query"));
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var pair in arguments.Where(pair =>
                         !pair.Key.Equals("sql", StringComparison.OrdinalIgnoreCase) &&
                         !pair.Key.Equals("query", StringComparison.OrdinalIgnoreCase)))
            {
                command.Parameters.AddWithValue("$" + pair.Key, pair.Value ?? DBNull.Value);
            }

            if (operation.Equals("execute", StringComparison.OrdinalIgnoreCase) ||
                sql.TrimStart().StartsWith("insert", StringComparison.OrdinalIgnoreCase) ||
                sql.TrimStart().StartsWith("update", StringComparison.OrdinalIgnoreCase) ||
                sql.TrimStart().StartsWith("delete", StringComparison.OrdinalIgnoreCase))
            {
                return (long)command.ExecuteNonQuery();
            }

            using var reader = command.ExecuteReader();
            var rows = new List<WritData>();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                rows.Add(new WritData(row));
            }

            return rows;
        }

        // Table-named read: SELECT * FROM operation
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT * FROM \"{operation.Replace("\"", "\"\"")}\"";
            using var reader = command.ExecuteReader();
            var rows = new List<WritData>();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                rows.Add(new WritData(row));
            }

            return rows;
        }
    }

    private static object? InvokePowerShellModuleCapability(
        string operation,
        Dictionary<string, object?> arguments,
        string module)
    {
        var splat = string.Join("; ", arguments.Select(pair =>
            $"`${pair.Key} = {ToPowerShellLiteral(pair.Value)}"));
        var argList = string.Join(" ", arguments.Keys.Select(key => $"-{key} ${key}"));
        var script = $@"
$ErrorActionPreference = 'Stop'
Import-Module {ToPowerShellLiteral(module)} -ErrorAction Stop
{splat}
$result = & {operation} {argList} | ConvertTo-Json -Compress -Depth 8
Write-Output $result
";
        var psi = new ProcessStartInfo("pwsh", "-NoProfile -NonInteractive -Command -")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start pwsh.");
        process.StandardInput.Write(script);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output : error);
        }

        var trimmed = output.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        try
        {
            return ParseJson(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }

    private static string ToPowerShellLiteral(object? value) => value switch
    {
        null => "$null",
        bool flag => flag ? "$true" : "$false",
        string text => "'" + text.Replace("'", "''") + "'",
        _ => "'" + Text(value).Replace("'", "''") + "'"
    };

    private static string ResolveCapabilityPath(string endpoint, string? workingDirectory)
    {
        if (Path.IsPathRooted(endpoint))
        {
            return endpoint;
        }

        var directory = string.IsNullOrWhiteSpace(workingDirectory) ? Directory.GetCurrentDirectory() : workingDirectory;
        return Path.GetFullPath(Path.Combine(directory, endpoint));
    }
}
