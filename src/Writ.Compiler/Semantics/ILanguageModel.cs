using Writ.Compiler.Diagnostics;

namespace Writ.Compiler.Semantics;

/// <summary>
/// Reads a piece of text against a bounded catalog. The model may return one or more
/// catalog entries that the text represents or implies. It may not invent new ones.
/// When the entries are operations, <see cref="ModelSelection.Order"/> is the order they run,
/// after foibles such as "but first" have been put right. A specific rule overrides innate understanding.
/// </summary>
public interface ILanguageModel
{
    Task<ModelReading> ReadAsync(ModelRequest request, CancellationToken cancellationToken = default);
}

public sealed record ModelRequest(
    string Text,
    IReadOnlyList<CatalogEntry> Types,
    IReadOnlyList<CatalogEntry> Operations,
    IReadOnlyList<string> Subjects,
    IReadOnlyList<string> Rules,
    IReadOnlyList<SymbolInfo> Symbols,
    IReadOnlyList<RecordSchemaInfo> Records);

public sealed record ModelSelection(
    string Id,
    string? Subject,
    IReadOnlyDictionary<string, string> Bindings,
    string Rationale,
    int Order = 0);

public sealed record ModelReading(IReadOnlyList<ModelSelection> Selections, string? FailureReason)
{
    public static ModelReading None(string? reason) => new([], reason);

    public static ModelReading Of(params ModelSelection[] selections) => new(selections, null);
}

public static class ModelReadings
{
    public static IReadOnlyList<ModelSelection>? Accept(
        ModelReading reading,
        ModelRequest request,
        DiagnosticBag diagnostics,
        string? filePath,
        SourceSpan span)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in request.Types)
        {
            allowed.Add(type.Id);
        }

        foreach (var operation in request.Operations)
        {
            allowed.Add(operation.Id);
        }

        foreach (var selection in reading.Selections)
        {
            if (!allowed.Contains(selection.Id))
            {
                diagnostics.Error("E2105",
                    $"The model returned '{selection.Id}', which is not in the bounded list of operations and types.",
                    filePath, span);
                return null;
            }
        }

        var competing = reading.Selections
            .GroupBy(s => $"{s.Subject ?? ""}#{s.Order}", StringComparer.Ordinal)
            .Where(group => group.Select(Signature).Distinct(StringComparer.Ordinal).Count() > 1)
            .ToList();
        if (competing.Count > 0)
        {
            var alternatives = string.Join(Environment.NewLine, reading.Selections.Select(s => $"  - {s.Rationale}"));
            diagnostics.Error("E2104",
                $"\"{request.Text}\" has competing interpretations.{Environment.NewLine}{alternatives}{Environment.NewLine}State the rule explicitly.",
                filePath, span);
            return null;
        }

        return reading.Selections
            .Select((selection, index) => (selection, index))
            .GroupBy(item => Signature(item.selection), StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.selection.Order)
            .ThenBy(item => item.index)
            .Select(item => item.selection)
            .ToList();
    }

    private static string Signature(ModelSelection selection)
    {
        var bindings = string.Join(",", selection.Bindings.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}"));
        return $"{selection.Order}|{selection.Subject}|{selection.Id}|{bindings}";
    }
}
