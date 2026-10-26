using Writ.Compiler.Capabilities;
using Writ.Compiler.Diagnostics;
using Writ.Compiler.Ir;
using Writ.Compiler.Syntax;

namespace Writ.Compiler.Compilation;

public sealed record CompilationResult(
    bool Success,
    IReadOnlyList<Diagnostic> Diagnostics,
    ProgramAst? Ast,
    IrModule? Ir,
    string? GeneratedPowerShell)
{
    /// <summary>C# source when the chosen emission is <c>csharp-library</c>.</summary>
    public string? GeneratedCSharp { get; init; }

    /// <summary>Capabilities discovered while binding (manifests and MCP servers), in declaration order.</summary>
    public IReadOnlyList<LoadedCapability> Capabilities { get; init; } = [];

    /// <summary>Effects granted by program-level "Allow …" sentences.</summary>
    public IReadOnlyCollection<EffectKind> GrantedEffects { get; init; } = [];

    /// <summary>Every template reading, in source order.</summary>
    public IReadOnlyList<LockedReading> Readings { get; init; } = [];

    /// <summary>The language, approach, and architecture chosen for this program.</summary>
    public EmissionDecision? Emission { get; init; }

    public static CompilationResult Failed(IReadOnlyList<Diagnostic> diagnostics) =>
        new(false, diagnostics, null, null, null);
}
