using System.Text.RegularExpressions;
using Writ.Compiler.Compilation;
using Writ.Compiler.Diagnostics;
using Writ.Compiler.Ir;
using Writ.Compiler.Semantics;

namespace Writ.Compiler.Capabilities;

internal sealed record ScriptParameter(string Name, IrType Type, bool Required);

internal sealed record ResolvedScript(
    string FullPath,
    string Kind,
    IReadOnlyList<ScriptParameter> Parameters,
    IrType ResultType,
    IReadOnlyList<EffectKind> Effects,
    string? CompiledScript);

/// <summary>Tracks scripts currently being compiled so a cycle is an error instead of a loop.</summary>
internal static class ScriptCompileStack
{
    private static readonly AsyncLocal<HashSet<string>?> Current = new();

    private static HashSet<string> Set => Current.Value ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string path) => Current.Value?.Contains(path) == true;

    public static bool Enter(string path) => Set.Add(path);

    public static void Exit(string path) => Current.Value?.Remove(path);
}

/// <summary>
/// Reads a script's contract without running it. A Writ script is compiled for its signature
/// and its generated PowerShell; a .ps1 is parsed for <c>param()</c> and <c>.OUTPUTS</c>.
/// </summary>
internal static class ScriptCalls
{
    public static ResolvedScript? Resolve(
        string pathText,
        string sourceDirectory,
        string? sourceFile,
        DiagnosticBag diagnostics,
        SourceSpan span,
        ISemanticResolver semanticResolver,
        ICapabilityLoader capabilities,
        ILanguageModel model,
        CmdletCatalog cmdlets)
    {
        var display = pathText.Trim();
        var full = Path.GetFullPath(Path.IsPathRooted(display) ? display : Path.Combine(sourceDirectory, display));
        var kind = KindOf(full);
        if (kind is null)
        {
            diagnostics.Error("E4010",
                $"\"{display}\" is not a Writ script (.writ) or a PowerShell script (.ps1).",
                sourceFile, span);
            return null;
        }

        if (!File.Exists(full))
        {
            diagnostics.Error("E4010", $"Could not find \"{display}\".", sourceFile, span);
            return null;
        }

        return kind == "writ"
            ? ResolveWrit(full, display, sourceFile, diagnostics, span, semanticResolver, capabilities, model, cmdlets)
            : ResolvePowerShell(full);
    }

    private static ResolvedScript? ResolveWrit(
        string full,
        string display,
        string? sourceFile,
        DiagnosticBag diagnostics,
        SourceSpan span,
        ISemanticResolver semanticResolver,
        ICapabilityLoader capabilities,
        ILanguageModel model,
        CmdletCatalog cmdlets)
    {
        if (ScriptCompileStack.Contains(full))
        {
            diagnostics.Error("E4012", $"\"{display}\" calls itself.", sourceFile, span);
            return null;
        }

        var pipeline = new CompilationPipeline(semanticResolver, capabilities, model, cmdlets);
        var result = pipeline.Compile(File.ReadAllText(full), full);
        if (!result.Success || result.Ir is null || result.GeneratedPowerShell is null)
        {
            var cycle = result.Diagnostics.Any(d => d.Code == "E4012");
            var details = string.Join(" ", result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.Message)
                .Take(3));
            diagnostics.Error(cycle ? "E4012" : "E4011",
                $"\"{display}\" did not check. {details}".Trim(),
                sourceFile, span);
            return null;
        }

        var entry = result.Ir.Functions.FirstOrDefault(f => f.IsEntry);
        if (entry is null)
        {
            diagnostics.Error("E4011",
                $"\"{display}\" has no 'When run' block, so there is nothing to run.",
                sourceFile, span);
            return null;
        }

        var effects = result.GrantedEffects.Where(e => e != EffectKind.Pure).Distinct().ToList();
        if (effects.Count == 0)
        {
            effects.Add(EffectKind.Pure);
        }

        return new ResolvedScript(
            full,
            "writ",
            entry.Parameters.Select(p => new ScriptParameter(p.Name, p.Type, Required: true)).ToList(),
            entry.ReturnType,
            effects,
            result.GeneratedPowerShell);
    }

    private static ResolvedScript ResolvePowerShell(string full)
    {
        var text = File.ReadAllText(full);
        return new ResolvedScript(
            full,
            "powershell",
            ReadParameters(text),
            ReadOutputs(text),
            [EffectKind.Script],
            null);
    }

    private static string? KindOf(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".writ", StringComparison.OrdinalIgnoreCase))
        {
            return "writ";
        }

        if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            return "powershell";
        }

        return null;
    }

    private static IrType ReadOutputs(string text)
    {
        var match = Regex.Match(text, @"(?im)^[ \t]*\.OUTPUTS\s*$");
        if (!match.Success)
        {
            return IrDynamicType.Instance;
        }

        var rest = text[(match.Index + match.Length)..];
        foreach (var line in rest.Split('\n'))
        {
            var trimmed = line.Trim().TrimStart('#').Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith('.') || trimmed.StartsWith("#>"))
            {
                break;
            }

            return MapTypeName(trimmed) ?? IrDynamicType.Instance;
        }

        return IrDynamicType.Instance;
    }

    private static List<ScriptParameter> ReadParameters(string text)
    {
        var match = Regex.Match(text, @"(?m)^[ \t]*param\s*\(");
        if (!match.Success)
        {
            return [];
        }

        var open = text.IndexOf('(', match.Index);
        var close = FindMatchingParen(text, open);
        if (close < 0)
        {
            return [];
        }

        var parameters = new List<ScriptParameter>();
        foreach (var chunk in SplitTopLevel(text[(open + 1)..close], ','))
        {
            var piece = chunk.Trim();
            if (piece.Length == 0)
            {
                continue;
            }

            var nameMatch = Regex.Match(piece, @"\$([A-Za-z_][A-Za-z0-9_]*)");
            if (!nameMatch.Success)
            {
                continue;
            }

            var typeMatch = Regex.Match(piece, @"\[([A-Za-z0-9_.]+)\]\s*\$");
            var type = typeMatch.Success ? MapTypeName(typeMatch.Groups[1].Value) ?? IrDynamicType.Instance : IrDynamicType.Instance;
            var isSwitch = typeMatch.Success && typeMatch.Groups[1].Value.Equals("switch", StringComparison.OrdinalIgnoreCase);
            if (isSwitch)
            {
                type = IrPrimitiveType.Boolean;
            }

            var mandatory = Regex.IsMatch(piece, @"Mandatory\s*=\s*\$true", RegexOptions.IgnoreCase)
                || Regex.IsMatch(piece, @"Mandatory\s*(?=,|\)|\])", RegexOptions.IgnoreCase);
            var required = !isSwitch && (mandatory || !HasTopLevelEquals(piece));
            parameters.Add(new ScriptParameter(nameMatch.Groups[1].Value, type, required));
        }

        return parameters;
    }

    private static bool HasTopLevelEquals(string piece)
    {
        var depth = 0;
        var quote = '\0';
        for (var i = 0; i < piece.Length; i++)
        {
            var ch = piece[i];
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (ch is '(' or '[' or '{')
            {
                depth++;
                continue;
            }

            if (ch is ')' or ']' or '}')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (ch == '=' && depth == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static int FindMatchingParen(string text, int open)
    {
        var depth = 0;
        var quote = '\0';
        for (var i = open; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var start = 0;
        var depth = 0;
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (ch is '(' or '[' or '{')
            {
                depth++;
                continue;
            }

            if (ch is ')' or ']' or '}')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (ch == separator && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }

    private static IrType? MapTypeName(string text)
    {
        var name = text.Trim().Trim('[', ']').ToLowerInvariant();
        if (name.StartsWith("system."))
        {
            name = name["system.".Length..];
        }

        const string listOf = "list of ";
        if (name.StartsWith(listOf, StringComparison.Ordinal))
        {
            return new IrListType(MapTypeName(name[listOf.Length..]) ?? IrDynamicType.Instance);
        }

        return name switch
        {
            "string" or "char" => IrPrimitiveType.String,
            "int" or "int32" or "int64" or "long" or "uint32" or "uint64" or "integer" => IrPrimitiveType.Integer,
            "decimal" or "double" or "single" or "float" => IrPrimitiveType.Decimal,
            "bool" or "boolean" or "switch" => IrPrimitiveType.Boolean,
            "void" or "nothing" => IrVoidType.Instance,
            "object" or "dynamic" or "untyped data" => IrDynamicType.Instance,
            _ => null
        };
    }
}
