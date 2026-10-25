using System.Text.Json;
using System.Text.Json.Serialization;
using Writ.Compiler.Capabilities;
using Writ.Compiler.Diagnostics;
using Writ.Compiler.Ir;

namespace Writ.Compiler.Compilation;

public sealed class LockedCapability
{
    public required string Source { get; init; }
    public required string Provider { get; init; }
    public required string Hash { get; init; }
    public IReadOnlyList<string> Operations { get; init; } = [];
}

public sealed class LockedReading
{
    public required string Where { get; init; }
    public required string Text { get; init; }
    public required IReadOnlyList<string> Selections { get; init; }
}

/// <summary>
/// What <c>&lt;file&gt;.writ.lock.json</c> pins: the source, the compiler, every discovered
/// capability, the cmdlet catalog, every template reading, and the emission those
/// constraints chose. Builds are reproducible when all of these agree.
/// </summary>
public sealed class WritLock
{
    public int Version { get; init; } = 1;
    public string SourceHash { get; init; } = "";
    public string CompilerVersion { get; init; } = "";
    public string IrSchema { get; init; } = "";
    public string CmdletCatalogHash { get; init; } = "";
    public Dictionary<string, LockedCapability> Capabilities { get; init; } = new(StringComparer.Ordinal);
    public List<LockedReading> Readings { get; init; } = [];

    /// <summary>Emission id chosen for this program. Absent on locks written before emissions were recorded.</summary>
    public string? Emission { get; init; }
}

public static class LockFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string PathFor(string intentPath) => intentPath + ".lock.json";

    public static WritLock? Read(string lockPath)
    {
        if (!File.Exists(lockPath))
        {
            return null;
        }

        return JsonSerializer.Deserialize<WritLock>(File.ReadAllText(lockPath), Options);
    }

    public static void Write(string lockPath, WritLock @lock) =>
        File.WriteAllText(lockPath, JsonSerializer.Serialize(@lock, Options) + Environment.NewLine);

    public static WritLock Build(string source, CompilationResult result, CmdletCatalog cmdlets)
    {
        var capabilities = new Dictionary<string, LockedCapability>(StringComparer.Ordinal);
        foreach (var capability in result.Capabilities)
        {
            capabilities[capability.Alias] = new LockedCapability
            {
                Source = capability.Source.ToString(),
                Provider = capability.Provider,
                Hash = capability.DiscoveryHash,
                Operations = capability.Operations.Keys.Order(StringComparer.Ordinal).ToList()
            };
        }

        return new WritLock
        {
            SourceHash = FileCapabilityLoader.Sha256(source),
            CompilerVersion = CompilerInfo.Version,
            IrSchema = IrVersions.SchemaVersion,
            CmdletCatalogHash = cmdlets.Hash,
            Capabilities = capabilities,
            Readings = result.Readings.ToList(),
            Emission = result.Emission?.Id
        };
    }

    /// <summary>
    /// Compares a fresh lock against the one on disk. Capability drift is always an error:
    /// a tool that changed under the program needs a deliberate <c>writ lock</c>.
    /// With <paramref name="frozen"/>, any difference at all is an error.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Verify(WritLock? existing, WritLock current, string filePath, bool frozen)
    {
        var diagnostics = new List<Diagnostic>();
        var span = new SourceSpan(1, 1, 1, 1);
        if (existing is null)
        {
            if (frozen)
            {
                diagnostics.Add(new Diagnostic("E4009", DiagnosticSeverity.Error,
                    $"No lockfile exists for {Path.GetFileName(filePath)}. Run 'writ lock' before building with --frozen.", filePath, span));
            }

            return diagnostics;
        }

        foreach (var (alias, locked) in existing.Capabilities)
        {
            if (!current.Capabilities.TryGetValue(alias, out var now))
            {
                if (frozen)
                {
                    diagnostics.Add(new Diagnostic("E4009", DiagnosticSeverity.Error,
                        $"Capability '{alias}' is in the lockfile but no longer used.", filePath, span));
                }

                continue;
            }

            if (!string.Equals(locked.Hash, now.Hash, StringComparison.Ordinal))
            {
                diagnostics.Add(new Diagnostic("E4008", DiagnosticSeverity.Error,
                    $"Capability '{alias}' changed since it was locked ({Short(locked.Hash)} -> {Short(now.Hash)}). Review the tool, then run 'writ lock' to accept it.",
                    filePath, span));
            }
        }

        if (!frozen)
        {
            return diagnostics;
        }

        foreach (var alias in current.Capabilities.Keys.Where(a => !existing.Capabilities.ContainsKey(a)))
        {
            diagnostics.Add(new Diagnostic("E4009", DiagnosticSeverity.Error,
                $"Capability '{alias}' is not in the lockfile.", filePath, span));
        }

        if (existing.SourceHash != current.SourceHash)
        {
            diagnostics.Add(new Diagnostic("E4009", DiagnosticSeverity.Error, "The source changed since it was locked.", filePath, span));
        }

        if (existing.CompilerVersion != current.CompilerVersion || existing.IrSchema != current.IrSchema)
        {
            diagnostics.Add(new Diagnostic("E4009", DiagnosticSeverity.Error,
                $"The lockfile was made by compiler {existing.CompilerVersion} (IR {existing.IrSchema}); this is {current.CompilerVersion} (IR {current.IrSchema}).", filePath, span));
        }

        if (existing.CmdletCatalogHash != current.CmdletCatalogHash)
        {
            diagnostics.Add(new Diagnostic("E4009", DiagnosticSeverity.Error, "The PowerShell cmdlet catalog changed since it was locked.", filePath, span));
        }

        var lockedReadings = existing.Readings.Select(Describe).ToList();
        var currentReadings = current.Readings.Select(Describe).ToList();
        if (!string.IsNullOrEmpty(existing.Emission) &&
            !string.IsNullOrEmpty(current.Emission) &&
            !string.Equals(existing.Emission, current.Emission, StringComparison.Ordinal))
        {
            diagnostics.Add(new Diagnostic("E4009", DiagnosticSeverity.Error,
                $"The chosen emission changed from {existing.Emission} to {current.Emission}.", filePath, span));
        }

        if (!lockedReadings.SequenceEqual(currentReadings, StringComparer.Ordinal))
        {
            var changed = currentReadings.Except(lockedReadings, StringComparer.Ordinal)
                .Concat(lockedReadings.Except(currentReadings, StringComparer.Ordinal))
                .FirstOrDefault() ?? "(reading count differs)";
            diagnostics.Add(new Diagnostic("E4009", DiagnosticSeverity.Error,
                $"A reading changed since the lock: {changed}", filePath, span));
        }

        return diagnostics;
    }

    private static string Describe(LockedReading reading) =>
        $"{reading.Where} \"{reading.Text}\" -> {string.Join(", ", reading.Selections)}";

    private static string Short(string hash) => hash.Length > 19 ? hash[..19] + "..." : hash;
}

public static class CompilerInfo
{
    public static string Version { get; } =
        typeof(CompilerInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
