namespace Writ.Compiler.Semantics;

/// <summary>Optional trace for a compilation. Readings are always the built-in template index.</summary>
public sealed record ModelOptions
{
    public static ModelOptions ProjectOnly { get; } = new();

    /// <summary>
    /// When set, each rule decision and each template reading is written here.
    /// The command line passes standard error for <c>--debug</c>.
    /// </summary>
    public TextWriter? Debug { get; init; }
}
