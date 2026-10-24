namespace Writ.Compiler.Semantics;

/// <summary>
/// The closed set a model is allowed to name. A reading that mentions anything else is rejected.
/// </summary>
public static class LanguageCatalog
{
    public static IReadOnlyList<CatalogEntry> Types { get; } =
    [
        new("integer", "A whole number, such as a count or a quantity."),
        new("decimal", "A fractional number, such as money, a price, a rate, or a total."),
        new("string", "Text."),
        new("boolean", "True or false.")
    ];

    public static IReadOnlyList<CatalogEntry> Operations { get; } =
    [
        new("sumProjection", "The sum of one number times another for each element of a list."),
        new("filter", "The elements of a list that meet a comparison.")
    ];
}

public sealed record CatalogEntry(
    string Id,
    string Description,
    IReadOnlyList<string>? Arguments = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? ArgumentAliases = null,
    IReadOnlyList<string>? SwitchArguments = null);
