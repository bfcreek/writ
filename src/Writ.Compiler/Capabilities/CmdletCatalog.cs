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
    IReadOnlyDictionary<string, bool> Fixed);

/// <summary>
/// The pinned list of PowerShell commands a program may bind. The compiler reads this file;
/// it never runs Get-Command or any cmdlet.
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

    public static CmdletCatalog Parse(string json)
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

            var fixedArguments = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (element.TryGetProperty("fixed", out var fixedObject))
            {
                foreach (var property in fixedObject.EnumerateObject())
                {
                    fixedArguments[property.Name] = property.Value.GetBoolean();
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

    public IEnumerable<CatalogEntry> AsCatalogEntries() =>
        Cmdlets.Select(c => new CatalogEntry(
            "pwsh." + c.Name,
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
        using var stream = typeof(CmdletCatalog).Assembly.GetManifestResourceStream("powershell.catalog.json")
            ?? throw new InvalidOperationException("powershell.catalog.json is not embedded.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}
