using Writ.Compiler.Capabilities;
using Writ.Compiler.Diagnostics;
using Writ.Compiler.Ir;
using Writ.Compiler.Syntax;

namespace Writ.Compiler.Binding;

public sealed partial class Binder
{
    private int _loopDepth;
    private IrRecordType? _methodFields;

    private IrParameter ToParameter(ParameterAst parameter) =>
        new(parameter.Name, ResolveType(parameter.Type))
        {
            FromPipeline = parameter.FromPipeline,
            Alias = parameter.Alias,
            OneOf = parameter.OneOf,
            Pattern = parameter.Pattern,
            Min = parameter.Min,
            Max = parameter.Max,
            MinLength = parameter.MinLength,
            MaxLength = parameter.MaxLength,
            MinCount = parameter.MinCount,
            MaxCount = parameter.MaxCount,
            AllowNull = parameter.AllowNull,
            AllowEmptyString = parameter.AllowEmptyString,
            AllowEmptyCollection = parameter.AllowEmptyCollection,
            SupportsWildcards = parameter.SupportsWildcards,
            RejectNull = parameter.RejectNull,
            RejectEmpty = parameter.RejectEmpty
        };

    private IrFunction BindFunctionBody(FunctionAst fn, FunctionSignature sig)
    {
        var scope = new Scope(null);
        var parameters = new List<IrParameter>();
        for (var i = 0; i < sig.Parameters.Count; i++)
        {
            var ir = sig.Parameters[i];
            scope.Define(ir.Name, ir.Type, fn.Span, _diagnostics, _filePath);
            if (i < fn.Parameters.Count && fn.Parameters[i].Default is { } fallback)
            {
                ir = ir with { Default = BindExpression(fallback, scope) };
            }

            if (i < fn.Parameters.Count && fn.Parameters[i].ValidateTest is { } test)
            {
                ir = ir with { ValidateTest = BindExpression(test, scope) };
            }

            parameters.Add(ir);
        }

        var body = fn.Body.Select(s => BindStatement(s, scope, sig.ReturnType)).ToList();
        body = RefineEmptyListDeclarations(body, scope);
        return new IrFunction(fn.Name, parameters, sig.ReturnType, body, fn.Span, sig.ReturnType)
        {
            IsEntry = fn.IsEntry,
            SourceFile = fn.SourceFile,
            IsFilter = fn.IsFilter,
            CmdletBinding = fn.CmdletBinding
        };
    }

    private IrFunction BindMethod(FunctionAst fn, IrRecordType type)
    {
        var signature = new FunctionSignature(fn.Name, fn.Parameters.Select(ToParameter).ToList(), ResolveType(fn.ReturnType), false);
        var previous = _methodFields;
        _methodFields = type;
        try
        {
            return BindFunctionBody(fn, signature) with { IsMethod = true };
        }
        finally
        {
            _methodFields = previous;
        }
    }

    private IrNode BindFlow(FlowStmtAst stmt, Scope scope)
    {
        if (stmt.Kind is "break" or "continue" && stmt.Label is null && _loopDepth == 0)
        {
            _diagnostics.Error("E2031", $"'{stmt.Kind}' belongs inside a loop or a switch.", _filePath, stmt.Span);
        }

        var code = stmt.Code is null ? null : BindExpression(stmt.Code, scope);
        return stmt.Kind switch
        {
            "continue" => new IrContinue(stmt.Label, stmt.Span, IrVoidType.Instance),
            "exit" => new IrExit(code, stmt.Span, IrVoidType.Instance),
            _ => new IrBreak(stmt.Label, stmt.Span, IrVoidType.Instance)
        };
    }

    private IrNode BindLabeled(LabeledStmtAst stmt, Scope scope, IrType returnType)
    {
        var inner = BindStatement(stmt.Statement, scope, returnType);
        return new IrLabel(stmt.Label, inner, stmt.Span, IrVoidType.Instance);
    }

    private IrNode BindDo(DoStmtAst stmt, Scope scope, IrType returnType)
    {
        var condition = BindCondition(stmt.Condition, scope);
        var bodyScope = new Scope(scope);
        _loopDepth++;
        var body = stmt.Body.Select(s => BindStatement(s, bodyScope, returnType)).ToList();
        _loopDepth--;
        return new IrDo(condition, body, stmt.Until, stmt.Span, IrVoidType.Instance);
    }

    private IrNode BindCountedFor(ForStmtAst stmt, Scope scope, IrType returnType)
    {
        var from = BindExpression(stmt.From, scope);
        var to = BindExpression(stmt.To, scope);
        if (!scope.TryLookup(stmt.Name, out _))
        {
            scope.Define(stmt.Name, IrPrimitiveType.Integer, stmt.Span, _diagnostics, _filePath);
        }

        var bodyScope = new Scope(scope);
        _loopDepth++;
        var body = stmt.Body.Select(s => BindStatement(s, bodyScope, returnType)).ToList();
        _loopDepth--;
        return new IrFor(stmt.Name, from, to, body, stmt.Span, IrVoidType.Instance);
    }

    private IrNode BindSwitch(SwitchStmtAst stmt, Scope scope, IrType returnType)
    {
        var value = BindExpression(stmt.Value, scope);
        _loopDepth++;
        var arms = stmt.Arms.Select(arm =>
        {
            var bodyScope = new Scope(scope);
            return new IrSwitchArm(
                arm.Values.Select(v => BindExpression(v, scope)).ToList(),
                arm.Body.Select(s => BindStatement(s, bodyScope, returnType)).ToList());
        }).ToList();
        IReadOnlyList<IrNode>? otherwise = null;
        if (stmt.Otherwise is not null)
        {
            var elseScope = new Scope(scope);
            otherwise = stmt.Otherwise.Select(s => BindStatement(s, elseScope, returnType)).ToList();
        }

        _loopDepth--;
        return new IrSwitch(value, arms, otherwise, stmt.Span, IrVoidType.Instance);
    }

    private IrNode BindBlock(BlockStmtAst stmt, Scope scope, IrType returnType)
    {
        var bodyScope = new Scope(scope);
        if (stmt.Kind == "trap")
        {
            bodyScope.Define("error", IrPrimitiveType.String, stmt.Span, _diagnostics, _filePath);
        }

        var body = stmt.Body.Select(s => BindStatement(s, bodyScope, returnType)).ToList();
        if (stmt.Kind is "begin" or "process" or "end" or "dynamicparam")
        {
            return new IrSection(stmt.Kind, body, stmt.Span, IrVoidType.Instance);
        }

        return stmt.Kind == "trap"
            ? new IrTrap(body, "error", stmt.Span, IrVoidType.Instance)
            : new IrData(body, stmt.Span, IrVoidType.Instance);
    }

    private IrNode BindRedirect(RedirectStmtAst stmt, Scope scope)
    {
        var command = BindExpression(stmt.Command, scope);
        IrNode? target = stmt.Target is null ? null : BindExpression(stmt.Target, scope);
        if (stmt.Operator != "2>&1" && target is not IrLiteral { Value: null }
            && !EffectGranted("redirect", EffectKind.FileSystemWrite, stmt.Span))
        {
            return new IrLiteral(null, stmt.Span, IrErrorType.Instance);
        }

        return new IrRedirect(stmt.Operator, command, target, stmt.Span, IrVoidType.Instance);
    }

    private IrNode BindModuleStatement(ModuleStmtAst stmt)
    {
        if (!EffectGranted(stmt.Kind, EffectKind.Script, stmt.Span))
        {
            return new IrLiteral(null, stmt.Span, IrErrorType.Instance);
        }

        var path = new IrLiteral(stmt.Name, stmt.Span, IrPrimitiveType.String);
        if (stmt.Kind == "source")
        {
            return new IrDotSource(path, stmt.Span, IrVoidType.Instance);
        }

        var command = stmt.Kind == "import" ? "Import-Module" : "Remove-Module";
        return new IrCommand(command, [new IrNamedArgument("Name", path)], [EffectKind.Script], $"pwsh:{command}", stmt.Span, IrVoidType.Instance);
    }

    private IrNode BindCondition(ExprAst expr, Scope scope)
    {
        var condition = BindExpression(expr, scope);
        if (!IsBoolean(condition.Type) && condition.Type is not IrErrorType)
        {
            _diagnostics.Error("E2004", "Loop condition must be boolean.", _filePath, expr.Span);
        }

        return condition;
    }

    private IrNode BindConstruct(ConstructExprAst expr, Scope scope)
    {
        var args = expr.Arguments.Select(a => BindExpression(a, scope)).ToList();
        IrType type = expr.Operator switch
        {
            "range" => new IrListType(IrPrimitiveType.Integer),
            "hash" => IrPrimitiveType.Table,
            "join" or "format" or "replace" => IrPrimitiveType.String,
            "split" => new IrListType(IrPrimitiveType.String),
            "index" when args.Count > 0 && args[0].Type is IrListType list => list.ElementType,
            "slice" when args.Count > 0 && args[0].Type is IrListType list => list,
            "slice" => new IrListType(IrDynamicType.Instance),
            "index" => IrDynamicType.Instance,
            "pipe" when args.Count > 0 && args[0].Type is IrListType list => list,
            "pipe" or "output" or "member" or "static" or "call" or "grid" => IrDynamicType.Instance,
            "andThen" or "orElse" => IrPrimitiveType.Boolean,
            "collected" => new IrListType(IrDynamicType.Instance),
            "block" => IrPrimitiveType.Block,
            "coalesce" or "ternary" when args.Count > 0 => args[0].Type,
            "as" or "cast" when args.Count > 1 && args[1] is IrLiteral { Value: string name } => ResolveNamed(name),
            "optional" when args.Count == 1 => args[0].Type,
            "auto" when args.Count == 1 && args[0] is IrLiteral { Value: "succeeded" } => IrPrimitiveType.Boolean,
            "scope" when args.Count == 2 && args[1] is IrLiteral { Value: string scoped } && scope.TryLookup(scoped, out var found) && found is not null => found,
            "scope" or "auto" or "splat" => IrDynamicType.Instance,
            _ => IrPrimitiveType.Boolean
        };

        if (expr.Operator == "auto" && args.Count == 1 && args[0] is IrLiteral { Value: string auto })
        {
            return new IrVar(auto, expr.Span, type);
        }

        if (expr.Operator == "scope" && args.Count == 2 && args[0] is IrLiteral { Value: string scopeName } && args[1] is IrLiteral { Value: string varName })
        {
            return new IrVar(varName, expr.Span, type) { Scope = scopeName };
        }

        if (expr.Operator == "optional" && args.Count == 1 && args[0] is IrFieldAccess field)
        {
            return field with { Optional = true };
        }

        return new IrCall(expr.Operator, args, true, [EffectKind.Pure], expr.Span, type);
    }

    private IrType ResolveNamed(string name) => name switch
    {
        "integer" => IrPrimitiveType.Integer,
        "decimal" => IrPrimitiveType.Decimal,
        "string" => IrPrimitiveType.String,
        "boolean" => IrPrimitiveType.Boolean,
        "character" or "char" => IrPrimitiveType.Character,
        "float" or "double" or "number" => IrPrimitiveType.Float,
        "table" or "hashtable" => IrPrimitiveType.Table,
        "pattern" or "regex" => IrPrimitiveType.Pattern,
        "xml" => IrPrimitiveType.Xml,
        "block" or "scriptblock" => IrPrimitiveType.Block,
        "object" or "pscustomobject" => IrPrimitiveType.Object,
        "switch" => IrPrimitiveType.Switch,
        _ when _records.TryGetValue(name, out var record) => record,
        _ => new IrPrimitiveType(name)
    };
}
