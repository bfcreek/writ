using System.Text.Json;
using Writ.Compiler.Diagnostics;
using Writ.Compiler.Ir;

namespace Writ.Compiler.Capabilities;

public sealed record BoundArgument(string Name, IrType Type, bool Required);

public sealed record BoundOperation(
    string Name,
    IReadOnlyList<BoundArgument> Arguments,
    IrType ResultType,
    IReadOnlyList<EffectKind> Effects);

/// <summary>Where a capability comes from: a manifest file ("file") or an MCP server command line ("mcp").</summary>
public sealed record CapabilitySource(string Kind, string Location)
{
    public static CapabilitySource File(string path) => new("file", path);
    public static CapabilitySource Mcp(string commandLine) => new("mcp", commandLine);
    public override string ToString() => Kind == "file" ? Location : $"{Kind}:{Location}";
}

public sealed class LoadedCapability
{
    public required string Alias { get; init; }
    public required string VersionHash { get; init; }
    public required string ManifestPath { get; init; }
    public required IReadOnlyList<EffectKind> GrantedEffects { get; init; }
    public required IReadOnlyList<string> AllowedOperations { get; init; }
    public required IReadOnlyDictionary<string, BoundOperation> Operations { get; init; }
    public required IReadOnlyList<IrTypeDecl> Types { get; init; }

    /// <summary>mock | mcp | powershell-module | http</summary>
    public string Provider { get; init; } = "mock";

    /// <summary>Provider-specific locator: the MCP command line, module name, or base URL.</summary>
    public string? Endpoint { get; init; }

    /// <summary>stdio or rest, for an MCP server. REST is an http or https endpoint.</summary>
    public string Transport { get; init; } = "stdio";

    /// <summary>Directory that relative paths in <see cref="Endpoint"/> are resolved against.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>What the lockfile pins: the manifest content hash, or the MCP tool list hash.</summary>
    public string DiscoveryHash { get; init; } = "";

    public CapabilitySource Source { get; init; } = CapabilitySource.File("");
}

/// <summary>
/// Compile-time discovery only. Implementations must not perform effectful calls.
/// </summary>
public interface ICapabilityLoader
{
    LoadedCapability? Load(string alias, CapabilitySource source, string? sourceFile, DiagnosticBag diagnostics, SourceSpan span, string? workingDirectory = null);
}

public sealed class FileCapabilityLoader : ICapabilityLoader
{
    public int Discoveries { get; private set; }

    public LoadedCapability? Load(string alias, CapabilitySource source, string? sourceFile, DiagnosticBag diagnostics, SourceSpan span, string? workingDirectory = null)
    {
        Discoveries++;
        if (source.Kind == "mcp")
        {
            return LoadFromMcp(alias, source, sourceFile, diagnostics, span, workingDirectory);
        }

        var fullPath = ResolvePath(source.Location, sourceFile);
        if (!File.Exists(fullPath))
        {
            diagnostics.Error("E4001", $"Capability manifest not found: {source.Location}", sourceFile, span);
            return null;
        }

        CapabilityFile? file;
        string json;
        try
        {
            json = File.ReadAllText(fullPath);
            file = JsonSerializer.Deserialize<CapabilityFile>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            diagnostics.Error("E4001", $"Capability manifest is not valid JSON: {ex.Message}", sourceFile, span);
            return null;
        }

        if (file is null)
        {
            diagnostics.Error("E4001", "Capability manifest is empty.", sourceFile, span);
            return null;
        }

        var errors = new List<string>();
        var records = new Dictionary<string, IrRecordType>(StringComparer.Ordinal);
        foreach (var type in file.Types ?? [])
        {
            if (string.IsNullOrWhiteSpace(type.Name))
            {
                errors.Add("A capability type is missing its name.");
                continue;
            }

            if (!records.TryAdd(type.Name, new IrRecordType(type.Name, [])))
            {
                errors.Add($"Duplicate capability type '{type.Name}'.");
            }
        }

        var typeDecls = new List<IrTypeDecl>();
        foreach (var type in file.Types ?? [])
        {
            if (string.IsNullOrWhiteSpace(type.Name) || !records.ContainsKey(type.Name))
            {
                continue;
            }

            var fields = new List<IrField>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in type.Fields ?? [])
            {
                if (string.IsNullOrWhiteSpace(field.Name) || string.IsNullOrWhiteSpace(field.Type))
                {
                    errors.Add($"Type '{type.Name}' has a field without a name or type.");
                    continue;
                }

                if (!seen.Add(field.Name))
                {
                    errors.Add($"Type '{type.Name}' declares '{field.Name}' more than once.");
                    continue;
                }

                var fieldType = ParseType(field.Type, records, errors);
                if (fieldType is not null)
                {
                    fields.Add(new IrField(field.Name, fieldType));
                }
            }

            var record = new IrRecordType(type.Name, fields);
            records[type.Name] = record;
            typeDecls.Add(new IrTypeDecl(type.Name, fields, span, record));
        }

        var granted = new List<EffectKind>();
        foreach (var effectName in file.GrantedEffects ?? [])
        {
            if (!TryParseEffect(effectName, out var effect))
            {
                errors.Add($"Unknown granted effect '{effectName}'.");
                continue;
            }

            granted.Add(effect);
        }

        var operations = new Dictionary<string, BoundOperation>(StringComparer.Ordinal);
        foreach (var operation in file.Operations ?? [])
        {
            if (string.IsNullOrWhiteSpace(operation.Name))
            {
                errors.Add("A capability operation is missing its name.");
                continue;
            }

            if (operations.ContainsKey(operation.Name))
            {
                errors.Add($"Duplicate operation '{operation.Name}'.");
                continue;
            }

            var arguments = new List<BoundArgument>();
            foreach (var argument in operation.Arguments ?? [])
            {
                if (string.IsNullOrWhiteSpace(argument.Name) || string.IsNullOrWhiteSpace(argument.Type))
                {
                    errors.Add($"Operation '{operation.Name}' has an argument without a name or type.");
                    continue;
                }

                var argumentType = ParseType(argument.Type, records, errors);
                if (argumentType is not null)
                {
                    arguments.Add(new BoundArgument(argument.Name, argumentType, argument.Required ?? true));
                }
            }

            if (string.IsNullOrWhiteSpace(operation.ResultType))
            {
                errors.Add($"Operation '{operation.Name}' is missing a result type.");
                continue;
            }

            var resultType = ParseType(operation.ResultType, records, errors);
            var effects = new List<EffectKind>();
            foreach (var effectName in operation.Effects ?? [])
            {
                if (!TryParseEffect(effectName, out var effect))
                {
                    errors.Add($"Operation '{operation.Name}' has unknown effect '{effectName}'.");
                    continue;
                }

                effects.Add(effect);
            }

            if (resultType is null)
            {
                continue;
            }

            operations[operation.Name] = new BoundOperation(operation.Name, arguments, resultType, effects);
        }

        var provider = (file.Provider?.Kind ?? (file.Mock is not null ? "mock" : "mock")).Trim().ToLowerInvariant();
        var endpoint = file.Provider?.Command ?? file.Provider?.Module ?? file.Provider?.BaseUrl;
        var manifestDirectory = Path.GetDirectoryName(fullPath);
        var discoveryHash = Sha256(json);

        switch (provider)
        {
            case "mock":
                break;
            case "mcp":
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    errors.Add("An MCP provider needs a 'command' or a 'baseUrl'.");
                    break;
                }

                // Discovery: confirm the server really offers every operation the manifest names.
                try
                {
                    var tools = McpDiscovery.ListTools(endpoint, manifestDirectory);
                    var offered = tools.Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
                    foreach (var name in operations.Keys)
                    {
                        if (!offered.Contains(name))
                        {
                            errors.Add($"The MCP server does not offer a tool named '{name}'. It offers: {string.Join(", ", offered.Order(StringComparer.Ordinal))}.");
                        }
                    }

                    discoveryHash = tools.Hash;
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
                {
                    errors.Add(ex.Message);
                }

                break;
            case "powershell-module":
            case "http":
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    errors.Add($"A '{provider}' provider needs a '{(provider == "http" ? "baseUrl" : "module")}'.");
                }

                break;
            default:
                errors.Add($"Unknown provider kind '{provider}'. Use mock, mcp, powershell-module, or http.");
                break;
        }

        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                diagnostics.Error("E4001", error, sourceFile, span);
            }

            return null;
        }

        return new LoadedCapability
        {
            Alias = alias,
            VersionHash = string.IsNullOrWhiteSpace(file.VersionHash) ? "unversioned" : file.VersionHash,
            ManifestPath = fullPath,
            GrantedEffects = granted,
            AllowedOperations = file.AllowedOperations ?? [],
            Operations = operations,
            Types = typeDecls,
            Provider = provider,
            Endpoint = endpoint,
            Transport = provider == "mcp" ? McpDiscovery.TransportOf(endpoint) : "stdio",
            WorkingDirectory = manifestDirectory,
            DiscoveryHash = discoveryHash,
            Source = source
        };
    }

    /// <summary>
    /// "Use payments from mcp "pwsh -File server.ps1"": the tool list is the manifest.
    /// Arguments come from each tool's input schema; results are dynamic until the program
    /// says what they are ("the response as a list of Payment").
    /// </summary>
    private static LoadedCapability? LoadFromMcp(string alias, CapabilitySource source, string? sourceFile, DiagnosticBag diagnostics, SourceSpan span, string? workingDirectory)
    {
        workingDirectory ??= string.IsNullOrEmpty(sourceFile) ? Directory.GetCurrentDirectory() : Path.GetDirectoryName(Path.GetFullPath(sourceFile));
        McpToolList tools;
        try
        {
            tools = McpDiscovery.ListTools(source.Location, workingDirectory);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            diagnostics.Error("E4001", $"Could not discover MCP tools for '{alias}': {ex.Message}", sourceFile, span);
            return null;
        }

        var operations = new Dictionary<string, BoundOperation>(StringComparer.Ordinal);
        foreach (var tool in tools.Tools)
        {
            var arguments = new List<BoundArgument>();
            var required = new HashSet<string>(StringComparer.Ordinal);
            if (tool.InputSchema.ValueKind == JsonValueKind.Object)
            {
                if (tool.InputSchema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in req.EnumerateArray())
                    {
                        if (item.GetString() is { } r)
                        {
                            required.Add(r);
                        }
                    }
                }

                if (tool.InputSchema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in props.EnumerateObject())
                    {
                        arguments.Add(new BoundArgument(prop.Name, SchemaType(prop.Value), required.Contains(prop.Name)));
                    }
                }
            }

            var effect = tool.ReadOnly ? EffectKind.NetworkRead : EffectKind.ExternalState;
            operations[tool.Name] = new BoundOperation(tool.Name, arguments, IrDynamicType.Instance, [effect]);
        }

        return new LoadedCapability
        {
            Alias = alias,
            VersionHash = tools.Hash,
            ManifestPath = "",
            GrantedEffects = [],
            AllowedOperations = operations.Keys.ToList(),
            Operations = operations,
            Types = [],
            Provider = "mcp",
            Endpoint = source.Location,
            Transport = McpDiscovery.TransportOf(source.Location),
            WorkingDirectory = workingDirectory,
            DiscoveryHash = tools.Hash,
            Source = source
        };
    }

    private static IrType SchemaType(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type))
        {
            return IrDynamicType.Instance;
        }

        var kind = type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(t => t.GetString()).FirstOrDefault(t => t != "null")
            : type.GetString();
        return kind switch
        {
            "string" => IrPrimitiveType.String,
            "integer" => IrPrimitiveType.Integer,
            "number" => IrPrimitiveType.Decimal,
            "boolean" => IrPrimitiveType.Boolean,
            "array" => new IrListType(schema.TryGetProperty("items", out var items) ? SchemaType(items) : IrDynamicType.Instance),
            _ => IrDynamicType.Instance
        };
    }

    public static string Sha256(string text) =>
        "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string ResolvePath(string manifestPath, string? sourceFile)
    {
        if (Path.IsPathRooted(manifestPath))
        {
            return manifestPath;
        }

        var directory = string.IsNullOrEmpty(sourceFile) ? Directory.GetCurrentDirectory() : Path.GetDirectoryName(sourceFile)!;
        return Path.GetFullPath(Path.Combine(directory, manifestPath));
    }

    private static IrType? ParseType(string text, IReadOnlyDictionary<string, IrRecordType> records, List<string> errors)
    {
        var trimmed = text.Trim();
        switch (trimmed.ToLowerInvariant())
        {
            case "integer": return IrPrimitiveType.Integer;
            case "decimal": return IrPrimitiveType.Decimal;
            case "string": return IrPrimitiveType.String;
            case "boolean": return IrPrimitiveType.Boolean;
        }

        const string listOf = "list of ";
        if (trimmed.StartsWith(listOf, StringComparison.OrdinalIgnoreCase))
        {
            var inner = ParseType(trimmed[listOf.Length..], records, errors);
            return inner is null ? null : new IrListType(inner);
        }

        if (trimmed.StartsWith("list<", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith('>'))
        {
            var inner = ParseType(trimmed[5..^1], records, errors);
            return inner is null ? null : new IrListType(inner);
        }

        if (records.TryGetValue(trimmed, out var record))
        {
            return record;
        }

        errors.Add($"Unknown capability type '{trimmed}'.");
        return null;
    }

    public static bool TryParseEffect(string text, out EffectKind effect)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "pure":
                effect = EffectKind.Pure;
                return true;
            case "external-read":
            case "network-read":
                effect = EffectKind.NetworkRead;
                return true;
            case "network-write":
                effect = EffectKind.NetworkWrite;
                return true;
            case "external-write":
                effect = EffectKind.ExternalState;
                return true;
            case "filesystem-read":
                effect = EffectKind.FileSystemRead;
                return true;
            case "filesystem-write":
                effect = EffectKind.FileSystemWrite;
                return true;
            default:
                effect = EffectKind.Pure;
                return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private sealed class CapabilityFile
    {
        public string? VersionHash { get; set; }
        public List<string>? GrantedEffects { get; set; }
        public List<string>? AllowedOperations { get; set; }
        public List<TypeFile>? Types { get; set; }
        public List<OperationFile>? Operations { get; set; }
        public ProviderFile? Provider { get; set; }
        public JsonElement? Mock { get; set; }
    }

    private sealed class ProviderFile
    {
        public string? Kind { get; set; }
        public string? Command { get; set; }
        public string? Module { get; set; }
        public string? BaseUrl { get; set; }
    }

    private sealed class TypeFile
    {
        public string? Name { get; set; }
        public List<FieldFile>? Fields { get; set; }
    }

    private sealed class FieldFile
    {
        public string? Name { get; set; }
        public string? Type { get; set; }
    }

    private sealed class OperationFile
    {
        public string? Name { get; set; }
        public List<ArgumentFile>? Arguments { get; set; }
        public string? ResultType { get; set; }
        public List<string>? Effects { get; set; }
    }

    private sealed class ArgumentFile
    {
        public string? Name { get; set; }
        public string? Type { get; set; }
        public bool? Required { get; set; }
    }
}
