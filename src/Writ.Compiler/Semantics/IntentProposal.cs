using System.Text.Json.Serialization;

namespace Writ.Compiler.Semantics;

/// <summary>
/// Constrained proposal schema for Milestone B. Resolvers must emit one of these shapes;
/// the binder never trusts free-form IR from a model.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SumProjectionProposal), "sumProjection")]
[JsonDerivedType(typeof(FilterProposal), "filter")]
public abstract record IntentProposal;

/// <summary>
/// sum of &lt;projection&gt; for each &lt;iterator&gt; in &lt;collection&gt;
/// </summary>
public sealed record SumProjectionProposal(
    string Collection,
    string Iterator,
    ProposalExpr Projection) : IntentProposal;

/// <summary>
/// &lt;collection&gt; where &lt;predicate&gt; (returns list of same element type)
/// </summary>
public sealed record FilterProposal(
    string Collection,
    string Iterator,
    ProposalExpr Predicate) : IntentProposal;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "exprKind")]
[JsonDerivedType(typeof(ProposalField), "field")]
[JsonDerivedType(typeof(ProposalLiteral), "literal")]
[JsonDerivedType(typeof(ProposalBinary), "binary")]
[JsonDerivedType(typeof(ProposalVar), "var")]
public abstract record ProposalExpr;

public sealed record ProposalField(string Field) : ProposalExpr;

public sealed record ProposalVar(string Name) : ProposalExpr;

public sealed record ProposalLiteral(object? Value) : ProposalExpr;

public sealed record ProposalBinary(ProposalExpr Left, string Operator, ProposalExpr Right) : ProposalExpr;

public sealed record IntentProposalCandidate(
    IntentProposal Proposal,
    string Rationale,
    IReadOnlyList<string> Assumptions);
