using System.Text;
using Writ.Compiler.Ir;

namespace Writ.Compiler.Compilation;

/// <summary>
/// `writ explain`: for each sentence that bound to something with an effect, a model reading,
/// or a tool, say what it became. The output is the audit trail a reviewer reads before running.
/// </summary>
public static class Explainer
{
    public static string Explain(CompilationResult result, string filePath)
    {
        var sb = new StringBuilder();
        var sourceLines = File.Exists(filePath) ? File.ReadAllLines(filePath) : [];
        string Line(int number) => number >= 1 && number <= sourceLines.Length ? sourceLines[number - 1].Trim() : "";

        sb.AppendLine($"{Path.GetFileName(filePath)}");
        if (result.Emission is { } emission)
        {
            sb.AppendLine($"  emission: {emission.Id} — {emission.Summary}");
            sb.AppendLine($"    {emission.Reason}");
            foreach (var other in emission.Considered)
            {
                sb.AppendLine($"    {other.Id}: {other.Reason}");
            }
        }

        if (result.Model is { } model)
        {
            var named = string.IsNullOrEmpty(model.Model) ? model.Provider : $"{model.Provider} {model.Model}";
            var profile = string.IsNullOrEmpty(model.Profile) ? "" : $", profile {model.Profile}";
            sb.AppendLine($"  model: {named}{profile} ({model.Source})");
        }

        if (result.GrantedEffects.Count > 0)
        {
            sb.AppendLine($"  permissions: {string.Join(", ", result.GrantedEffects.Select(DescribeEffect))}");
        }

        foreach (var capability in result.Capabilities)
        {
            sb.AppendLine($"  tool {capability.Alias}: {capability.Provider} {capability.Source} ({Short(capability.DiscoveryHash)})");
        }

        if (result.Readings.Count > 0)
        {
            sb.AppendLine("  model readings:");
            foreach (var reading in result.Readings)
            {
                sb.AppendLine($"    {reading.Where} \"{reading.Text}\" -> {string.Join("; ", reading.Selections)}");
            }
        }

        if (result.Ir is null)
        {
            return sb.ToString();
        }

        foreach (var function in result.Ir.Functions)
        {
            var header = function.IsEntry ? "When run" : $"To {function.Name}";
            var parameters = string.Join(", ", function.Parameters.Select(p => $"{p.Name}: {Describe(p.Type)}"));
            sb.AppendLine($"  {header}({parameters}) -> {Describe(function.ReturnType)}{(function.SourceFile is null ? "" : $"  [from {Path.GetFileName(function.SourceFile)}]")}");
            var seen = new HashSet<int>();
            foreach (var node in Walk(function.Body))
            {
                var what = DescribeNode(node);
                if (what is null)
                {
                    continue;
                }

                var line = node.Span.StartLine;
                if (seen.Add(line))
                {
                    sb.AppendLine($"    line {line}: {Line(line)}");
                }

                sb.AppendLine($"      -> {what}");
            }
        }

        return sb.ToString();
    }

    private static string? DescribeNode(IrNode node) => node switch
    {
        IrCommand command => $"PowerShell {command.Name} {string.Join(" ", command.Arguments.Select(a => $"-{a.Name}"))} [{string.Join(", ", command.Effects.Select(DescribeEffect))}] ({command.Binding})",
        IrCapabilityCall call => $"tool {call.Alias}.{call.Operation}({string.Join(", ", call.Arguments.Select(a => a.Name))}) via {call.Provider} [{string.Join(", ", call.Effects.Select(DescribeEffect))}]",
        IrScriptCall script => $"run {Path.GetFileName(script.Path)} ({script.Kind}) {string.Join(" ", script.Arguments.Select(a => $"-{a.Name}"))} [{string.Join(", ", script.Effects.Select(DescribeEffect))}]",
        IrRetry retry => $"retry up to {retry.Times} times",
        IrTry t => $"try{(t.OnFailure is not null ? " / if that fails" : "")}{(t.Finally is not null ? " / finally" : "")}",
        IrValidate validate => $"check the data is a {Describe(validate.Type)}",
        IrSumProjection sum when sum.Provenance == Provenance.ResolvedByModel => $"sum of {DescribeProjection(sum)} (model)",
        IrFilter filter when filter.Provenance == Provenance.ResolvedByModel => "filter (model)",
        IrCall { Callee: "json" or "csv" } call => $"parse as {call.Callee.ToUpperInvariant()}",
        IrCall { Callee: "env" } => "read an environment variable",
        _ => null
    };

    private static string DescribeProjection(IrSumProjection sum) => sum.Projection switch
    {
        IrBinary bin => $"{Short(bin.Left)} {bin.Operator} {Short(bin.Right)}",
        var other => Short(other)
    };

    private static string Short(IrNode node) => node switch
    {
        IrFieldAccess field => field.FieldName,
        IrVar v => v.Name,
        IrLiteral l => l.Value?.ToString() ?? "null",
        _ => node.GetType().Name
    };

    private static IEnumerable<IrNode> Walk(IEnumerable<IrNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Children(node))
            {
                foreach (var inner in Walk([child]))
                {
                    yield return inner;
                }
            }
        }
    }

    private static IEnumerable<IrNode> Children(IrNode node) => node switch
    {
        IrAssign a => [a.Value],
        IrReturn r => [r.Value],
        IrIf i => new[] { i.Condition }.Concat(i.ThenBody).Concat(i.OtherwiseBody ?? []),
        IrForEach f => new[] { f.Collection }.Concat(f.Body),
        IrWhile w => new[] { w.Condition }.Concat(w.Body),
        IrDo d => new[] { d.Condition }.Concat(d.Body),
        IrFor f => new[] { f.From, f.To }.Concat(f.Body),
        IrSwitch s => new[] { s.Value }.Concat(s.Arms.SelectMany(a => a.Values.Concat(a.Body))).Concat(s.Otherwise ?? []),
        IrTrap t => t.Body,
        IrData d => d.Body,
        IrSection s => s.Body,
        IrLabel l => [l.Statement],
        IrExit e => e.Code is null ? [] : [e.Code],
        IrRedirect r => r.Target is null ? [r.Command] : [r.Command, r.Target],
        IrDotSource d => [d.Path],
        IrTry t => t.Body.Concat(t.OnFailure ?? []).Concat(t.Finally ?? []),
        IrSay s => s.Parts,
        IrBinary b => [b.Left, b.Right],
        IrUnary u => [u.Operand],
        IrCall c => c.Arguments,
        IrFieldAccess f => [f.Target],
        IrSumProjection s => [s.Collection, s.Projection],
        IrFilter f => [f.Collection, f.Predicate],
        IrCapabilityCall c => c.Arguments.Select(a => a.Value),
        IrCommand c => c.Arguments.Select(a => a.Value),
        IrScriptCall s => s.Arguments.Select(a => a.Value),
        IrSequence s => s.Steps,
        IrSort s => [s.Collection],
        IrTake t => [t.Collection, t.Count],
        IrSelectField s => [s.Collection],
        IrValidate v => [v.Value],
        IrRetry r => [r.Operation],
        IrListLiteral l => l.Elements,
        _ => []
    };

    public static string Describe(IrType type) => type switch
    {
        IrPrimitiveType p => p.Name,
        IrListType l => $"list of {Describe(l.ElementType)}",
        IrRecordType r => r.Name,
        IrDynamicType => "untyped data",
        IrVoidType => "nothing",
        _ => "?"
    };

    public static string DescribeEffect(EffectKind effect) => effect switch
    {
        EffectKind.Pure => "pure",
        EffectKind.FileSystemRead => "reads files",
        EffectKind.FileSystemWrite => "writes files",
        EffectKind.NetworkRead => "reads from outside",
        EffectKind.NetworkWrite => "sends outside",
        EffectKind.ExternalState => "changes something outside",
        EffectKind.RuntimeAi => "asks a model at run time",
        EffectKind.Script => "runs a script",
        _ => effect.ToString()
    };

    private static string Short(string hash) => hash.Length > 19 ? hash[..19] + "..." : hash;

    /// <summary>
    /// `writ diff`: what changed in meaning between the lockfile and the program as it binds now.
    /// Text edits that bind the same way are reported as such.
    /// </summary>
    public static string Diff(WritLock? locked, WritLock current)
    {
        var sb = new StringBuilder();
        if (locked is null)
        {
            sb.AppendLine("No lockfile yet; everything below is new.");
            locked = new WritLock();
        }

        var changes = 0;
        void Change(string text)
        {
            changes++;
            sb.AppendLine(text);
        }

        if (locked.SourceHash != current.SourceHash)
        {
            Change($"source: text changed ({Short(locked.SourceHash)} -> {Short(current.SourceHash)})");
        }

        if (locked.CompilerVersion != current.CompilerVersion || locked.IrSchema != current.IrSchema)
        {
            Change($"compiler: {locked.CompilerVersion}/IR {locked.IrSchema} -> {current.CompilerVersion}/IR {current.IrSchema}");
        }

        if (locked.CmdletCatalogHash != current.CmdletCatalogHash)
        {
            Change("cmdlet catalog: changed");
        }

        if (!string.Equals(locked.Emission, current.Emission, StringComparison.Ordinal))
        {
            var lockedEmission = string.IsNullOrEmpty(locked.Emission) ? "(none)" : locked.Emission;
            var currentEmission = string.IsNullOrEmpty(current.Emission) ? "(none)" : current.Emission;
            Change($"emission: {lockedEmission} -> {currentEmission}");
        }

        foreach (var (alias, cap) in current.Capabilities)
        {
            if (!locked.Capabilities.TryGetValue(alias, out var was))
            {
                Change($"+ tool {alias}: {cap.Provider} {cap.Source} ({string.Join(", ", cap.Operations)})");
            }
            else
            {
                if (was.Hash != cap.Hash)
                {
                    Change($"~ tool {alias}: {Short(was.Hash)} -> {Short(cap.Hash)}");
                }

                foreach (var op in cap.Operations.Except(was.Operations))
                {
                    Change($"  + {alias}.{op}");
                }

                foreach (var op in was.Operations.Except(cap.Operations))
                {
                    Change($"  - {alias}.{op}");
                }
            }
        }

        foreach (var alias in locked.Capabilities.Keys.Except(current.Capabilities.Keys))
        {
            Change($"- tool {alias}");
        }

        var before = locked.Readings.Select(r => $"\"{r.Text}\" -> {string.Join("; ", r.Selections)}").ToList();
        var after = current.Readings.Select(r => $"\"{r.Text}\" -> {string.Join("; ", r.Selections)}").ToList();
        foreach (var reading in after.Except(before))
        {
            Change($"+ reading {reading}");
        }

        foreach (var reading in before.Except(after))
        {
            Change($"- reading {reading}");
        }

        if (changes == 0)
        {
            sb.AppendLine("No semantic changes.");
        }
        else if (locked.SourceHash != current.SourceHash && changes == 1)
        {
            sb.AppendLine("The text changed but it binds the same way.");
        }

        return sb.ToString();
    }
}
