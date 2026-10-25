using System.Globalization;
using System.Text.Json;
using Writ.Compiler.Diagnostics;

namespace Writ.Compiler.Semantics;

/// <summary>
/// Text written to standard error when <c>--debug</c> is set. One block per rule decision
/// and one block per model call: the sentence, the catalog that was offered, and the reply.
/// </summary>
internal static class ModelDebug
{
    private const int ClipAt = 8000;

    public static void WriteRule(TextWriter writer, string? filePath, SourceSpan span, string phrase, string outcome)
    {
        writer.WriteLine($"debug: rule at {Where(filePath, span)}");
        writer.WriteLine($"  text: {phrase}");
        writer.WriteLine($"  {outcome}");
    }

    public static void WriteCall(TextWriter writer, string? filePath, SourceSpan span, ModelRequest request)
    {
        writer.WriteLine($"debug: model read at {Where(filePath, span)}");
        writer.WriteLine($"  text: {request.Text}");
        WriteList(writer, "subjects", request.Subjects);
        WriteList(writer, "rules", request.Rules);
        WriteList(writer, "symbols", request.Symbols.Select(symbol => $"{symbol.Name}: {symbol.TypeDescription}"));
        WriteList(writer, "records", request.Records.Select(record =>
            record.Name + " {" + string.Join(", ", record.Fields.Select(field => $"{field.Name}: {field.TypeDescription}")) + "}"));
        WriteCatalog(writer, "types", request.Types);
        WriteCatalog(writer, "operations", request.Operations);
    }

    public static void WriteChatRequest(TextWriter writer, string url, string model, double temperature, string prompt)
    {
        writer.WriteLine(
            $"  chat: POST {url} model={model} temperature={temperature.ToString(CultureInfo.InvariantCulture)}");
        WriteBlock(writer, "chat prompt", prompt);
    }

    public static void WriteChatResponse(TextWriter writer, int status, string body, TimeSpan elapsed)
    {
        writer.WriteLine(
            $"  chat response: {status} in {elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)}ms");
        WriteBlock(writer, "chat body", body);
    }

    public static void WriteReading(TextWriter writer, ModelReading reading, TimeSpan elapsed)
    {
        if (reading.Selections.Count == 0)
        {
            var reason = reading.FailureReason is null ? "" : $" — {reading.FailureReason}";
            writer.WriteLine($"  reply: (no selection){reason}");
        }
        else
        {
            writer.WriteLine("  reply:");
            foreach (var selection in reading.Selections)
            {
                writer.WriteLine($"    {Describe(selection)}");
            }
        }

        writer.WriteLine($"  {elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)}ms");
    }

    public static void WriteOutcome(TextWriter writer, string outcome) =>
        writer.WriteLine($"  {outcome}");

    public static void WriteFailure(TextWriter writer, Exception exception, TimeSpan elapsed)
    {
        writer.WriteLine($"  failed: {exception.Message}");
        writer.WriteLine($"  {elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)}ms");
    }

    private static void WriteCatalog(TextWriter writer, string label, IReadOnlyList<CatalogEntry> entries)
    {
        if (entries.Count == 0)
        {
            writer.WriteLine($"  {label}: (none)");
            return;
        }

        writer.WriteLine($"  {label}: {entries.Count}");
        foreach (var entry in entries)
        {
            var args = entry.Arguments is { Count: > 0 } ? " (" + string.Join(", ", entry.Arguments) + ")" : "";
            writer.WriteLine($"    {entry.Id}{args} — {entry.Description}");
            if (entry.ArgumentAliases is not { Count: > 0 })
            {
                continue;
            }

            foreach (var pair in entry.ArgumentAliases.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                if (pair.Value.Count == 0)
                {
                    continue;
                }

                var kind = entry.SwitchArguments?.Contains(pair.Key, StringComparer.Ordinal) == true ? " switch" : "";
                writer.WriteLine($"      {pair.Key}{kind}: {string.Join(", ", pair.Value)}");
            }
        }
    }

    private static void WriteList(TextWriter writer, string label, IEnumerable<string> items)
    {
        var list = items.ToList();
        writer.WriteLine(list.Count == 0 ? $"  {label}: (none)" : $"  {label}: {string.Join("; ", list)}");
    }

    private static void WriteBlock(TextWriter writer, string label, string text)
    {
        writer.WriteLine($"  {label}:");
        foreach (var line in Clip(Pretty(text)).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            writer.WriteLine("    " + line);
        }
    }

    private static string Describe(ModelSelection selection)
    {
        var subject = string.IsNullOrEmpty(selection.Subject) ? "" : $" ({selection.Subject})";
        var bindings = selection.Bindings.Count == 0
            ? ""
            : " {" + string.Join(", ", selection.Bindings.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")) + "}";
        var rationale = string.IsNullOrEmpty(selection.Rationale) ? "" : $" — {selection.Rationale}";
        return $"{selection.Order}: {selection.Id}{subject}{bindings}{rationale}";
    }

    private static string Where(string? filePath, SourceSpan span)
    {
        var file = string.IsNullOrEmpty(filePath) ? "" : Path.GetFileName(filePath) + ":";
        return file + span;
    }

    private static string Pretty(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private static string Clip(string text) =>
        text.Length <= ClipAt ? text : text[..ClipAt] + $"… ({text.Length - ClipAt} more characters)";
}
