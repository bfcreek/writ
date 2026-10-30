using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Writ.Compiler.Ir;
using Writ.Compiler.Semantics;

namespace Writ.Compiler.Capabilities;

public sealed record CmdletParameter(string Name, IrType Type, bool Required, IReadOnlyList<string> Aliases, bool IsSwitch);

public sealed record CmdletEntry(
    string Name,
    IReadOnlyList<string> Phrases,
    EffectKind Effect,
    IrType Output,
    IReadOnlyList<CmdletParameter> Parameters,
    IReadOnlyDictionary<string, object> Fixed);

/// <summary>
/// The pinned list of tools a program may bind: PowerShell-shaped catalog commands plus
/// Writ-native dotted tools. The compiler reads these files; it never runs Get-Command.
/// </summary>
public sealed class CmdletCatalog
{
    private static readonly Lazy<CmdletCatalog> Embedded = new(LoadEmbedded);

    public string Version { get; }
    public string Hash { get; }
    public IReadOnlyList<CmdletEntry> Cmdlets { get; }

    private CmdletCatalog(string version, string hash, IReadOnlyList<CmdletEntry> cmdlets)
    {
        Version = version;
        Hash = hash;
        Cmdlets = cmdlets;
    }

    public static CmdletCatalog Default => Embedded.Value;

    public static CmdletCatalog Parse(string json) => Merge([ParseOne(json)]);

    public static CmdletCatalog Merge(IReadOnlyList<CmdletCatalog> parts)
    {
        var cmdlets = new List<CmdletEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var versions = new List<string>();
        var hashMaterial = new StringBuilder();
        foreach (var part in parts)
        {
            versions.Add(part.Version);
            hashMaterial.Append(part.Hash).Append('\n');
            foreach (var entry in part.Cmdlets)
            {
                if (!seen.Add(entry.Name))
                {
                    throw new InvalidOperationException($"Duplicate catalog tool '{entry.Name}'.");
                }

                cmdlets.Add(entry);
            }
        }

        var hash = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashMaterial.ToString())))
            .ToLowerInvariant();
        return new CmdletCatalog(string.Join("+", versions), hash, cmdlets);
    }

    public static CmdletCatalog ParseOne(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = document.RootElement;
        var version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
        var cmdlets = new List<CmdletEntry>();
        foreach (var element in root.GetProperty("cmdlets").EnumerateArray())
        {
            var parameters = new List<CmdletParameter>();
            if (element.TryGetProperty("parameters", out var parameterArray))
            {
                foreach (var parameter in parameterArray.EnumerateArray())
                {
                    var typeText = parameter.TryGetProperty("type", out var t) ? t.GetString() ?? "string" : "string";
                    parameters.Add(new CmdletParameter(
                        parameter.GetProperty("name").GetString()!,
                        typeText == "switch" ? IrPrimitiveType.Boolean : ParseType(typeText),
                        parameter.TryGetProperty("required", out var r) && r.GetBoolean(),
                        Strings(parameter, "aliases"),
                        typeText == "switch"));
                }
            }

            var fixedArguments = new Dictionary<string, object>(StringComparer.Ordinal);
            if (element.TryGetProperty("fixed", out var fixedObject))
            {
                foreach (var property in fixedObject.EnumerateObject())
                {
                    fixedArguments[property.Name] = property.Value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => property.Value.GetString() ?? ""
                    };
                }
            }

            FileCapabilityLoader.TryParseEffect(element.TryGetProperty("effect", out var e) ? e.GetString() ?? "pure" : "pure", out var effect);
            cmdlets.Add(new CmdletEntry(
                element.GetProperty("name").GetString()!,
                Strings(element, "phrases"),
                effect,
                ParseType(element.TryGetProperty("output", out var o) ? o.GetString() ?? "dynamic" : "dynamic"),
                parameters,
                fixedArguments));
        }

        var hash = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        return new CmdletCatalog(version, hash, cmdlets);
    }

    public CmdletEntry? Find(string name) =>
        Cmdlets.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public static string CatalogPrefix(string name) =>
        name.Contains('.', StringComparison.Ordinal) ? "writ." : "pwsh.";

    public static bool IsCatalogToolId(string id) =>
        id.StartsWith("pwsh.", StringComparison.Ordinal) || id.StartsWith("writ.", StringComparison.Ordinal);

    public static string? ToolNameFromId(string id)
    {
        if (id.StartsWith("pwsh.", StringComparison.Ordinal))
        {
            return id["pwsh.".Length..];
        }

        if (id.StartsWith("writ.", StringComparison.Ordinal))
        {
            return id["writ.".Length..];
        }

        return null;
    }

    public IEnumerable<CatalogEntry> AsCatalogEntries() =>
        Cmdlets.Select(c => new CatalogEntry(
            CatalogPrefix(c.Name) + c.Name,
            string.Join(" | ", c.Phrases),
            c.Parameters.Select(p => p.Name).ToList(),
            c.Parameters.ToDictionary(p => p.Name, p => p.Aliases, StringComparer.Ordinal),
            c.Parameters.Where(p => p.IsSwitch).Select(p => p.Name).ToList()));

    public static IrType ParseType(string text)
    {
        var trimmed = text.Trim();
        switch (trimmed.ToLowerInvariant())
        {
            case "integer": return IrPrimitiveType.Integer;
            case "decimal": return IrPrimitiveType.Decimal;
            case "string": return IrPrimitiveType.String;
            case "boolean": return IrPrimitiveType.Boolean;
            case "dynamic": return IrDynamicType.Instance;
            case "void": return IrVoidType.Instance;
        }

        const string listOf = "list of ";
        if (trimmed.StartsWith(listOf, StringComparison.OrdinalIgnoreCase))
        {
            return new IrListType(ParseType(trimmed[listOf.Length..]));
        }

        return IrDynamicType.Instance;
    }

    private static List<string> Strings(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
    }

    private static CmdletCatalog LoadEmbedded()
    {
        var powershell = ReadResource("powershell.catalog.json");
        var writ = ReadResource("writ.catalog.json");
        return Merge([ParseOne(powershell), ParseOne(writ)]);
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(CmdletCatalog).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
