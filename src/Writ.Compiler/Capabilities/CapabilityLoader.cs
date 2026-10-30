using System.Text;
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

        if (source.Kind == "sqlite")
        {
            return LoadFromSqliteSource(alias, source, sourceFile, diagnostics, span, workingDirectory);
        }

        if (source.Kind == "http")
        {
            return LoadFromHttpSource(alias, source, sourceFile, diagnostics, span, workingDirectory);
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
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    errors.Add("A 'powershell-module' provider needs a 'module'.");
                }

                break;
            case "http":
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    errors.Add("An 'http' provider needs a 'baseUrl' or an OpenAPI document path in 'command'.");
                    break;
                }

                try
                {
                    var openApiPath = ResolveMaybeRelative(endpoint, manifestDirectory);
                    if (File.Exists(openApiPath))
                    {
                        var discovered = DiscoverOpenApi(openApiPath, records, errors);
                        foreach (var pair in discovered.Operations)
                        {
                            operations[pair.Key] = pair.Value;
                        }

                        discoveryHash = discovered.Hash;
                        endpoint = discovered.BaseUrl;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or JsonException or IOException)
                {
                    errors.Add(ex.Message);
                }

                break;
            case "sqlite":
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    errors.Add("A 'sqlite' provider needs a database path in 'command' or 'module'.");
                    break;
                }

                try
                {
                    var dbPath = ResolveMaybeRelative(endpoint, manifestDirectory);
                    var schema = DiscoverSqlite(dbPath, errors);
                    foreach (var pair in schema.Operations)
                    {
                        operations[pair.Key] = pair.Value;
                    }

                    typeDecls.AddRange(schema.Types);
                    discoveryHash = schema.Hash;
                }
                catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException)
                {
                    errors.Add(ex.Message);
                }

                break;
            default:
                errors.Add($"Unknown provider kind '{provider}'. Use mock, mcp, powershell-module, http, or sqlite.");
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

    private static LoadedCapability? LoadFromHttpSource(
        string alias,
        CapabilitySource source,
        string? sourceFile,
        DiagnosticBag diagnostics,
        SourceSpan span,
        string? workingDirectory)
    {
        workingDirectory ??= string.IsNullOrEmpty(sourceFile)
            ? Directory.GetCurrentDirectory()
            : Path.GetDirectoryName(Path.GetFullPath(sourceFile));
        var location = source.Location;
        if (location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            location.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new LoadedCapability
            {
                Alias = alias,
                VersionHash = Sha256(location),
                ManifestPath = "",
                GrantedEffects = [EffectKind.NetworkRead, EffectKind.NetworkWrite],
                AllowedOperations = ["GET /", "POST /"],
                Operations = new Dictionary<string, BoundOperation>(StringComparer.Ordinal)
                {
                    ["GET /"] = new("GET /", [], IrDynamicType.Instance, [EffectKind.NetworkRead]),
                    ["POST /"] = new("POST /", [new BoundArgument("body", IrDynamicType.Instance, false)], IrDynamicType.Instance, [EffectKind.NetworkWrite])
                },
                Types = [],
                Provider = "http",
                Endpoint = location.TrimEnd('/'),
                Transport = "http",
                WorkingDirectory = workingDirectory,
                DiscoveryHash = Sha256(location),
                Source = source
            };
        }

        var errors = new List<string>();
        var openApiPath = ResolveMaybeRelative(location, workingDirectory);
        if (!File.Exists(openApiPath))
        {
            diagnostics.Error("E4001", $"OpenAPI document not found: {location}", sourceFile, span);
            return null;
        }

        var discovered = DiscoverOpenApi(openApiPath, new Dictionary<string, IrRecordType>(), errors);
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
            VersionHash = discovered.Hash,
            ManifestPath = openApiPath,
            GrantedEffects = [EffectKind.NetworkRead, EffectKind.NetworkWrite],
            AllowedOperations = discovered.Operations.Keys.ToList(),
            Operations = discovered.Operations,
            Types = [],
            Provider = "http",
            Endpoint = discovered.BaseUrl,
            Transport = "http",
            WorkingDirectory = workingDirectory,
            DiscoveryHash = discovered.Hash,
            Source = source
        };
    }

    private static LoadedCapability? LoadFromSqliteSource(
        string alias,
        CapabilitySource source,
        string? sourceFile,
        DiagnosticBag diagnostics,
        SourceSpan span,
        string? workingDirectory)
    {
        workingDirectory ??= string.IsNullOrEmpty(sourceFile)
            ? Directory.GetCurrentDirectory()
            : Path.GetDirectoryName(Path.GetFullPath(sourceFile));
        var errors = new List<string>();
        var dbPath = ResolveMaybeRelative(source.Location, workingDirectory);
        var schema = DiscoverSqlite(dbPath, errors);
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
            VersionHash = schema.Hash,
            ManifestPath = "",
            GrantedEffects = [EffectKind.FileSystemRead, EffectKind.FileSystemWrite],
            AllowedOperations = schema.Operations.Keys.ToList(),
            Operations = schema.Operations,
            Types = schema.Types,
            Provider = "sqlite",
            Endpoint = dbPath,
            Transport = "sqlite",
            WorkingDirectory = workingDirectory,
            DiscoveryHash = schema.Hash,
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

    private static string ResolveMaybeRelative(string path, string? directory)
    {
        if (Path.IsPathRooted(path) || path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return Path.GetFullPath(Path.Combine(directory ?? Directory.GetCurrentDirectory(), path));
    }

    private static (Dictionary<string, BoundOperation> Operations, string Hash, string BaseUrl) DiscoverOpenApi(
        string path,
        IReadOnlyDictionary<string, IrRecordType> records,
        List<string> errors)
    {
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var baseUrl = "http://127.0.0.1";
        if (root.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array &&
            servers.GetArrayLength() > 0 && servers[0].TryGetProperty("url", out var url))
        {
            baseUrl = url.GetString()?.TrimEnd('/') ?? baseUrl;
        }

        var operations = new Dictionary<string, BoundOperation>(StringComparer.Ordinal);
        if (root.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object)
        {
            foreach (var pathItem in paths.EnumerateObject())
            {
                foreach (var method in pathItem.Value.EnumerateObject())
                {
                    if (method.Name is not ("get" or "post" or "put" or "delete"))
                    {
                        continue;
                    }

                    var name = method.Value.TryGetProperty("operationId", out var id) && id.ValueKind == JsonValueKind.String
                        ? id.GetString()!
                        : $"{method.Name.ToUpperInvariant()} {pathItem.Name}";
                    var args = new List<BoundArgument>();
                    if (method.Value.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var parameter in parameters.EnumerateArray())
                        {
                            var argName = parameter.TryGetProperty("name", out var n) ? n.GetString() ?? "arg" : "arg";
                            var required = parameter.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True;
                            args.Add(new BoundArgument(argName, IrDynamicType.Instance, required));
                        }
                    }

                    var effect = method.Name.Equals("get", StringComparison.OrdinalIgnoreCase)
                        ? EffectKind.NetworkRead
                        : EffectKind.NetworkWrite;
                    operations[name] = new BoundOperation(name, args, IrDynamicType.Instance, [effect]);
                }
            }
        }

        if (operations.Count == 0)
        {
            errors.Add($"OpenAPI document '{path}' declared no get/post/put/delete operations.");
        }

        return (operations, Sha256(json), baseUrl);
    }

    private static (Dictionary<string, BoundOperation> Operations, List<IrTypeDecl> Types, string Hash) DiscoverSqlite(
        string dbPath,
        List<string> errors)
    {
        if (!File.Exists(dbPath))
        {
            errors.Add($"SQLite database not found: {dbPath}");
            return ([], [], Sha256(dbPath));
        }

        var operations = new Dictionary<string, BoundOperation>(StringComparer.Ordinal);
        var types = new List<IrTypeDecl>();
        var schema = new StringBuilder();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using (var tables = connection.CreateCommand())
        {
            tables.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var reader = tables.ExecuteReader();
            while (reader.Read())
            {
                var table = reader.GetString(0);
                schema.Append(table).Append(':');
                var fields = new List<IrField>();
                using var info = connection.CreateCommand();
                info.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\")";
                using var columns = info.ExecuteReader();
                while (columns.Read())
                {
                    var column = columns.GetString(1);
                    var declared = columns.GetString(2);
                    var type = MapSqliteType(declared);
                    fields.Add(new IrField(column, type));
                    schema.Append(column).Append('=').Append(declared).Append(';');
                }

                var record = new IrRecordType(table, fields);
                types.Add(new IrTypeDecl(table, fields, default, record));
                operations[table] = new BoundOperation(table, [], new IrListType(record), [EffectKind.FileSystemRead]);
            }
        }

        operations["query"] = new BoundOperation(
            "query",
            [new BoundArgument("sql", IrPrimitiveType.String, true)],
            new IrListType(IrDynamicType.Instance),
            [EffectKind.FileSystemRead]);
        operations["execute"] = new BoundOperation(
            "execute",
            [new BoundArgument("sql", IrPrimitiveType.String, true)],
            IrPrimitiveType.Integer,
            [EffectKind.FileSystemWrite]);

        return (operations, types, Sha256(schema.ToString()));
    }

    private static IrType MapSqliteType(string declared)
    {
        var text = declared.ToLowerInvariant();
        if (text.Contains("int"))
        {
            return IrPrimitiveType.Integer;
        }

        if (text.Contains("real") || text.Contains("floa") || text.Contains("doub") || text.Contains("dec"))
        {
            return IrPrimitiveType.Decimal;
        }

        if (text.Contains("bool"))
        {
            return IrPrimitiveType.Boolean;
        }

        return IrPrimitiveType.String;
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
            case "runtime-ai":
                effect = EffectKind.RuntimeAi;
                return true;
            case "secret-read":
                effect = EffectKind.SecretRead;
                return true;
            case "script":
                effect = EffectKind.Script;
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
