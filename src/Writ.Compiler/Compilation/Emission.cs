using System.Text.Json;
using System.Text.Json.Serialization;
using Writ.Compiler.Diagnostics;
using Writ.Compiler.Ir;

namespace Writ.Compiler.Compilation;

/// <summary>
/// How <see cref="CompilationPipeline"/> chooses an emission.
/// <see cref="ReadUserFile"/> reads <c>~/.writ/constraints.json</c>.
/// <see cref="ForcedId"/> is an explicit <c>--target</c> and skips scoring.
/// </summary>
public sealed record EmissionOptions
{
    /// <summary>Project <c>writ.constraints.json</c> only. Tests and library callers stay independent of the home directory.</summary>
    public static EmissionOptions ProjectOnly { get; } = new();

    public string? ForcedId { get; init; }
    public bool ReadUserFile { get; init; }
    public string? UserFilePath { get; init; }
}

/// <summary>One way to emit a program: a language, an approach, and an architecture.</summary>
public sealed class EmissionPlan
{
    public required string Id { get; init; }
    public required string Language { get; init; }
    public required string Approach { get; init; }
    public required string Architecture { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlySet<string> Traits { get; init; }
    public int Rank { get; init; }
    public required Func<IrModule, string?> Blocker { get; init; }

    public bool Offers(string trait) => Traits.Contains(trait);
}

/// <summary>Why a plan was not chosen.</summary>
public sealed record EmissionRejection(string Id, string Reason);

/// <summary>The emission compilation settled on, and the other plans it set aside.</summary>
public sealed record EmissionDecision(
    string Id,
    string Language,
    string Approach,
    string Architecture,
    string Summary,
    int Score,
    string Reason,
    IReadOnlyList<EmissionRejection> Considered);

public static class EmissionCatalog
{
    public static readonly EmissionPlan PowerShellScript = new()
    {
        Id = "powershell-script",
        Language = "powershell",
        Approach = "script",
        Architecture = "external-process",
        Summary = "PowerShell script run in its own process",
        Rank = 0,
        Traits = Traits("powershell", "script", "external-process", "dry-run", "cmdlets"),
        Blocker = static _ => null
    };

    public static IReadOnlyList<EmissionPlan> All { get; } = [PowerShellScript];

    public static IReadOnlySet<string> TraitsVocabulary { get; } = All
        .SelectMany(plan => plan.Traits)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static EmissionPlan? Find(string id) =>
        All.FirstOrDefault(plan => plan.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>auto</c> (or an empty target) lets constraints choose.
    /// <c>powershell</c> forces the script emission.
    /// </summary>
    public static bool TryParseTarget(string? text, out string? id, out string? error)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            id = null;
            error = null;
            return true;
        }

        var key = text.Trim();
        if (key.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("ps", StringComparison.OrdinalIgnoreCase) ||
            key.Equals(PowerShellScript.Id, StringComparison.OrdinalIgnoreCase))
        {
            id = PowerShellScript.Id;
            error = null;
            return true;
        }

        id = null;
        error = $"Unknown target '{text}'. Use auto or powershell.";
        return false;
    }

    private static HashSet<string> Traits(params string[] traits) =>
        new(traits, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Reads <c>writ.constraints.json</c> (nearest file at or above the source) and, when asked,
/// <c>~/.writ/constraints.json</c>. Hard constraints from both files apply. A project preference
/// counts twice a user preference. When both files set language, approach, or architecture, the project file wins.
/// </summary>
public static class EmissionConstraints
{
    public const string ProjectFileName = "writ.constraints.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly SourceSpan Span = new(1, 1, 1, 1);

    public static string DefaultUserPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".writ", "constraints.json");

    internal static IReadOnlyList<ConstraintLayer> Load(string sourceFile, EmissionOptions options, DiagnosticBag diagnostics)
    {
        var layers = new List<ConstraintLayer>();
        if (options.ReadUserFile)
        {
            var userPath = string.IsNullOrWhiteSpace(options.UserFilePath) ? DefaultUserPath() : options.UserFilePath;
            if (File.Exists(userPath) && TryRead(userPath, "user", diagnostics) is { } user)
            {
                layers.Add(user);
            }
        }

        var projectPath = FindProjectFile(sourceFile);
        if (projectPath is not null && TryRead(projectPath, "project", diagnostics) is { } project)
        {
            layers.Add(project);
        }

        if (diagnostics.HasErrors)
        {
            return layers;
        }

        var require = layers.SelectMany(layer => layer.Require).ToHashSet(StringComparer.Ordinal);
        var forbid = layers.SelectMany(layer => layer.Forbid).ToHashSet(StringComparer.Ordinal);
        var overlap = require.Intersect(forbid, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (overlap.Count > 0)
        {
            var where = projectPath ?? layers.FirstOrDefault()?.Path ?? sourceFile;
            diagnostics.Error("E5002",
                $"Constraints both require and forbid {string.Join(", ", overlap)}.",
                where, Span);
        }

        return layers;
    }

    /// <summary>Walks up from the source file, the same way <c>writ.mcp.json</c> is found.</summary>
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

    private static ConstraintLayer? TryRead(string path, string role, DiagnosticBag diagnostics)
    {
        ConstraintsFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ConstraintsFile>(File.ReadAllText(path), Options);
        }
        catch (JsonException ex)
        {
            diagnostics.Error("E5002", $"{Path.GetFileName(path)} is not valid: {ex.Message}", path, Span);
            return null;
        }

        if (file is null)
        {
            diagnostics.Error("E5002", $"{Path.GetFileName(path)} is empty.", path, Span);
            return null;
        }

        var languages = EmissionCatalog.All.Select(plan => plan.Language).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var approaches = EmissionCatalog.All.Select(plan => plan.Approach).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var architectures = EmissionCatalog.All.Select(plan => plan.Architecture).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new ConstraintLayer(
            role,
            Path.GetFullPath(path),
            Normalize(file.Require, "require", path, EmissionCatalog.TraitsVocabulary, diagnostics),
            Normalize(file.Prefer, "prefer", path, EmissionCatalog.TraitsVocabulary, diagnostics),
            Normalize(file.Avoid, "avoid", path, EmissionCatalog.TraitsVocabulary, diagnostics),
            Normalize(file.Forbid, "forbid", path, EmissionCatalog.TraitsVocabulary, diagnostics),
            NormalizeScalar(file.Language, "language", path, languages, diagnostics),
            NormalizeScalar(file.Approach, "approach", path, approaches, diagnostics),
            NormalizeScalar(file.Architecture, "architecture", path, architectures, diagnostics));
    }

    private static IReadOnlyList<string> Normalize(
        List<string>? items,
        string field,
        string path,
        IReadOnlySet<string> allowed,
        DiagnosticBag diagnostics)
    {
        var result = new List<string>();
        foreach (var raw in items ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                diagnostics.Error("E5002", $"{field} in {Path.GetFileName(path)} has an empty entry.", path, Span);
                continue;
            }

            var trait = raw.Trim().ToLowerInvariant();
            if (!allowed.Contains(trait))
            {
                diagnostics.Error("E5002",
                    $"'{raw}' is not a known {field} value. Known values: {string.Join(", ", allowed.Order(StringComparer.Ordinal))}.",
                    path, Span);
                continue;
            }

            if (!result.Contains(trait, StringComparer.Ordinal))
            {
                result.Add(trait);
            }
        }

        return result;
    }

    private static string? NormalizeScalar(
        string? raw,
        string field,
        string path,
        IReadOnlySet<string> allowed,
        DiagnosticBag diagnostics)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim().ToLowerInvariant();
        if (!allowed.Contains(value))
        {
            diagnostics.Error("E5002",
                $"'{raw}' is not a known {field}. Known values: {string.Join(", ", allowed.Order(StringComparer.Ordinal))}.",
                path, Span);
            return null;
        }

        return value;
    }

    private sealed class ConstraintsFile
    {
        public List<string>? Require { get; set; }
        public List<string>? Prefer { get; set; }
        public List<string>? Avoid { get; set; }
        public List<string>? Forbid { get; set; }
        public string? Language { get; set; }
        public string? Approach { get; set; }
        public string? Architecture { get; set; }
    }
}

internal sealed record ConstraintLayer(
    string Role,
    string Path,
    IReadOnlyList<string> Require,
    IReadOnlyList<string> Prefer,
    IReadOnlyList<string> Avoid,
    IReadOnlyList<string> Forbid,
    string? Language,
    string? Approach,
    string? Architecture);

public static class EmissionSelector
{
    private static readonly SourceSpan Span = new(1, 1, 1, 1);

    public static EmissionDecision? Select(IrModule module, string sourceFile, EmissionOptions options, DiagnosticBag diagnostics)
    {
        var layers = EmissionConstraints.Load(sourceFile, options, diagnostics);
        if (diagnostics.HasErrors)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(options.ForcedId))
        {
            var forced = EmissionCatalog.Find(options.ForcedId);
            if (forced is null)
            {
                diagnostics.Error("E5003",
                    $"Unknown emission '{options.ForcedId}'. Known emissions: {string.Join(", ", EmissionCatalog.All.Select(plan => plan.Id))}.",
                    sourceFile, Span);
                return null;
            }

            var blocker = forced.Blocker(module);
            if (blocker is not null)
            {
                diagnostics.Error("E5001", $"{forced.Id} cannot compile this program: {blocker}.", sourceFile, Span);
                return null;
            }

            return Decide(forced, 0, "forced with --target", []);
        }

        var require = layers.SelectMany(layer => layer.Require).Distinct(StringComparer.Ordinal).ToList();
        var forbid = layers.SelectMany(layer => layer.Forbid).Distinct(StringComparer.Ordinal).ToList();
        var language = Effective(layers, layer => layer.Language);
        var approach = Effective(layers, layer => layer.Approach);
        var architecture = Effective(layers, layer => layer.Architecture);

        var considered = new List<EmissionRejection>();
        var eligible = new List<(EmissionPlan Plan, int Score)>();
        foreach (var plan in EmissionCatalog.All)
        {
            var why = Reject(plan, module, language, approach, architecture, require, forbid);
            if (why is not null)
            {
                considered.Add(new EmissionRejection(plan.Id, why));
                continue;
            }

            eligible.Add((plan, Score(plan, layers)));
        }

        if (eligible.Count == 0)
        {
            var detail = string.Join("; ", considered.Select(item => $"{item.Id}: {item.Reason}"));
            diagnostics.Error("E5001", $"No emission satisfies the constraints. {detail}", sourceFile, Span);
            return null;
        }

        var bestScore = eligible.Max(item => item.Score);
        var tied = eligible.Count(item => item.Score == bestScore) > 1;
        var winner = eligible.Where(item => item.Score == bestScore).OrderBy(item => item.Plan.Rank).First();
        if (layers.Count > 0)
        {
            foreach (var other in eligible.Where(item => item.Plan.Id != winner.Plan.Id).OrderBy(item => item.Plan.Rank))
            {
                considered.Add(new EmissionRejection(other.Plan.Id, $"scored {other.Score}; {winner.Plan.Id} scored {bestScore}"));
            }
        }

        var blocked = considered.Any(item => item.Reason.StartsWith("calls ", StringComparison.Ordinal));
        return Decide(winner.Plan, winner.Score, Describe(winner.Plan, winner.Score, eligible.Count == 1, tied, blocked, layers), considered);
    }

    private static EmissionDecision Decide(EmissionPlan plan, int score, string reason, IReadOnlyList<EmissionRejection> considered) =>
        new(plan.Id, plan.Language, plan.Approach, plan.Architecture, plan.Summary, score, reason, considered);

    private static string? Reject(
        EmissionPlan plan,
        IrModule module,
        string? language,
        string? approach,
        string? architecture,
        IReadOnlyList<string> require,
        IReadOnlyList<string> forbid)
    {
        var blocker = plan.Blocker(module);
        if (blocker is not null)
        {
            return blocker;
        }

        if (language is not null && !plan.Language.Equals(language, StringComparison.OrdinalIgnoreCase))
        {
            return $"language is {plan.Language}; the constraints ask for {language}";
        }

        if (approach is not null && !plan.Approach.Equals(approach, StringComparison.OrdinalIgnoreCase))
        {
            return $"approach is {plan.Approach}; the constraints ask for {approach}";
        }

        if (architecture is not null && !plan.Architecture.Equals(architecture, StringComparison.OrdinalIgnoreCase))
        {
            return $"architecture is {plan.Architecture}; the constraints ask for {architecture}";
        }

        var missing = require.Where(trait => !plan.Offers(trait)).ToList();
        if (missing.Count > 0)
        {
            return "missing " + string.Join(", ", missing);
        }

        var forbidden = forbid.Where(plan.Offers).ToList();
        if (forbidden.Count > 0)
        {
            return "forbidden " + string.Join(", ", forbidden);
        }

        return null;
    }

    private static int Score(EmissionPlan plan, IReadOnlyList<ConstraintLayer> layers)
    {
        var score = 0;
        foreach (var layer in layers)
        {
            var weight = layer.Role == "project" ? 2 : 1;
            foreach (var trait in layer.Prefer)
            {
                if (plan.Offers(trait))
                {
                    score += weight;
                }
            }

            foreach (var trait in layer.Avoid)
            {
                if (plan.Offers(trait))
                {
                    score -= weight;
                }
            }
        }

        return score;
    }

    private static string? Effective(IReadOnlyList<ConstraintLayer> layers, Func<ConstraintLayer, string?> pick)
    {
        string? value = null;
        foreach (var layer in layers)
        {
            var next = pick(layer);
            if (!string.IsNullOrWhiteSpace(next))
            {
                value = next;
            }
        }

        return value;
    }

    private static string Describe(EmissionPlan winner, int score, bool sole, bool tied, bool blocked, IReadOnlyList<ConstraintLayer> layers)
    {
        if (layers.Count == 0)
        {
            return "default; no user or project constraints file";
        }

        var from = string.Join("; ", layers.Select(DescribeLayer).Concat(Overrides(layers)));
        if (sole)
        {
            var phrase = blocked
                ? "only emission that can compile this program"
                : "only emission that satisfies the constraints";
            return $"{phrase} ({from})";
        }

        if (tied)
        {
            return $"scored {score}, tied with another emission; {winner.Id} stays the default ({from})";
        }

        return $"scored {score} ({from})";
    }

    private static string DescribeLayer(ConstraintLayer layer)
    {
        var bits = new List<string>();
        void Add(string label, IReadOnlyList<string> items)
        {
            if (items.Count > 0)
            {
                bits.Add($"{label} {string.Join(", ", items)}");
            }
        }

        Add("requires", layer.Require);
        Add("forbids", layer.Forbid);
        Add("prefers", layer.Prefer);
        Add("avoids", layer.Avoid);
        if (layer.Language is not null)
        {
            bits.Add($"language {layer.Language}");
        }

        if (layer.Approach is not null)
        {
            bits.Add($"approach {layer.Approach}");
        }

        if (layer.Architecture is not null)
        {
            bits.Add($"architecture {layer.Architecture}");
        }

        var body = bits.Count == 0 ? "no preferences" : string.Join(", ", bits);
        return $"{layer.Role} {Path.GetFileName(layer.Path)}: {body}";
    }

    private static IEnumerable<string> Overrides(IReadOnlyList<ConstraintLayer> layers)
    {
        var user = layers.FirstOrDefault(layer => layer.Role == "user");
        var project = layers.FirstOrDefault(layer => layer.Role == "project");
        if (user is null || project is null)
        {
            yield break;
        }

        if (Disagree(user.Language, project.Language))
        {
            yield return $"project language {project.Language} overrides the user language {user.Language}";
        }

        if (Disagree(user.Approach, project.Approach))
        {
            yield return $"project approach {project.Approach} overrides the user approach {user.Approach}";
        }

        if (Disagree(user.Architecture, project.Architecture))
        {
            yield return $"project architecture {project.Architecture} overrides the user architecture {user.Architecture}";
        }
    }

    private static bool Disagree(string? user, string? project) =>
        !string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(project) && !string.Equals(user, project, StringComparison.Ordinal);
}
