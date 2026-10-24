using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Writ.Compiler.Capabilities;

public sealed record McpToolInfo(
    string Name,
    string? Description,
    JsonElement InputSchema,
    bool ReadOnly,
    bool Destructive,
    bool Idempotent);

public sealed record McpToolList(IReadOnlyList<McpToolInfo> Tools, string Hash);

/// <summary>
/// Compile-time MCP discovery over stdio or HTTP. This client speaks exactly two requests,
/// <c>initialize</c> and <c>tools/list</c>. It has no way to send <c>tools/call</c>:
/// the compiler discovers tools and never invokes them.
/// </summary>
public static class McpDiscovery
{
    private static readonly HttpClient Http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    public static bool IsRest(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        var trimmed = endpoint.Trim();
        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    public static string TransportOf(string? endpoint) => IsRest(endpoint) ? "rest" : "stdio";

    public static McpToolList ListTools(string endpoint, string? workingDirectory)
    {
        if (IsRest(endpoint))
        {
            return ListToolsRest(endpoint.Trim());
        }

        var commandLine = endpoint;
        var (fileName, arguments) = SplitCommandLine(commandLine);
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory()
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start MCP server '{commandLine}'.");
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            Send(process, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "writ", version = "0.1.0" }
                }
            });
            _ = Receive(process, 1);
            Send(process, new { jsonrpc = "2.0", method = "notifications/initialized" });
            Send(process, new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } });
            var response = Receive(process, 2);
            return ParseTools(response);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            var err = stderr.IsCompleted ? stderr.Result.Trim() : "";
            throw new InvalidOperationException($"MCP discovery failed for '{commandLine}': {ex.Message} {err}".Trim(), ex);
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(2000))
                {
                    process.Kill(true);
                }
            }
            catch
            {
                // The server is being discarded; a failure to stop it cleanly is not an error for the caller.
            }
        }
    }

    private static McpToolList ListToolsRest(string url)
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        try
        {
            var session = Post(url, null, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "writ", version = "0.1.0" }
                }
            }, cancellation.Token).SessionId;
            Post(url, session, new { jsonrpc = "2.0", method = "notifications/initialized" }, cancellation.Token);
            var listed = Post(url, session, new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } }, cancellation.Token);
            return ParseTools(listed.Result);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"The MCP server did not answer within {Timeout.TotalSeconds:0} seconds.");
        }
    }

    private readonly record struct RpcReply(JsonElement Result, string? SessionId);

    private static RpcReply Post(string url, string? sessionId, object message, CancellationToken cancellation)
    {
        var json = JsonSerializer.Serialize(message);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (!string.IsNullOrEmpty(sessionId))
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        }

        try
        {
            using var response = Http.Send(request, cancellation);
            var nextSession = sessionId;
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
            {
                nextSession = values.FirstOrDefault() ?? sessionId;
            }

            var body = response.Content.ReadAsStringAsync(cancellation).GetAwaiter().GetResult();
            if ((int)response.StatusCode is 202 or 204 || string.IsNullOrWhiteSpace(body))
            {
                return new RpcReply(default, nextSession);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"MCP server returned {(int)response.StatusCode}: {Trim(body)}");
            }

            return new RpcReply(ParseRpc(body), nextSession);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"MCP discovery failed for '{url}': {ex.Message}", ex);
        }
    }

    private static JsonElement ParseRpc(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            using var document = JsonDocument.Parse(trimmed);
            return Unwrap(SelectMessage(document.RootElement).Clone());
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
            if (!data.StartsWith('{') && !data.StartsWith('['))
            {
                continue;
            }

            using var document = JsonDocument.Parse(data);
            last = Unwrap(SelectMessage(document.RootElement).Clone());
        }

        return last ?? throw new InvalidOperationException("MCP server returned a response that is not JSON.");
    }

    private static JsonElement SelectMessage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return root;
        }

        JsonElement? fallback = null;
        JsonElement? chosen = null;
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            fallback = item;
            if (item.TryGetProperty("result", out _) || item.TryGetProperty("error", out _))
            {
                chosen = item;
            }
        }

        return chosen ?? fallback ?? root;
    }

    private static JsonElement Unwrap(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var m) ? m.GetString() : error.ToString();
            throw new InvalidOperationException($"MCP server returned an error: {message}");
        }

        return root.TryGetProperty("result", out var result) ? result.Clone() : root.Clone();
    }

    private static McpToolList ParseTools(JsonElement response)
    {
        var tools = new List<McpToolInfo>();
        if (response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("tools", out var array) &&
            array.ValueKind == JsonValueKind.Array)
        {
            foreach (var tool in array.EnumerateArray())
            {
                var name = tool.GetProperty("name").GetString() ?? "";
                var description = tool.TryGetProperty("description", out var d) ? d.GetString() : null;
                var schema = tool.TryGetProperty("inputSchema", out var s) ? s.Clone() : default;
                var readOnly = false;
                var destructive = true;
                var idempotent = false;
                if (tool.TryGetProperty("annotations", out var annotations) && annotations.ValueKind == JsonValueKind.Object)
                {
                    readOnly = annotations.TryGetProperty("readOnlyHint", out var r) && r.ValueKind == JsonValueKind.True;
                    destructive = !annotations.TryGetProperty("destructiveHint", out var x) || x.ValueKind != JsonValueKind.False;
                    idempotent = annotations.TryGetProperty("idempotentHint", out var i) && i.ValueKind == JsonValueKind.True;
                }

                tools.Add(new McpToolInfo(name, description, schema, readOnly, destructive, idempotent));
            }
        }

        tools.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return new McpToolList(tools, HashTools(tools));
    }

    private static string Trim(string text) => text.Length <= 500 ? text : text[..500];

    public static (string FileName, string Arguments) SplitCommandLine(string commandLine)
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

    private static void Send(Process process, object message)
    {
        process.StandardInput.WriteLine(JsonSerializer.Serialize(message));
        process.StandardInput.Flush();
    }

    private static JsonElement Receive(Process process, int id)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            var readTask = process.StandardOutput.ReadLineAsync();
            if (!readTask.Wait(deadline - DateTime.UtcNow))
            {
                break;
            }

            var line = readTask.Result;
            if (line is null)
            {
                throw new InvalidOperationException("The MCP server closed its output before answering.");
            }

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
                throw new InvalidOperationException($"MCP server returned an error: {message}");
            }

            return root.TryGetProperty("result", out var result) ? result.Clone() : default;
        }

        throw new TimeoutException($"The MCP server did not answer within {Timeout.TotalSeconds:0} seconds.");
    }

    private static string HashTools(IReadOnlyList<McpToolInfo> tools)
    {
        var canonical = new StringBuilder();
        foreach (var tool in tools)
        {
            canonical.Append(tool.Name).Append('\n');
            Canonical(tool.InputSchema, canonical);
            canonical.Append('\n');
            canonical.Append(tool.ReadOnly ? "ro" : "rw").Append('\n');
        }

        return "sha256:" + System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    /// <summary>JSON with object keys sorted, so servers that emit keys in varying order hash the same.</summary>
    public static void Canonical(JsonElement element, StringBuilder into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                into.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        into.Append(',');
                    }

                    first = false;
                    into.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                    Canonical(property.Value, into);
                }

                into.Append('}');
                break;
            case JsonValueKind.Array:
                into.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        into.Append(',');
                    }

                    firstItem = false;
                    Canonical(item, into);
                }

                into.Append(']');
                break;
            case JsonValueKind.Undefined:
                break;
            default:
                into.Append(element.GetRawText());
                break;
        }
    }
}
