using System.Text.RegularExpressions;

namespace Writ.Compiler.Semantics;

/// <summary>
/// Deterministic stand-in for a model. It uses ordinary word knowledge, and only by
/// choosing entries from the catalog it was given. A specific rule wins over that knowledge.
/// Operation results are returned in execution order, so "but first" runs before the clause that precedes it.
/// </summary>
public sealed partial class InnateLanguageModel : ILanguageModel
{
    private static readonly Dictionary<string, string> TypesByWord = new(StringComparer.OrdinalIgnoreCase)
    {
        ["price"] = "decimal",
        ["amount"] = "decimal",
        ["cost"] = "decimal",
        ["rate"] = "decimal",
        ["tax"] = "decimal",
        ["fee"] = "decimal",
        ["total"] = "decimal",
        ["subtotal"] = "decimal",
        ["balance"] = "decimal",
        ["money"] = "decimal",
        ["quantity"] = "integer",
        ["count"] = "integer",
        ["qty"] = "integer",
        ["index"] = "integer",
        ["number"] = "integer",
        ["name"] = "string",
        ["title"] = "string",
        ["id"] = "string",
        ["message"] = "string",
        ["description"] = "string",
        ["day"] = "string",
        ["email"] = "string",
        ["note"] = "string",
        ["text"] = "string",
        ["code"] = "string",
        ["status"] = "string",
        ["active"] = "boolean",
        ["enabled"] = "boolean",
        ["succeeded"] = "boolean",
        ["flag"] = "boolean"
    };

    public Task<ModelReading> ReadAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Subjects.Count == 0 && request.Operations.Count > 0 &&
            TryReadCommandSequence(request, out var commands))
        {
            return Task.FromResult(commands);
        }

        var selections = new List<ModelSelection>();
        foreach (var subject in request.Subjects)
        {
            if (TryRuleType(request.Rules, subject, out var ruled))
            {
                if (request.Types.Any(type => type.Id == ruled))
                {
                    selections.Add(new ModelSelection(
                        ruled,
                        subject,
                        EmptyBindings,
                        $"A specific rule says {subject} is {ruled}."));
                }

                continue;
            }

            if (TryInnateType(subject, out var typeId) && request.Types.Any(type => type.Id == typeId))
            {
                selections.Add(new ModelSelection(
                    typeId,
                    subject,
                    EmptyBindings,
                    $"{subject} is {typeId} unless a rule says otherwise."));
            }
        }

        var phrase = StripArticle(Normalize(request.Text));
        if (phrase is "subtotal" && request.Operations.Any(operation => operation.Id == "sumProjection"))
        {
            if (HasRule(request.Rules, "subtotal"))
            {
                return Task.FromResult(ModelReading.None("A specific rule already defines subtotal."));
            }

            var collections = SubtotalCollections(request);
            if (collections.Count == 0)
            {
                return Task.FromResult(ModelReading.None(
                    "Subtotal would be the sum of price times quantity, but no list of those fields is in scope."));
            }

            foreach (var collection in collections)
            {
                var iterator = collection.EndsWith('s') && collection.Length > 1 ? collection[..^1] : "item";
                selections.Add(new ModelSelection(
                    "sumProjection",
                    "subtotal",
                    new Dictionary<string, string>
                    {
                        ["collection"] = collection,
                        ["iterator"] = iterator,
                        ["left"] = "price",
                        ["right"] = "quantity"
                    },
                    $"Subtotal is the sum of price times quantity for each {iterator} in {collection}."));
            }
        }

        if (selections.Count == 0)
        {
            return Task.FromResult(ModelReading.None(null));
        }

        return Task.FromResult(new ModelReading(selections, null));
    }

    private static bool TryReadCommandSequence(ModelRequest request, out ModelReading reading)
    {
        reading = ModelReading.None(null);
        var text = NormalizeFoibles(Normalize(request.Text));
        var ordered = TrySplitOrdered(text, out var clauses);
        if (!ordered)
        {
            clauses = [text];
        }

        var selections = new List<ModelSelection>();
        for (var order = 0; order < clauses.Count; order++)
        {
            var matches = MatchOperations(clauses[order], request);
            if (matches.Count == 0)
            {
                if (!ordered)
                {
                    return false;
                }

                reading = ModelReading.None($"Could not match \"{clauses[order]}\" to an allowed operation.");
                return true;
            }

            foreach (var match in matches)
            {
                selections.Add(new ModelSelection(
                    match.Id,
                    clauses[order],
                    BindArguments(match, clauses[order], request),
                    ordered ? $"Step {order + 1}: {match.Id}." : $"{match.Id} for \"{clauses[order]}\".",
                    order));
            }
        }

        reading = new ModelReading(selections, null);
        return true;
    }

    private static bool TrySplitOrdered(string text, out List<string> clauses)
    {
        clauses = [];
        var butFirst = ButFirstRegex().Match(text);
        if (butFirst.Success)
        {
            clauses.Add(butFirst.Groups["sooner"].Value.Trim());
            clauses.Add(butFirst.Groups["later"].Value.Trim());
            return true;
        }

        var after = AfterRegex().Match(text);
        if (after.Success)
        {
            clauses.Add(after.Groups["sooner"].Value.Trim());
            clauses.Add(after.Groups["later"].Value.Trim());
            return true;
        }

        var before = BeforeRegex().Match(text);
        if (before.Success)
        {
            clauses.Add(before.Groups["sooner"].Value.Trim());
            clauses.Add(before.Groups["later"].Value.Trim());
            return true;
        }

        if (text.Contains(" then ", StringComparison.Ordinal))
        {
            clauses.AddRange(text.Split(" then ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            return clauses.Count > 1;
        }

        return false;
    }

    private static List<CatalogEntry> MatchOperations(string clause, ModelRequest request)
    {
        var matches = new List<CatalogEntry>();
        var commandMatches = new List<(CatalogEntry Entry, int PhraseLength)>();
        foreach (var operation in request.Operations)
        {
            if (operation.Id.StartsWith("pwsh.", StringComparison.Ordinal))
            {
                var length = LongestPhrase(clause, operation);
                if (length > 0)
                {
                    commandMatches.Add((operation, length));
                }
            }
            else if (ClauseMentions(clause, operation))
            {
                matches.Add(operation);
            }
        }

        // "the contents of the file at the path" mentions both Get-ChildItem ("contents of")
        // and Get-Content ("contents of the file"); the more specific phrase wins.
        if (commandMatches.Count > 0)
        {
            var best = commandMatches.Max(m => m.PhraseLength);
            matches.AddRange(commandMatches.Where(m => m.PhraseLength == best).Select(m => m.Entry));
        }

        return matches;
    }

    private static int LongestPhrase(string clause, CatalogEntry operation)
    {
        var padded = " " + clause + " ";
        var best = 0;
        foreach (var raw in operation.Description.Split(" | ", StringSplitOptions.RemoveEmptyEntries))
        {
            var phrase = raw.Trim();
            if (padded.Contains(" " + phrase + " ", StringComparison.Ordinal)
                || padded.Contains(" " + phrase + ",", StringComparison.Ordinal))
            {
                best = Math.Max(best, phrase.Length);
            }
        }

        return best;
    }

    private static bool ClauseMentions(string clause, CatalogEntry operation)
    {
        if (operation.Id.StartsWith("pwsh.", StringComparison.Ordinal))
        {
            return LongestPhrase(clause, operation) > 0;
        }

        var name = operation.Id;
        var dot = name.LastIndexOf('.');
        if (dot >= 0)
        {
            name = name[(dot + 1)..];
        }

        var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "sumprojection" or "filter"))
        {
            return false;
        }

        var tokens = clause.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var stem = part.Length <= 4 ? part : part[..4];
            if (!tokens.Any(token => token.StartsWith(stem, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, string> BindArguments(CatalogEntry operation, string clause, ModelRequest request)
    {
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        var padded = " " + clause + " ";
        var used = new HashSet<string>(StringComparer.Ordinal);
        var quoted = QuotedRegex().Match(clause);
        foreach (var argument in operation.Arguments ?? [])
        {
            var names = new List<string> { argument };
            if (operation.ArgumentAliases is not null && operation.ArgumentAliases.TryGetValue(argument, out var aliases))
            {
                names.AddRange(aliases);
            }

            if (operation.SwitchArguments?.Contains(argument, StringComparer.Ordinal) == true)
            {
                if (names.Any(name => padded.Contains(" " + name.ToLowerInvariant() + " ", StringComparison.Ordinal)))
                {
                    bindings[argument] = "true";
                }

                continue;
            }

            var symbol = request.Symbols.FirstOrDefault(candidate =>
                !used.Contains(candidate.Name) &&
                padded.Contains(" " + candidate.Name.ToLowerInvariant() + " ", StringComparison.Ordinal) &&
                names.Any(name => Same(name, candidate.Name)));
            symbol ??= request.Symbols.FirstOrDefault(candidate =>
                !used.Contains(candidate.Name) &&
                names.Any(name => Same(name, candidate.Name)));
            if (symbol is not null)
            {
                bindings[argument] = symbol.Name;
                used.Add(symbol.Name);
                continue;
            }

            if (quoted.Success && !bindings.ContainsValue(quoted.Value))
            {
                bindings[argument] = quoted.Value;
                continue;
            }

            if (argument == operation.Arguments![0])
            {
                var mentioned = request.Symbols.FirstOrDefault(candidate =>
                    !used.Contains(candidate.Name) &&
                    padded.Contains(" " + candidate.Name.ToLowerInvariant() + " ", StringComparison.Ordinal));
                if (mentioned is not null)
                {
                    bindings[argument] = mentioned.Name;
                    used.Add(mentioned.Name);
                }
            }
        }

        return bindings;
    }

    private static bool Same(string alias, string symbol)
    {
        static string Fold(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var a = Fold(alias);
        var s = Fold(symbol);
        return a == s || (a.Length > 2 && s.EndsWith(a, StringComparison.Ordinal));
    }

    [GeneratedRegex("\"[^\"]*\"", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedRegex();

    private static string NormalizeFoibles(string text) =>
        text.Replace("gut first", "but first", StringComparison.Ordinal)
            .Replace("frist", "first", StringComparison.Ordinal);

    [GeneratedRegex(@"^(?<later>.+?)\s*,?\s*but first\s+(?<sooner>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ButFirstRegex();

    [GeneratedRegex(@"^(?<later>.+?)\s+after\s+(?<sooner>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex AfterRegex();

    [GeneratedRegex(@"^(?<sooner>.+?)\s+before\s+(?<later>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex BeforeRegex();

    private static List<string> SubtotalCollections(ModelRequest request)
    {
        var found = new List<string>();
        foreach (var symbol in request.Symbols)
        {
            if (!symbol.TypeDescription.StartsWith("list<", StringComparison.Ordinal) ||
                !symbol.TypeDescription.EndsWith('>'))
            {
                continue;
            }

            var recordName = symbol.TypeDescription["list<".Length..^1];
            var record = request.Records.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, recordName, StringComparison.Ordinal));
            if (record is null)
            {
                continue;
            }

            var fields = record.Fields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
            if (fields.Contains("price") && fields.Contains("quantity"))
            {
                found.Add(symbol.Name);
            }
        }

        return found;
    }

    private static bool TryInnateType(string subject, out string typeId)
    {
        var words = SplitWords(subject);
        var last = words.Count == 0 ? subject : words[^1];
        if (TypesByWord.TryGetValue(last, out typeId!))
        {
            return true;
        }

        if (words.Count > 0 && (words[0] is "is" or "has"))
        {
            typeId = "boolean";
            return true;
        }

        typeId = "";
        return false;
    }

    private static bool TryRuleType(IReadOnlyList<string> rules, string subject, out string typeId)
    {
        typeId = "";
        var prefix = subject.ToLowerInvariant() + " is ";
        foreach (var rule in rules)
        {
            var text = Normalize(rule);
            if (!text.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = text[prefix.Length..].Replace("a ", "", StringComparison.Ordinal).Replace("an ", "", StringComparison.Ordinal).Trim();
            if (rest is "integer" or "decimal" or "string" or "boolean")
            {
                typeId = rest;
                return true;
            }
        }

        return false;
    }

    private static bool HasRule(IReadOnlyList<string> rules, string subject)
    {
        var prefix = subject.ToLowerInvariant() + " is ";
        return rules.Any(rule => Normalize(rule).StartsWith(prefix, StringComparison.Ordinal));
    }

    private static List<string> SplitWords(string name)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in name)
        {
            if (char.IsUpper(ch) && current.Length > 0)
            {
                words.Add(current.ToString().ToLowerInvariant());
                current.Clear();
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString().ToLowerInvariant());
        }

        return words;
    }

    private static string StripArticle(string text) =>
        text.StartsWith("the ", StringComparison.Ordinal) ? text[4..] :
        text.StartsWith("a ", StringComparison.Ordinal) ? text[2..] :
        text.StartsWith("an ", StringComparison.Ordinal) ? text[3..] :
        text;

    private static string Normalize(string text) =>
        string.Join(' ', text.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static readonly IReadOnlyDictionary<string, string> EmptyBindings =
        new Dictionary<string, string>();
}
