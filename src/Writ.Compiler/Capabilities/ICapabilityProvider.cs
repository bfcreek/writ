namespace Writ.Compiler.Capabilities;

/// <summary>
/// Future provider seam (Milestone D). Compile-time loading uses <see cref="ICapabilityLoader"/>,
/// which must not invoke operations. Runtime calls go through the generated PowerShell script.
/// </summary>
public interface ICapabilityProvider
{
    string Alias { get; }
    Task<CapabilityManifest> DiscoverAsync(CancellationToken cancellationToken = default);
    Task<object?> InvokeAsync(
        string operation,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default);
}

public sealed record CapabilityManifest(
    string ProviderAlias,
    string VersionHash,
    IReadOnlyList<CapabilityOperation> Operations);

public sealed record CapabilityOperation(
    string Name,
    IReadOnlyList<CapabilityArgument> Arguments,
    string ResultTypeName,
    IReadOnlyList<string> Effects);

public sealed record CapabilityArgument(string Name, string TypeName, bool Required);

public sealed class StubCapabilityProvider : ICapabilityProvider
{
    public string Alias => "stub";

    public Task<CapabilityManifest> DiscoverAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new CapabilityManifest(Alias, "stub-0", []));

    public Task<object?> InvokeAsync(
        string operation,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Capability invocation is not implemented in Milestone A.");
}
