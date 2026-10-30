using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Writ.Runtime;

public static partial class Machine
{
    private sealed class StdioMcpSession
    {
        public required Process Process { get; init; }
        public required string CommandLine { get; init; }
        public int NextId;
        public readonly object Gate = new();
    }

    private sealed record RestMcpSession(string SessionId);

    private static readonly ConcurrentDictionary<string, StdioMcpSession> McpSessions = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, RestMcpSession> McpRestSessions = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, bool> McpValidated = new(StringComparer.Ordinal);
    private static int McpRestNextId = 2;
    private static readonly HttpClient McpHttp = new();

    public static void ResetMcpSessions()
    {
        foreach (var session in McpSessions.Values)
        {
            try
            {
                if (!session.Process.HasExited)
                {
                    session.Process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort test/runtime cleanup.
            }
        }

        McpSessions.Clear();
        McpRestSessions.Clear();
        McpValidated.Clear();
    }

    public static object? InvokeMcpCapability(
        string alias,
        string operation,
        Dictionary<string, object?> arguments,
        string endpoint,
        string transport,
        string? workingDirectory)
    {
        AssertMcpTool(alias, operation, endpoint, transport, workingDirectory);
        Ambient.Value?.CallLog.Add($"{alias}.{operation}");
        return transport.Equals("rest", StringComparison.OrdinalIgnoreCase)
            ? InvokeMcpRest(endpoint, operation, arguments)
            : InvokeMcpTool(endpoint, workingDirectory ?? "", operation, arguments);
    }

    public static void AssertMcpTools(string requiredJson)
    {
        if (string.IsNullOrWhiteSpace(requiredJson) || requiredJson == "[]")
        {
            return;
        }

        using var document = JsonDocument.Parse(requiredJson);
        var required = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToList()
            : [];
        foreach (var group in required.GroupBy(tool =>
                     (Transport: TextProperty(tool, "transport"),
                      Endpoint: TextProperty(tool, "endpoint"),
                      WorkingDirectory: TextProperty(tool, "workingDirectory"))))
        {
            var available = ListMcpToolNames(group.Key.Transport, group.Key.Endpoint, group.Key.WorkingDirectory)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var tool in group)
            {
                var operation = TextProperty(tool, "operation");
                if (!available.Contains(operation))
                {
                    throw new InvalidOperationException($"Required MCP tool '{TextProperty(tool, "alias")}.{operation}' is not available.");
                }
            }
        }
    }

    private static void AssertMcpTool(string alias, string operation, string endpoint, string transport, string? workingDirectory)
    {
        var required = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, string>
            {
                ["alias"] = alias,
                ["operation"] = operation,
                ["endpoint"] = endpoint,
                ["transport"] = string.IsNullOrWhiteSpace(transport) ? "stdio" : transport,
                ["workingDirectory"] = workingDirectory ?? ""
            }
        });
        AssertMcpTools(required);
    }

    private static object? InvokeMcpTool(string commandLine, string workingDirectory, string tool, Dictionary<string, object?> arguments)
    {
        var session = GetMcpSession(commandLine, workingDirectory);
        lock (session.Gate)
        {
            var id = session.NextId++;
            SendMcp(session, new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = "tools/call",
                ["params"] = new Dictionary<string, object?>
                {
                    ["name"] = tool,
                    ["arguments"] = Plain(arguments)
                }
            });
            return ReadMcpToolResult(ReceiveMcp(session, id), tool);
        }
    }

    private static StdioMcpSession GetMcpSession(string commandLine, string workingDirectory)
    {
        var key = commandLine + "\n" + workingDirectory;
        return McpSessions.GetOrAdd(key, ignored =>
        {
            var (fileName, arguments) = SplitCommandLine(commandLine);
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                psi.WorkingDirectory = workingDirectory;
            }

            var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start MCP server '{commandLine}'.");
            _ = process.StandardError.ReadToEndAsync();
            var session = new StdioMcpSession { Process = process, CommandLine = commandLine, NextId = 2 };
            SendMcp(session, new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "initialize",
                ["params"] = new Dictionary<string, object?>
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new Dictionary<string, object?>(),
                    ["clientInfo"] = new Dictionary<string, object?> { ["name"] = "writ-csharp", ["version"] = "0.1.0" }
                }
            });
            _ = ReceiveMcp(session, 1);
            SendMcp(session, new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
            return session;
        });
    }

    private static void SendMcp(StdioMcpSession session, object message)
    {
        session.Process.StandardInput.WriteLine(JsonSerializer.Serialize(Plain(message)));
        session.Process.StandardInput.Flush();
    }

    private static WritData ReceiveMcp(StdioMcpSession session, int id)
    {
        while (true)
        {
            var line = session.Process.StandardOutput.ReadLine();
            if (line is null)
            {
                throw new InvalidOperationException($"The MCP server '{session.CommandLine}' closed its output.");
            }

            if (!line.TrimStart().StartsWith('{'))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var idElement) || idElement.GetInt32() != id)
            {
                continue;
            }

            if (root.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException("MCP server returned an error: " + Text(Field(ToClr(error), "message", optional: true)));
            }

            return ConvertTo<WritData>(root.TryGetProperty("result", out var result) ? ToClr(result) : null);
        }
    }

    private static object? InvokeMcpRest(string url, string tool, Dictionary<string, object?> arguments)
    {
        var session = GetMcpRestSession(url);
        var id = Interlocked.Increment(ref McpRestNextId);
        var call = SendMcpRest(url, session.SessionId, new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/call",
            ["params"] = new Dictionary<string, object?>
            {
                ["name"] = tool,
                ["arguments"] = Plain(arguments)
            }
        });
        return call.Result is null
            ? throw new InvalidOperationException($"MCP tool '{tool}' returned an empty response.")
            : ReadMcpToolResult(call.Result, tool);
    }

    private static RestMcpSession GetMcpRestSession(string url) =>
        McpRestSessions.GetOrAdd(url, endpoint =>
        {
            var init = SendMcpRest(endpoint, "", new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "initialize",
                ["params"] = new Dictionary<string, object?>
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new Dictionary<string, object?>(),
                    ["clientInfo"] = new Dictionary<string, object?> { ["name"] = "writ-csharp", ["version"] = "0.1.0" }
                }
            });
            var session = new RestMcpSession(init.SessionId ?? "");
            _ = SendMcpRest(endpoint, session.SessionId, new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
            return session;
        });

    private static (string? SessionId, WritData? Result) SendMcpRest(string url, string sessionId, object message)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrEmpty(sessionId))
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        }

        request.Content = new StringContent(JsonSerializer.Serialize(Plain(message)), Encoding.UTF8, "application/json");
        using var response = McpHttp.Send(request);
        var nextSession = response.Headers.TryGetValues("Mcp-Session-Id", out var values)
            ? values.FirstOrDefault()
            : sessionId;
        var payload = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var trimmed = payload.Trim();
        if ((int)response.StatusCode is 202 or 204 || trimmed.Length == 0)
        {
            return (nextSession, null);
        }

        if ((int)response.StatusCode >= 400)
        {
            throw new InvalidOperationException($"MCP server returned {(int)response.StatusCode}: {trimmed}");
        }

        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
        {
            var jsonLine = payload.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(line => line[5..].Trim())
                .FirstOrDefault(line => line.StartsWith('{'));
            trimmed = jsonLine ?? throw new InvalidOperationException("MCP server returned a response that is not JSON.");
        }

        using var document = JsonDocument.Parse(trimmed);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException("MCP server returned an error: " + Text(Field(ToClr(error), "message", optional: true)));
        }

        return (nextSession, ConvertTo<WritData>(root.TryGetProperty("result", out var result) ? ToClr(result) : ToClr(root)));
    }

    private static IReadOnlyList<string> ListMcpToolNames(string transport, string endpoint, string workingDirectory)
    {
        WritData result;
        if (transport.Equals("rest", StringComparison.OrdinalIgnoreCase))
        {
            var session = GetMcpRestSession(endpoint);
            var id = Interlocked.Increment(ref McpRestNextId);
            result = SendMcpRest(endpoint, session.SessionId, new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = "tools/list",
                ["params"] = new Dictionary<string, object?>()
            }).Result ?? new WritData();
        }
        else
        {
            var session = GetMcpSession(endpoint, workingDirectory);
            lock (session.Gate)
            {
                var id = session.NextId++;
                SendMcp(session, new Dictionary<string, object?>
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["method"] = "tools/list",
                    ["params"] = new Dictionary<string, object?>()
                });
                result = ReceiveMcp(session, id);
            }
        }

        if (!TryField(result, "tools", out var tools) || tools is not System.Collections.IEnumerable enumerable)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var tool in enumerable)
        {
            if (TryField(tool, "name", out var name) && name is not null)
            {
                names.Add(Text(name));
            }
        }

        return names;
    }

    private static object? ReadMcpToolResult(WritData result, string tool)
    {
        var text = default(string);
        if (TryField(result, "content", out var content) && content is System.Collections.IEnumerable items)
        {
            foreach (var item in items)
            {
                if (Text(Field(item, "type", optional: true)) == "text")
                {
                    text = Text(Field(item, "text", optional: true));
                    break;
                }
            }
        }

        if (ToBoolean(Field(result, "isError", optional: true)))
        {
            throw new InvalidOperationException(text ?? $"MCP tool '{tool}' failed.");
        }

        if (TryField(result, "structuredContent", out var structured))
        {
            return structured;
        }

        if (text is null)
        {
            return result;
        }

        try
        {
            return ParseJson(text);
        }
        catch
        {
            return text;
        }
    }

    private static (string FileName, string Arguments) SplitCommandLine(string commandLine)
    {
        var trimmed = commandLine.Trim();
        if (trimmed.StartsWith('"'))
        {
            var close = trimmed.IndexOf('"', 1);
            if (close > 0)
            {
                return (trimmed[1..close], trimmed[(close + 1)..].Trim());
            }
        }

        var space = trimmed.IndexOf(' ');
        return space < 0 ? (trimmed, "") : (trimmed[..space], trimmed[(space + 1)..].Trim());
    }

    private static object? Plain(object? value) => value switch
    {
        null => null,
        WritData data => data.ToDictionary().ToDictionary(pair => pair.Key, pair => Plain(pair.Value), StringComparer.OrdinalIgnoreCase),
        IReadOnlyDictionary<string, object?> dictionary => dictionary.ToDictionary(pair => pair.Key, pair => Plain(pair.Value), StringComparer.OrdinalIgnoreCase),
        System.Collections.IDictionary dictionary => dictionary.Keys.Cast<object>().ToDictionary(key => Text(key), key => Plain(dictionary[key])),
        System.Collections.IEnumerable enumerable and not string => enumerable.Cast<object?>().Select(Plain).ToList(),
        _ => value
    };

    private static string TextProperty(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
