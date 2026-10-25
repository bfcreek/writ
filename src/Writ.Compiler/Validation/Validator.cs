using Writ.Compiler.Diagnostics;
using Writ.Compiler.Ir;

namespace Writ.Compiler.Validation;

public sealed class Validator
{
    private readonly string? _filePath;
    private readonly DiagnosticBag _diagnostics;

    public Validator(string? filePath, DiagnosticBag diagnostics)
    {
        _filePath = filePath;
        _diagnostics = diagnostics;
    }

    public void Validate(IrModule module)
    {
        foreach (var function in module.Functions)
        {
            if (function.ReturnType is not IrVoidType && !ReturnsOnAllPaths(function.Body))
            {
                _diagnostics.Error("E3001",
                    $"Function '{function.Name}' does not return a value on all code paths.",
                    _filePath, function.Span);
            }

            ValidateEffects(function.Body);
        }
    }

    private static bool ReturnsOnAllPaths(IReadOnlyList<IrNode> body)
    {
        for (var i = 0; i < body.Count; i++)
        {
            switch (body[i])
            {
                case IrReturn:
                    return true;
                case IrIf iff:
                    var thenReturns = ReturnsOnAllPaths(iff.ThenBody);
                    var elseReturns = iff.OtherwiseBody is not null && ReturnsOnAllPaths(iff.OtherwiseBody);
                    if (thenReturns && elseReturns)
                    {
                        return true;
                    }
                    break;
                case IrTry tryNode:
                    // A try returns on all paths when both the body and the failure handler do,
                    // or when the finally block does.
                    if (tryNode.Finally is not null && ReturnsOnAllPaths(tryNode.Finally))
                    {
                        return true;
                    }

                    if (ReturnsOnAllPaths(tryNode.Body) && tryNode.OnFailure is not null && ReturnsOnAllPaths(tryNode.OnFailure))
                    {
                        return true;
                    }
                    break;
            }
        }

        return false;
    }

    private void ValidateEffects(IReadOnlyList<IrNode> nodes)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case IrCall call:
                    foreach (var effect in call.Effects)
                    {
                        if (effect != EffectKind.Pure)
                        {
                            _diagnostics.Error("E3002",
                                $"Effect '{effect}' is not authorized.",
                                _filePath, call.Span);
                        }
                    }

                    ValidateEffects(call.Arguments);
                    break;
                case IrAssign assign:
                    ValidateEffects([assign.Value]);
                    break;
                case IrIf iff:
                    ValidateEffects([iff.Condition]);
                    ValidateEffects(iff.ThenBody);
                    if (iff.OtherwiseBody is not null)
                    {
                        ValidateEffects(iff.OtherwiseBody);
                    }
                    break;
                case IrForEach loop:
                    ValidateEffects([loop.Collection]);
                    ValidateEffects(loop.Body);
                    break;
                case IrReturn ret:
                    ValidateEffects([ret.Value]);
                    break;
                case IrBinary bin:
                    ValidateEffects([bin.Left, bin.Right]);
                    break;
                case IrUnary unary:
                    ValidateEffects([unary.Operand]);
                    break;
                case IrFieldAccess field:
                    ValidateEffects([field.Target]);
                    break;
                case IrListLiteral list:
                    ValidateEffects(list.Elements);
                    break;
                case IrSumProjection sum:
                    ValidateEffects([sum.Collection, sum.Projection]);
                    break;
                case IrFilter filter:
                    ValidateEffects([filter.Collection, filter.Predicate]);
                    break;
                case IrCapabilityCall capability:
                    ValidateEffects(capability.Arguments.Select(a => a.Value).ToList());
                    break;
                case IrSequence sequence:
                    ValidateEffects(sequence.Steps);
                    break;
                case IrCommand command:
                    ValidateEffects(command.Arguments.Select(a => a.Value).ToList());
                    break;
                case IrScriptCall script:
                    ValidateEffects(script.Arguments.Select(a => a.Value).ToList());
                    break;
                case IrSort sort:
                    ValidateEffects([sort.Collection]);
                    break;
                case IrValidate validate:
                    ValidateEffects([validate.Value]);
                    break;
                case IrTake take:
                    ValidateEffects([take.Collection, take.Count]);
                    break;
                case IrSelectField select:
                    ValidateEffects([select.Collection]);
                    break;
                case IrSay say:
                    ValidateEffects(say.Parts);
                    break;
                case IrWhile loop:
                    ValidateEffects([loop.Condition]);
                    ValidateEffects(loop.Body);
                    break;
                case IrDo repeat:
                    ValidateEffects([repeat.Condition]);
                    ValidateEffects(repeat.Body);
                    break;
                case IrFor loop:
                    ValidateEffects([loop.From, loop.To]);
                    ValidateEffects(loop.Body);
                    break;
                case IrSwitch branch:
                    ValidateEffects([branch.Value]);
                    foreach (var arm in branch.Arms)
                    {
                        ValidateEffects(arm.Values);
                        ValidateEffects(arm.Body);
                    }

                    if (branch.Otherwise is not null)
                    {
                        ValidateEffects(branch.Otherwise);
                    }

                    break;
                case IrTrap trap:
                    ValidateEffects(trap.Body);
                    break;
                case IrData data:
                    ValidateEffects(data.Body);
                    break;
                case IrSection section:
                    ValidateEffects(section.Body);
                    break;
                case IrLabel label:
                    ValidateEffects([label.Statement]);
                    break;
                case IrExit exit when exit.Code is not null:
                    ValidateEffects([exit.Code]);
                    break;
                case IrRedirect redirect:
                    ValidateEffects([redirect.Command]);
                    if (redirect.Target is not null)
                    {
                        ValidateEffects([redirect.Target]);
                    }

                    break;
                case IrDotSource sourced:
                    ValidateEffects([sourced.Path]);
                    break;
                case IrTry tryNode:
                    ValidateEffects(tryNode.Body);
                    if (tryNode.OnFailure is not null)
                    {
                        ValidateEffects(tryNode.OnFailure);
                    }

                    if (tryNode.Finally is not null)
                    {
                        ValidateEffects(tryNode.Finally);
                    }
                    break;
                case IrRetry retry:
                    ValidateEffects([retry.Operation]);
                    break;
            }
        }
    }
}
