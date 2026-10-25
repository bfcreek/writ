using System.Text;
using Writ.Compiler.Ir;

namespace Writ.Compiler.Codegen;

public sealed partial class PowerShellGenerator
{
    private void EmitAssignment(StringBuilder sb, IrAssign assign, string pad)
    {
        var name = string.IsNullOrEmpty(assign.Scope) ? $"${Var(assign.Name)}" : $"${assign.Scope}:{Var(assign.Name)}";
        if (assign.Option is "readonly" or "constant")
        {
            var option = assign.Option == "constant" ? "Constant" : "ReadOnly";
            sb.AppendLine($"{pad}Set-Variable -Name {Quote(Var(assign.Name))} -Value {Expression(assign.Value)} -Option {option}");
            return;
        }

        if (assign.Operator is "++" or "--")
        {
            sb.AppendLine($"{pad}{name}{assign.Operator}");
            return;
        }

        sb.Append($"{pad}{name} {assign.Operator} ");
        if (assign.Operator == "=" && assign.Value is IrListLiteral { Elements.Count: 0 } && assign.Type is IrListType listType)
        {
            sb.Append($"[{ListTypeName(listType)}]::new()");
        }
        else
        {
            sb.Append(Expression(assign.Value));
        }

        sb.AppendLine();
    }

    private void EmitFunctionAttributes(StringBuilder sb, IrFunction function, string pad)
    {
        if (function.CmdletBinding)
        {
            sb.AppendLine($"{pad}[CmdletBinding()]");
        }

        if (function.ReturnType is not IrVoidType and not IrErrorType)
        {
            sb.AppendLine($"{pad}[OutputType([{TypeName(function.ReturnType)}])]");
        }
    }

    private void EmitFunctionBody(StringBuilder sb, IrFunction function, int indent, string? sourcePath)
    {
        var sections = function.Body.OfType<IrSection>().ToList();
        if (sections.Count == 0)
        {
            foreach (var stmt in function.Body)
            {
                EmitStatement(sb, stmt, indent, sourcePath);
            }

            return;
        }

        var rest = function.Body.Where(node => node is not IrSection).ToList();
        foreach (var name in new[] { "dynamicparam", "begin", "process", "end" })
        {
            var section = sections.FirstOrDefault(item => item.Name == name);
            var content = name == "end" ? rest.Concat(section?.Body ?? []).ToList() : section?.Body;
            if (section is null && (content is null || content.Count == 0))
            {
                continue;
            }

            var pad = new string(' ', indent * 4);
            sb.AppendLine($"{pad}{name} {{");
            foreach (var stmt in content ?? [])
            {
                EmitStatement(sb, stmt, indent + 1, sourcePath);
            }

            sb.AppendLine($"{pad}}}");
        }
    }

    private void EmitMethod(StringBuilder sb, IrFunction method)
    {
        sb.Append($"    [{TypeName(method.ReturnType)}] {Sanitize(method.Name)}(");
        sb.Append(string.Join(", ", method.Parameters.Select(FormatParameter)));
        sb.AppendLine(") {");
        foreach (var stmt in method.Body)
        {
            EmitStatement(sb, stmt, 2, null);
        }

        sb.AppendLine("    }");
    }

    private string FormatParameter(IrParameter parameter)
    {
        var sb = new StringBuilder();
        if (parameter.Alias is not null)
        {
            sb.Append($"[Alias({Quote(parameter.Alias)})]");
        }

        if (parameter.AllowNull)
        {
            sb.Append("[AllowNull()]");
        }

        if (parameter.AllowEmptyString)
        {
            sb.Append("[AllowEmptyString()]");
        }

        if (parameter.AllowEmptyCollection)
        {
            sb.Append("[AllowEmptyCollection()]");
        }

        if (parameter.SupportsWildcards)
        {
            sb.Append("[SupportsWildcards()]");
        }

        if (parameter.RejectNull)
        {
            sb.Append("[ValidateNotNull()]");
        }

        if (parameter.RejectEmpty)
        {
            sb.Append("[ValidateNotNullOrEmpty()]");
        }

        if (parameter.OneOf is { Count: > 0 })
        {
            sb.Append($"[ValidateSet({string.Join(", ", parameter.OneOf.Select(Quote))})]");
        }

        if (parameter.Pattern is not null)
        {
            sb.Append($"[ValidatePattern({Quote(parameter.Pattern)})]");
        }

        if (parameter.Min is not null && parameter.Max is not null)
        {
            sb.Append($"[ValidateRange({parameter.Min}, {parameter.Max})]");
        }

        if (parameter.MinLength is not null && parameter.MaxLength is not null)
        {
            sb.Append($"[ValidateLength({parameter.MinLength}, {parameter.MaxLength})]");
        }

        if (parameter.MinCount is not null && parameter.MaxCount is not null)
        {
            sb.Append($"[ValidateCount({parameter.MinCount}, {parameter.MaxCount})]");
        }

        if (parameter.FromPipeline)
        {
            sb.Append("[Parameter(ValueFromPipeline)]");
        }

        if (parameter.ValidateTest is not null)
        {
            sb.Append($"[ValidateScript({{ {Expression(parameter.ValidateTest)} }})]");
        }

        sb.Append(TypeAttribute(parameter.Type));
        sb.Append('$').Append(Var(parameter.Name));
        if (parameter.Default is not null)
        {
            sb.Append(" = ").Append(Expression(parameter.Default));
        }

        return sb.ToString();
    }

    private string? EmitConstructCall(IrCall call)
    {
        var args = call.Arguments;
        string Arg(int index) => Expression(args[index]);
        string TypeOf(int index) => args[index] is IrLiteral { Value: string name } ? PsType(name) : "object";
        switch (call.Callee)
        {
            case "range" when args.Count == 2:
                return $"({Arg(0)}..{Arg(1)})";
            case "index" when args.Count == 2:
                return $"@({Arg(0)})[{Arg(1)}]";
            case "slice" when args.Count == 3:
                return $"@({Arg(0)})[{Arg(1)}..{Arg(2)}]";
            case "join" when args.Count == 1:
                return $"(-join {Arg(0)})";
            case "join" when args.Count == 2:
                return $"({Arg(0)} -join {Arg(1)})";
            case "split":
                return $"({Arg(0)} -split {Arg(1)})";
            case "format":
                return $"({Arg(0)} -f {string.Join(", ", args.Skip(1).Select(Expression))})";
            case "replace":
                return $"({Arg(0)} -replace {Arg(1)}, {Arg(2)})";
            case "like": return $"({Arg(0)} -like {Arg(1)})";
            case "notlike": return $"({Arg(0)} -notlike {Arg(1)})";
            case "match": return $"({Arg(0)} -match {Arg(1)})";
            case "notmatch": return $"({Arg(0)} -notmatch {Arg(1)})";
            case "contains": return $"({Arg(0)} -contains {Arg(1)})";
            case "notcontains": return $"({Arg(0)} -notcontains {Arg(1)})";
            case "in": return $"({Arg(0)} -in {Arg(1)})";
            case "notin": return $"({Arg(0)} -notin {Arg(1)})";
            case "xor": return $"({Arg(0)} -xor {Arg(1)})";
            case "band": return $"({Arg(0)} -band {Arg(1)})";
            case "bor": return $"({Arg(0)} -bor {Arg(1)})";
            case "bxor": return $"({Arg(0)} -bxor {Arg(1)})";
            case "bnot": return $"(-bnot {Arg(0)})";
            case "shl": return $"({Arg(0)} -shl {Arg(1)})";
            case "shr": return $"({Arg(0)} -shr {Arg(1)})";
            case "is": return $"({Arg(0)} -is [{TypeOf(1)}])";
            case "isnot": return $"({Arg(0)} -isnot [{TypeOf(1)}])";
            case "as": return $"({Arg(0)} -as [{TypeOf(1)}])";
            case "cast": return $"([{TypeOf(1)}]{Arg(0)})";
            case "coalesce": return $"({Arg(0)} ?? {Arg(1)})";
            case "ternary": return $"(({Arg(1)}) ? {Arg(0)} : {Arg(2)})";
            case "andThen": return $"({Arg(0)}) && ({Arg(1)})";
            case "orElse": return $"({Arg(0)}) || ({Arg(1)})";
            case "hash":
                var ordered = args.Count > 0 && args[0] is IrLiteral { Value: true };
                var pairs = new List<string>();
                for (var i = 1; i + 1 < args.Count; i += 2)
                {
                    var key = args[i] is IrLiteral { Value: string text } ? text : Arg(i);
                    pairs.Add($"{key} = {Expression(args[i + 1])}");
                }

                return ordered ? $"[ordered]@{{ {string.Join("; ", pairs)} }}" : $"@{{ {string.Join("; ", pairs)} }}";
            case "block":
                var closure = args.Count > 1 && args[1] is IrLiteral { Value: true };
                var block = $"{{ {Arg(0)} }}";
                return closure ? $"({block}).GetNewClosure()" : block;
            case "output": return $"$({Arg(0)})";
            case "collected": return $"@({Arg(0)})";
            case "pipe" when args.Count == 2 && args[1] is IrCall { Callee: "block" }:
                return $"({Arg(0)} | ForEach-Object {Arg(1)})";
            case "pipe" when args.Count == 2 && args[1] is IrLiteral { Value: string command }:
                return $"({Arg(0)} | {command})";
            case "pipe":
                return $"({Arg(0)} | {Arg(1)})";
            case "static":
                var member = args[1] is IrLiteral { Value: string memberName } ? memberName : "Member";
                var type = args[0] is IrLiteral { Value: string typeName } ? typeName : "object";
                return args.Count == 2
                    ? $"[{type}]::{member}"
                    : $"[{type}]::{member}({string.Join(", ", args.Skip(2).Select(Expression))})";
            case "member":
                var called = args[1] is IrLiteral { Value: string calledName } ? calledName : "Member";
                var passed = args.Count > 2 ? string.Join(", ", args.Skip(2).Select(Expression)) : "";
                return $"({Arg(0)}).{called}({passed})";
            case "call":
                return $"& {Arg(0)}";
            case "grid":
                return $"(New-Object 'long[,]' {Arg(0)}, {Arg(1)})";
            case "char":
            case "float":
            case "pattern":
            case "xml":
            case "object":
                return $"([{PsType(call.Callee)}]{Arg(0)})";
            default:
                return null;
        }
    }

    private static string PsType(string name) => name switch
    {
        "integer" => "long",
        "decimal" => "decimal",
        "string" => "string",
        "boolean" => "bool",
        "character" or "char" => "char",
        "float" or "double" or "number" => "double",
        "table" or "hashtable" => "hashtable",
        "pattern" or "regex" => "regex",
        "xml" => "xml",
        "block" or "scriptblock" => "scriptblock",
        "object" or "pscustomobject" => "pscustomobject",
        "switch" => "switch",
        var other => other
    };

    private static string DictionaryType(string name)
    {
        var parts = name.Split(':');
        var key = parts.Length > 1 ? PsType(parts[1]) : "object";
        var value = parts.Length > 2 ? PsType(parts[2]) : "object";
        return $"System.Collections.Generic.Dictionary[{key},{value}]";
    }

    private static string GridType(string name)
    {
        var element = name.Split(':').Length > 1 ? PsType(name.Split(':')[1]) : "object";
        return $"{element}[,]";
    }
}
