using Writ.Compiler.Diagnostics;

namespace Writ.Compiler.Semantics;

/// <summary>
/// Resolves bounded intent phrases into schema-conformant proposal candidates.
/// Candidates are validated and lowered by non-LLM code; proposals are never proof of correctness.
/// </summary>
public interface ISemanticResolver
{
    Task<SemanticResolutionResult> ResolveAsync(
        SemanticResolutionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record SymbolInfo(string Name, string TypeDescription);

public sealed record RecordFieldInfo(string Name, string TypeDescription);

public sealed record RecordSchemaInfo(string Name, IReadOnlyList<RecordFieldInfo> Fields);

public sealed record SemanticResolutionRequest(
    string IntentText,
    string? ExpectedTypeDescription,
    IReadOnlyList<SymbolInfo> PermissibleSymbols,
    IReadOnlyList<RecordSchemaInfo> RecordSchemas,
    SourceSpan Span);

public sealed record SemanticResolutionResult(
    IReadOnlyList<IntentProposalCandidate> Candidates,
    string? FailureReason)
{
    public static SemanticResolutionResult None(string reason) =>
        new([], reason);

    public static SemanticResolutionResult Unique(IntentProposalCandidate candidate) =>
        new([candidate], null);

    public static SemanticResolutionResult Ambiguous(IReadOnlyList<IntentProposalCandidate> candidates) =>
        new(candidates, null);
}

/// <summary>
/// Default Milestone A/B stub that never resolves phrases.
/// </summary>
public sealed class StubSemanticResolver : ISemanticResolver
{
    public Task<SemanticResolutionResult> ResolveAsync(
        SemanticResolutionRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(SemanticResolutionResult.None(
            "No semantic resolver is configured for this compilation."));
}
