using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Writ.Runtime;

/// <summary>
/// Run-time MCP client over stdio. One server process per command line, kept for the life of the process.
/// This is the only place in the .NET runtime that sends <c>tools/call</c>.
/// </summary>
public static class McpClient
{
    private static readonly ConcurrentDictionary<string, Session> Sessions = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, RestSession> RestSessions = new(StringComparer.Ordinal);
    private static readonly object RestGate = new();
    private static readonly HttpClient Http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    public static JsonElement CallTool(string commandLine, string workingDirectory, string tool, IReadOnlyDictionary<string, object> arguments, string transport = "stdio")
    {
        var result = string.Equals(transport, "rest", StringComparison.OrdinalIgnoreCase)
            ? CallRest(commandLine, tool, arguments)
            : CallStdio(commandLine, workingDirectory, tool, arguments);
        return Interpret(result, tool);
    }

    private static JsonElement CallStdio(string commandLine, string workingDirectory, string tool, IReadOnlyDictionary<string, object> arguments)
    {
        var session = Sessions.GetOrAdd(commandLine + "\n" + workingDirectory, _ => Session.Start(commandLine, workingDirectory));
        lock (session)
        {
            return session.Request("tools/call", new { name = tool, arguments });
        }
    }

    private static JsonElement Interpret(JsonElement result, string tool)
    {
        if (result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
        {
            throw new CapabilityException(FirstText(result) ?? $"MCP tool '{tool}' failed.");
        }

        if (result.TryGetProperty("structuredContent", out var structured))
        {
            return structured.Clone();
        }

        var text = FirstText(result);
        if (text is null)
        {
            return result.Clone();
        }

        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(text);
        }
    }

    private static JsonElement CallRest(string url, string tool, IReadOnlyDictionary<string, object> arguments)
    {
        if (!RestSessions.TryGetValue(url, out var session))
        {
            lock (RestGate)
            {
                if (!RestSessions.TryGetValue(url, out session))
                {
                    session = OpenRest(url);
                    RestSessions[url] = session;
                }
            }
        }

        lock (session)
        {
            var id = session.NextId++;
            return Post(url, session.SessionId, new
            {
                jsonrpc = "2.0",
                id,
                method = "tools/call",
                @params = new { name = tool, arguments }
            });
        }
    }

    private static RestSession OpenRest(string url)
    {
        var opened = PostRaw(url, null, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "writ-runtime", version = "0.1.0" }
            }
        });
        PostRaw(url, opened.SessionId, new { jsonrpc = "2.0", method = "notifications/initialized" });
        return new RestSession { SessionId = opened.SessionId, NextId = 2 };
    }

    private readonly record struct RestReply(JsonElement Result, string? SessionId);

    private sealed class RestSession
    {
        public string? SessionId { get; init; }
        public int NextId { get; set; }
    }

    private static JsonElement Post(string url, string? sessionId, object message) => PostRaw(url, sessionId, message).Result;

    private static RestReply PostRaw(string url, string? sessionId, object message)
    {
        var json = JsonSerializer.Serialize(message);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (!string.IsNullOrEmpty(sessionId))
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        }

        using var cancellation = new CancellationTokenSource(Timeout);
        try
        {
            using var response = Http.Send(request, cancellation.Token);
            var nextSession = sessionId;
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
            {
                nextSession = values.FirstOrDefault() ?? sessionId;
            }

            var body = response.Content.ReadAsStringAsync(cancellation.Token).GetAwaiter().GetResult();
            if ((int)response.StatusCode is 202 or 204 || string.IsNullOrWhiteSpace(body))
            {
                return new RestReply(default, nextSession);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new CapabilityException($"MCP server returned {(int)response.StatusCode}: {(body.Length <= 500 ? body : body[..500])}");
            }

            return new RestReply(ParseRpc(body), nextSession);
        }
        catch (OperationCanceledException)
        {
            throw new CapabilityException($"The MCP server did not answer within {Timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException ex)
        {
            throw new CapabilityException($"MCP call failed for '{url}': {ex.Message}");
        }
    }

    private static JsonElement ParseRpc(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            using var document = JsonDocument.Parse(trimmed);
            return Unwrap(document.RootElement.Clone());
        }

        JsonElement? last = null;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data:".Length..].Trim();
            if (!data.StartsWith('{'))
            {
                continue;
            }

            using var document = JsonDocument.Parse(data);
            last = Unwrap(document.RootElement.Clone());
        }

        return last ?? throw new CapabilityException("MCP server returned a response that is not JSON.");
    }

    private static JsonElement Unwrap(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            JsonElement? chosen = null;
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && (item.TryGetProperty("result", out _) || item.TryGetProperty("error", out _)))
                {
                    chosen = item;
                }
            }

            root = chosen ?? root;
        }

        if (root.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var m) ? m.GetString() : error.ToString();
            throw new CapabilityException($"MCP server returned an error: {message}");
        }

        return root.TryGetProperty("result", out var result) ? result.Clone() : root.Clone();
    }

    private static string? FirstText(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var type) && type.GetString() == "text" && item.TryGetProperty("text", out var text))
            {
                return text.GetString();
            }
        }

        return null;
    }

    private sealed class Session
    {
        private readonly Process _process;
        private int _nextId = 2;

        private Session(Process process) => _process = process;

        public static Session Start(string commandLine, string workingDirectory)
        {
            var trimmed = commandLine.Trim();
            string fileName, arguments;
            if (trimmed.StartsWith('"') && trimmed.IndexOf('"', 1) is var close && close > 0)
            {
                fileName = trimmed[1..close];
                arguments = trimmed[(close + 1)..].Trim();
            }
            else
            {
                var space = trimmed.IndexOf(' ');
                fileName = space < 0 ? trimmed : trimmed[..space];
                arguments = space < 0 ? "" : trimmed[(space + 1)..].Trim();
            }

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = string.IsNullOrEmpty(workingDirectory) ? Directory.GetCurrentDirectory() : workingDirectory
            }) ?? throw new CapabilityException($"Could not start MCP server '{commandLine}'.");
            _ = process.StandardError.ReadToEndAsync();

            var session = new Session(process);
            session.Send(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "writ-runtime", version = "0.1.0" } }
            });
            session.Receive(1);
            session.Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
            return session;
        }

        public JsonElement Request(string method, object parameters)
        {
            var id = _nextId++;
            Send(new { jsonrpc = "2.0", id, method, @params = parameters });
            return Receive(id);
        }

        private void Send(object message)
        {
            _process.StandardInput.WriteLine(JsonSerializer.Serialize(message));
            _process.StandardInput.Flush();
        }

        private JsonElement Receive(int id)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (DateTime.UtcNow < deadline)
            {
                var readTask = _process.StandardOutput.ReadLineAsync();
                if (!readTask.Wait(deadline - DateTime.UtcNow))
                {
                    break;
                }

                var line = readTask.Result ?? throw new CapabilityException("The MCP server closed its output.");
                if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{'))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var messageId) || messageId.ValueKind != JsonValueKind.Number || messageId.GetInt32() != id)
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var m) ? m.GetString() : error.ToString();
                    throw new CapabilityException($"MCP server returned an error: {message}");
                }

                return root.TryGetProperty("result", out var result) ? result.Clone() : default;
            }

            throw new CapabilityException($"The MCP server did not answer within {Timeout.TotalSeconds:0} seconds.");
        }
    }
}
