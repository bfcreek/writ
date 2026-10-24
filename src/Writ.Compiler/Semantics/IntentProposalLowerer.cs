using Writ.Compiler.Diagnostics;
using Writ.Compiler.Ir;

namespace Writ.Compiler.Semantics;

/// <summary>
/// Validates schema proposals against symbols/types and lowers them to typed IR.
/// Independent of how the proposal was produced (rules or model).
/// </summary>
public sealed class IntentProposalLowerer
{
    private readonly string? _filePath;
    private readonly DiagnosticBag _diagnostics;

    public IntentProposalLowerer(string? filePath, DiagnosticBag diagnostics)
    {
        _filePath = filePath;
        _diagnostics = diagnostics;
    }

    public IrNode? Lower(
        IntentProposal proposal,
        SourceSpan span,
        Func<string, IrType?> lookupSymbol)
    {
        return proposal switch
        {
            SumProjectionProposal sum => LowerSum(sum, span, lookupSymbol),
            FilterProposal filter => LowerFilter(filter, span, lookupSymbol),
            _ => Fail(span, $"Unsupported intent proposal kind '{proposal.GetType().Name}'.")
        };
    }

    private IrNode? LowerSum(
        SumProjectionProposal proposal,
        SourceSpan span,
        Func<string, IrType?> lookupSymbol)
    {
        var collectionType = lookupSymbol(proposal.Collection);
        if (collectionType is not IrListType list)
        {
            return Fail(span, $"Sum projection collection '{proposal.Collection}' is not a list in scope.");
        }

        if (list.ElementType is not IrRecordType record)
        {
            return Fail(span, list.ElementType is IrDynamicType
                ? $"'{proposal.Collection}' holds untyped data. Say what it is first, for example 'treat the {proposal.Collection} as a list of Item'."
                : "Sum projection requires a list of record elements.");
        }

        var projection = BindProposalExpr(proposal.Projection, proposal.Iterator, record, span);
        if (projection is null)
        {
            return null;
        }

        if (!IsNumeric(projection.Type) && projection.Type is not IrErrorType)
        {
            return Fail(span, "Sum projection must produce a numeric value.");
        }

        IrType resultType = IsInteger(projection.Type)
            ? IrPrimitiveType.Integer
            : IrPrimitiveType.Decimal;

        var collection = new IrVar(proposal.Collection, span, list, Provenance.ResolvedByModel);
        return new IrSumProjection(
            collection,
            proposal.Iterator,
            projection,
            span,
            resultType,
            Provenance.ResolvedByModel);
    }

    private IrNode? LowerFilter(
        FilterProposal proposal,
        SourceSpan span,
        Func<string, IrType?> lookupSymbol)
    {
        var collectionType = lookupSymbol(proposal.Collection);
        if (collectionType is not IrListType list)
        {
            return Fail(span, $"Filter collection '{proposal.Collection}' is not a list in scope.");
        }

        if (list.ElementType is not IrRecordType and not IrDynamicType)
        {
            return Fail(span, "Filter requires a list of record elements.");
        }

        var predicate = BindProposalExpr(proposal.Predicate, proposal.Iterator, list.ElementType, span);
        if (predicate is null)
        {
            return null;
        }

        if (predicate.Type is not IrPrimitiveType { Name: "boolean" } and not IrErrorType)
        {
            return Fail(span, "Filter predicate must be boolean.");
        }

        var collection = new IrVar(proposal.Collection, span, list, Provenance.ResolvedByModel);
        return new IrFilter(
            collection,
            proposal.Iterator,
            predicate,
            span,
            list,
            Provenance.ResolvedByModel);
    }

    private IrNode? BindProposalExpr(
        ProposalExpr expr,
        string iterator,
        IrType element,
        SourceSpan span)
    {
        switch (expr)
        {
            case ProposalField field:
            {
                var target = new IrVar(iterator, span, element, Provenance.ResolvedByModel);
                if (element is IrDynamicType)
                {
                    return new IrFieldAccess(target, field.Field, span, IrDynamicType.Instance, Provenance.ResolvedByModel);
                }

                var record = (IrRecordType)element;
                var irField = record.Fields.FirstOrDefault(f => f.Name == field.Field);
                if (irField is null)
                {
                    return Fail(span,
                        $"Unknown field '{field.Field}' on '{record.Name}'. Available: {string.Join(", ", record.Fields.Select(f => f.Name))}.");
                }

                return new IrFieldAccess(target, field.Field, span, irField.Type, Provenance.ResolvedByModel);
            }
            case ProposalVar varExpr:
                if (varExpr.Name == iterator)
                {
                    return new IrVar(iterator, span, element, Provenance.ResolvedByModel);
                }

                return Fail(span, $"Unknown name '{varExpr.Name}' in intent proposal.");

            case ProposalLiteral lit:
            {
                IrType litType = lit.Value switch
                {
                    long => IrPrimitiveType.Integer,
                    int => IrPrimitiveType.Integer,
                    decimal => IrPrimitiveType.Decimal,
                    string => IrPrimitiveType.String,
                    bool => IrPrimitiveType.Boolean,
                    _ => IrErrorType.Instance
                };
                object? value = lit.Value is int i ? (long)i : lit.Value;
                return new IrLiteral(value, span, litType, Provenance.ResolvedByModel);
            }
            case ProposalBinary binary:
            {
                var left = BindProposalExpr(binary.Left, iterator, element, span);
                var right = BindProposalExpr(binary.Right, iterator, element, span);
                if (left is null || right is null)
                {
                    return null;
                }

                var type = InferBinaryType(binary.Operator, left.Type, right.Type, span);
                return new IrBinary(left, binary.Operator, right, span, type, Provenance.ResolvedByModel);
            }
            default:
                return Fail(span, $"Unsupported proposal expression '{expr.GetType().Name}'.");
        }
    }

    private IrType InferBinaryType(string op, IrType left, IrType right, SourceSpan span)
    {
        if (left is IrErrorType || right is IrErrorType)
        {
            return IrErrorType.Instance;
        }

        if (left is IrDynamicType || right is IrDynamicType)
        {
            return op is "and" or "or" or "==" or "!=" or "<" or "<=" or ">" or ">="
                ? IrPrimitiveType.Boolean
                : IrDynamicType.Instance;
        }

        return op switch
        {
            "and" or "or" => IrPrimitiveType.Boolean,
            "==" or "!=" or "<" or "<=" or ">" or ">=" => IrPrimitiveType.Boolean,
            "*" or "+" or "-" or "/" or "%" when IsNumeric(left) && IsNumeric(right) =>
                IsInteger(left) && IsInteger(right) && op != "/"
                    ? IrPrimitiveType.Integer
                    : IrPrimitiveType.Decimal,
            _ => FailType(span, $"Invalid operator '{op}' in intent proposal.")
        };
    }

    private IrNode? Fail(SourceSpan span, string message)
    {
        _diagnostics.Error("E2105", message, _filePath, span);
        return null;
    }

    private IrType FailType(SourceSpan span, string message)
    {
        _diagnostics.Error("E2105", message, _filePath, span);
        return IrErrorType.Instance;
    }

    private static bool IsNumeric(IrType type) =>
        type is IrPrimitiveType { Name: "integer" or "decimal" };

    private static bool IsInteger(IrType type) =>
        type is IrPrimitiveType { Name: "integer" };
}
