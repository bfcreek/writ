using System.Text.Json;
using System.Text.Json.Serialization;
using Writ.Compiler.Diagnostics;

namespace Writ.Compiler.Semantics;

/// <summary>
/// How <see cref="Compilation.CompilationPipeline"/> chooses a language model.
/// A project <c>writ.model.json</c> wins. <see cref="ReadUserFile"/> also reads
/// <c>~/.writ/model.json</c> when the project has none. An omitted file is the built-in stand-in.
/// </summary>
public sealed record ModelOptions
{
    /// <summary>Project <c>writ.model.json</c> only. Library callers stay independent of the home directory.</summary>
    public static ModelOptions ProjectOnly { get; } = new();

    public bool ReadUserFile { get; init; }
    public string? UserFilePath { get; init; }

    /// <summary>Profile name from <c>--model</c>. The file's <c>use</c> applies when this is omitted.</summary>
    public string? Profile { get; init; }

    /// <summary>Tests stand in for the network. The compiler disposes nothing it did not create.</summary>
    public HttpMessageHandler? Handler { get; init; }

    /// <summary>Reads a named environment variable. Tests supply a stand-in so they never touch the process environment.</summary>
    public Func<string, string?>? ReadEnvironment { get; init; }

    /// <summary>
    /// When set, each rule decision and each model call is written here.
    /// The command line passes standard error for <c>--debug</c>.
    /// </summary>
    public TextWriter? Debug { get; init; }
}

/// <summary>The model a compilation will call, and where that choice was written.</summary>
public sealed record ModelChoice(string Provider, string? Profile, string? Model, string Source);

public sealed record ResolvedModel(ILanguageModel Model, ModelChoice Choice);

/// <summary>
/// Reads <c>writ.model.json</c> (nearest file at or above the source) and, when asked,
/// <c>~/.writ/model.json</c>. The nearest file is the whole configuration.
/// A secret is named with <c>apiKeyEnv</c>; the file cannot hold the secret itself.
/// </summary>
public static class ModelSetup
{
    public const string ProjectFileName = "writ.model.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly SourceSpan Span = new(1, 1, 1, 1);

    public static string DefaultUserPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".writ", "model.json");

    /// <summary>Walks up from the source file, the same way <c>writ.constraints.json</c> is found.</summary>
    public static string? FindProjectFile(string? sourceFile)
    {
        if (string.IsNullOrWhiteSpace(sourceFile))
        {
            return null;
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(sourceFile));
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            var candidate = Path.Combine(dir, ProjectFileName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    /// <summary>Null when the file is invalid. The diagnostics say why.</summary>
    public static ResolvedModel? Resolve(string? sourceFile, ModelOptions options, DiagnosticBag diagnostics)
    {
        var project = FindProjectFile(sourceFile);
        var path = project;
        if (path is null && options.ReadUserFile)
        {
            var user = string.IsNullOrWhiteSpace(options.UserFilePath) ? DefaultUserPath() : options.UserFilePath;
            if (File.Exists(user))
            {
                path = Path.GetFullPath(user);
            }
        }

        if (path is null)
        {
            if (!string.IsNullOrWhiteSpace(options.Profile))
            {
                diagnostics.Error("E2107",
                    $"No {ProjectFileName} was found, so --model '{options.Profile.Trim()}' has nothing to select.",
                    sourceFile, Span);
                return null;
            }

            return BuiltIn();
        }

        var file = Read(path, diagnostics);
        if (file is null)
        {
            return null;
        }

        var selected = Select(file, path, options.Profile, diagnostics);
        if (selected is null)
        {
            return null;
        }

        return Build(selected.Value.Profile, selected.Value.Name, path, options, diagnostics);
    }

    private static ResolvedModel BuiltIn() =>
        new(new InnateLanguageModel(), new ModelChoice("innate", null, null, "built-in"));

    private static ModelFile? Read(string path, DiagnosticBag diagnostics)
    {
        try
        {
            var file = JsonSerializer.Deserialize<ModelFile>(File.ReadAllText(path), Options);
            if (file is null)
            {
                diagnostics.Error("E2107", $"{Path.GetFileName(path)} is empty.", path, Span);
            }

            return file;
        }
        catch (JsonException ex)
        {
            var hint = ex.Message.Contains("apiKey", StringComparison.OrdinalIgnoreCase)
                ? " Name the environment variable with apiKeyEnv; the file cannot hold a secret."
                : "";
            diagnostics.Error("E2107", $"{Path.GetFileName(path)} is not valid: {ex.Message}.{hint}", path, Span);
            return null;
        }
    }

    private static (ModelProfileFile Profile, string? Name)? Select(
        ModelFile file,
        string path,
        string? requested,
        DiagnosticBag diagnostics)
    {
        var name = Path.GetFileName(path);
        var listed = file.Models is { Count: > 0 };
        var flat = file.Provider is not null || file.Model is not null || file.Endpoint is not null || file.ApiKeyEnv is not null
            || file.Temperature is not null || file.TimeoutSeconds is not null;
        if (listed && flat)
        {
            diagnostics.Error("E2107",
                $"{name} sets a model and a models list. Use one of those forms.",
                path, Span);
            return null;
        }

        if (!listed)
        {
            if (!flat)
            {
                diagnostics.Error("E2107", $"{name} does not declare a model.", path, Span);
                return null;
            }

            if (string.IsNullOrWhiteSpace(file.Provider))
            {
                diagnostics.Error("E2107", $"{name} needs a provider: innate, ollama, openai, copilot, or compatible.", path, Span);
                return null;
            }

            var ask = requested?.Trim();
            if (!string.IsNullOrEmpty(ask) &&
                !ask.Equals(file.Provider.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Error("E2107",
                    $"{name} declares {file.Provider.Trim()}, so --model '{ask}' does not match. Add a models list to name more than one.",
                    path, Span);
                return null;
            }

            return (new ModelProfileFile
            {
                Provider = file.Provider,
                Model = file.Model,
                Endpoint = file.Endpoint,
                ApiKeyEnv = file.ApiKeyEnv,
                Temperature = file.Temperature,
                TimeoutSeconds = file.TimeoutSeconds
            }, null);
        }

        var profileName = string.IsNullOrWhiteSpace(requested) ? file.Use?.Trim() : requested.Trim();
        if (string.IsNullOrEmpty(profileName))
        {
            diagnostics.Error("E2107",
                $"{name} lists models but does not say which one to use. Set \"use\", or pass --model.",
                path, Span);
            return null;
        }

        var matches = file.Models!
            .Where(pair => pair.Key.Equals(profileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
        {
            var known = string.Join(", ", file.Models!.Keys.Order(StringComparer.OrdinalIgnoreCase));
            diagnostics.Error("E2107",
                $"{name} has no model named '{profileName}'. The names are: {known}.",
                path, Span);
            return null;
        }

        if (matches.Count > 1)
        {
            diagnostics.Error("E2107", $"{name} names '{profileName}' more than once.", path, Span);
            return null;
        }

        if (matches[0].Value is null)
        {
            diagnostics.Error("E2107", $"{name} names '{matches[0].Key}' without a provider.", path, Span);
            return null;
        }

        return (matches[0].Value, matches[0].Key);
    }

    private static ResolvedModel? Build(
        ModelProfileFile profile,
        string? profileName,
        string path,
        ModelOptions options,
        DiagnosticBag diagnostics)
    {
        var where = profileName is null ? Path.GetFileName(path) : $"'{profileName}' in {Path.GetFileName(path)}";
        if (string.IsNullOrWhiteSpace(profile.Provider) || !TryCanonical(profile.Provider, out var provider))
        {
            var shown = string.IsNullOrWhiteSpace(profile.Provider) ? "(missing)" : profile.Provider.Trim();
            diagnostics.Error("E2107",
                $"{where} has provider '{shown}'. Use innate, ollama, openai, copilot, or compatible.",
                path, Span);
            return null;
        }

        var model = string.IsNullOrWhiteSpace(profile.Model) ? null : profile.Model.Trim();
        var endpoint = string.IsNullOrWhiteSpace(profile.Endpoint) ? null : profile.Endpoint.Trim();
        var apiKeyEnv = string.IsNullOrWhiteSpace(profile.ApiKeyEnv) ? null : profile.ApiKeyEnv.Trim();
        var temperature = profile.Temperature ?? 0;
        var timeout = profile.TimeoutSeconds ?? 120;
        if (temperature is < 0 or > 2)
        {
            diagnostics.Error("E2107", $"{where} has temperature {temperature}. Use a value from 0 to 2.", path, Span);
            return null;
        }

        if (timeout is < 1 or > 600)
        {
            diagnostics.Error("E2107", $"{where} has timeoutSeconds {timeout}. Use a value from 1 to 600.", path, Span);
            return null;
        }

        if (provider == "innate")
        {
            return new ResolvedModel(new InnateLanguageModel(), new ModelChoice("innate", profileName, null, path));
        }

        endpoint = provider switch
        {
            "ollama" => endpoint ?? "http://127.0.0.1:11434/v1",
            "openai" => endpoint ?? "https://api.openai.com/v1",
            "copilot" => endpoint ?? "https://api.githubcopilot.com",
            _ => endpoint
        };

        if (provider is "openai" or "copilot")
        {
            apiKeyEnv ??= provider == "openai" ? "OPENAI_API_KEY" : "GITHUB_TOKEN";
        }

        if (model is null)
        {
            diagnostics.Error("E2107", $"{where} needs a model name.", path, Span);
            return null;
        }

        if (endpoint is null)
        {
            diagnostics.Error("E2107",
                $"{where} needs an endpoint, an http:// or https:// URL of an OpenAI-compatible chat API.",
                path, Span);
            return null;
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            diagnostics.Error("E2107", $"{where} has an endpoint that is not an http:// or https:// URL.", path, Span);
            return null;
        }

        string? apiKey = null;
        if (apiKeyEnv is not null)
        {
            var read = options.ReadEnvironment ?? Environment.GetEnvironmentVariable;
            apiKey = read(apiKeyEnv);
            if (provider is "openai" or "copilot" && string.IsNullOrWhiteSpace(apiKey))
            {
                diagnostics.Error("E2107",
                    $"{where} reads its API key from {apiKeyEnv}, and that variable is empty.",
                    path, Span);
                return null;
            }
        }

        var chat = new ChatLanguageModel(new ChatEndpoint(
            ChatUrl(endpoint),
            model,
            temperature,
            timeout,
            string.IsNullOrWhiteSpace(apiKey) ? null : apiKey,
            provider == "copilot",
            options.Handler), options.Debug);
        return new ResolvedModel(chat, new ModelChoice(provider, profileName, model, path));
    }

    /// <summary>Accepts the names a person would write, and returns the one the compiler stores.</summary>
    public static bool TryCanonical(string text, out string provider)
    {
        provider = text.Trim().ToLowerInvariant() switch
        {
            "innate" or "builtin" or "built-in" or "none" or "off" => "innate",
            "ollama" => "ollama",
            "openai" => "openai",
            "copilot" or "github-copilot" or "githubcopilot" => "copilot",
            "compatible" or "openai-compatible" or "custom" => "compatible",
            _ => ""
        };
        return provider.Length > 0;
    }

    private static string ChatUrl(string endpoint)
    {
        var trimmed = endpoint.TrimEnd('/');
        return trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + "/chat/completions";
    }

    private sealed class ModelFile
    {
        public string? Use { get; set; }
        public Dictionary<string, ModelProfileFile>? Models { get; set; }
        public string? Provider { get; set; }
        public string? Model { get; set; }
        public string? Endpoint { get; set; }
        public string? ApiKeyEnv { get; set; }
        public double? Temperature { get; set; }
        public int? TimeoutSeconds { get; set; }
    }

    private sealed class ModelProfileFile
    {
        public string? Provider { get; set; }
        public string? Model { get; set; }
        public string? Endpoint { get; set; }
        public string? ApiKeyEnv { get; set; }
        public double? Temperature { get; set; }
        public int? TimeoutSeconds { get; set; }
    }
}
