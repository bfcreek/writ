using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Writ.Compiler.Semantics;

/// <summary>One configured chat endpoint: Ollama, OpenAI, Copilot, or another OpenAI-compatible server.</summary>
public sealed record ChatEndpoint(
    string Url,
    string Model,
    double Temperature,
    int TimeoutSeconds,
    string? ApiKey,
    bool CopilotHeaders,
    HttpMessageHandler? Handler);

/// <summary>
/// Asks a chat model to pick catalog entries. The reply is parsed into a
/// <see cref="ModelReading"/>; <see cref="ModelReadings.Accept"/> still rejects anything outside the catalog.
/// </summary>
public sealed class ChatLanguageModel : ILanguageModel
{
    private static readonly JsonSerializerOptions BodyOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ChatEndpoint _endpoint;

    public ChatLanguageModel(ChatEndpoint endpoint) => _endpoint = endpoint;

    public async Task<ModelReading> ReadAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        using var client = _endpoint.Handler is null
            ? new HttpClient()
            : new HttpClient(_endpoint.Handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(_endpoint.TimeoutSeconds);

        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint.Url);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(_endpoint.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _endpoint.ApiKey);
        }

        if (_endpoint.CopilotHeaders)
        {
            message.Headers.TryAddWithoutValidation("Copilot-Integration-Id", "copilot-developer-cli");
        }

        var body = new ChatRequest
        {
            Model = _endpoint.Model,
            Temperature = _endpoint.Temperature,
            Stream = false,
            ResponseFormat = new JsonObjectFormat("json_object"),
            Messages =
            [
                new ChatMessage("system", ModelMessages.System),
                new ChatMessage("user", ModelMessages.Describe(request))
            ]
        };
        message.Content = new StringContent(JsonSerializer.Serialize(body, BodyOptions), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"The model did not answer within {_endpoint.TimeoutSeconds} seconds.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Could not reach {_endpoint.Url}: {ex.Message}");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = text.Length > 300 ? text[..300] : text;
                throw new InvalidOperationException($"{(int)response.StatusCode} from {_endpoint.Url}: {detail}");
            }

            return ModelMessages.Parse(ReadContent(text));
        }
    }

    private static string ReadContent(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var content = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidOperationException("The model returned an empty message.");
            }

            return content;
        }
        catch (InvalidOperationException ex) when (ex.Message == "The model returned an empty message.")
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException($"The model returned a response this compiler cannot read: {ex.Message}");
        }
    }

    private sealed class ChatRequest
    {
        public required string Model { get; init; }
        public required double Temperature { get; init; }
        public required bool Stream { get; init; }
        [JsonPropertyName("response_format")]
        public required JsonObjectFormat ResponseFormat { get; init; }
        public required IReadOnlyList<ChatMessage> Messages { get; init; }
    }

    private sealed record JsonObjectFormat(string Type);

    private sealed record ChatMessage(string Role, string Content);
}

/// <summary>The prompt a chat model sees, and the reading parsed from its reply.</summary>
public static class ModelMessages
{
    public const string System = """
        You choose entries from a closed catalog for the Writ compiler.
        Reply with one JSON object and no other text:
        {"selections":[{"id":"","subject":null,"bindings":{},"rationale":"","order":0}],"failureReason":null}
        Rules:
        - id is copied from types or operations in the request. Never invent an id.
        - subject is the field or clause the entry is about, or null.
        - bindings maps a catalog argument name to a symbol name or a quoted literal from the text.
        - order is the order operations run, starting at 0. A step moves earlier when the text says but first, before, or after. then keeps spoken order.
        - A specific rule in the request wins over a guess.
        - When nothing in the catalog fits, selections is empty and failureReason says why in one sentence.
        """;

    private static readonly JsonSerializerOptions RequestOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReplyOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string Describe(ModelRequest request) =>
        JsonSerializer.Serialize(new
        {
            request.Text,
            request.Types,
            request.Operations,
            request.Subjects,
            request.Rules,
            request.Symbols,
            request.Records
        }, RequestOptions);

    public static ModelReading Parse(string content)
    {
        ReplyFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ReplyFile>(Unwrap(content), ReplyOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The model did not return a reading: {ex.Message}");
        }

        if (file is null)
        {
            throw new InvalidOperationException("The model did not return a reading.");
        }

        var selections = new List<ModelSelection>();
        foreach (var item in file.Selections ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.Id))
            {
                throw new InvalidOperationException("The model returned a selection with no id.");
            }

            selections.Add(new ModelSelection(
                item.Id.Trim(),
                string.IsNullOrWhiteSpace(item.Subject) ? null : item.Subject.Trim(),
                Coerce(item.Bindings),
                item.Rationale?.Trim() ?? "",
                item.Order ?? 0));
        }

        var reason = string.IsNullOrWhiteSpace(file.FailureReason) ? null : file.FailureReason.Trim();
        return new ModelReading(selections, reason);
    }

    private static string Unwrap(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = trimmed.IndexOf('\n');
            var close = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (newline >= 0 && close > newline)
            {
                trimmed = trimmed[(newline + 1)..close].Trim();
            }
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start ? trimmed[start..(end + 1)] : trimmed;
    }

    private static Dictionary<string, string> Coerce(Dictionary<string, JsonElement>? bindings)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (bindings is null)
        {
            return result;
        }

        foreach (var (key, value) in bindings)
        {
            result[key] = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "",
                _ => value.ToString()
            };
        }

        return result;
    }

    private sealed class ReplyFile
    {
        public List<ReplySelection>? Selections { get; set; }
        public string? FailureReason { get; set; }
    }

    private sealed class ReplySelection
    {
        public string? Id { get; set; }
        public string? Subject { get; set; }
        public Dictionary<string, JsonElement>? Bindings { get; set; }
        public string? Rationale { get; set; }
        public int? Order { get; set; }
    }
}
