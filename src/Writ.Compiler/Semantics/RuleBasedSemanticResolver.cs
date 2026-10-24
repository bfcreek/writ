using System.Globalization;
using System.Text.RegularExpressions;

namespace Writ.Compiler.Semantics;

/// <summary>
/// Deterministic offline resolver for a small set of filter/aggregate phrases.
/// Stands in for an LLM while keeping the same request/response contract.
/// </summary>
public sealed partial class RuleBasedSemanticResolver : ISemanticResolver
{
    public Task<SemanticResolutionResult> ResolveAsync(
        SemanticResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        var text = Normalize(request.IntentText);
        var candidates = new List<IntentProposalCandidate>();

        // Deliberate ambiguity fixture: "active items" with competing definitions.
        if (text is "active items" or "active items in items" or "the active items")
        {
            if (TryFindCollection(request, "items", out var collection))
            {
                candidates.Add(new IntentProposalCandidate(
                    new FilterProposal(collection, "item",
                        new ProposalBinary(new ProposalField("isActive"), "==", new ProposalLiteral(true))),
                    Rationale: "Interpreted 'active' as item.isActive == true.",
                    Assumptions: ["active means isActive field"]));

                candidates.Add(new IntentProposalCandidate(
                    new FilterProposal(collection, "item",
                        new ProposalBinary(new ProposalField("status"), "==", new ProposalLiteral("active"))),
                    Rationale: "Interpreted 'active' as item.status == \"active\".",
                    Assumptions: ["active means status field equals active"]));

                return Task.FromResult(SemanticResolutionResult.Ambiguous(candidates));
            }
        }

        if (TryMatchSumProjection(text, request, out var sumCandidate))
        {
            candidates.Add(sumCandidate);
        }

        if (TryMatchFilter(text, request, out var filterCandidate))
        {
            candidates.Add(filterCandidate);
        }

        if (candidates.Count == 0)
        {
            return Task.FromResult(SemanticResolutionResult.None(
                $"No deterministic interpretation for intent phrase: \"{request.IntentText}\"."));
        }

        if (candidates.Count > 1)
        {
            return Task.FromResult(SemanticResolutionResult.Ambiguous(candidates));
        }

        return Task.FromResult(SemanticResolutionResult.Unique(candidates[0]));
    }

    private static bool TryMatchSumProjection(
        string text,
        SemanticResolutionRequest request,
        out IntentProposalCandidate candidate)
    {
        candidate = null!;
        // sum of <left> times|*<right> for each <iter> in <collection>
        var m = SumTimesRegex().Match(text);
        if (!m.Success)
        {
            m = SumProductRegex().Match(text);
        }

        if (!m.Success)
        {
            return false;
        }

        var leftField = m.Groups["left"].Value;
        var rightField = m.Groups["right"].Value;
        var iterator = m.Groups["iter"].Value;
        var collection = m.Groups["coll"].Value;
        if (!SymbolExists(request, collection))
        {
            return false;
        }

        var projection = new ProposalBinary(
            new ProposalField(leftField),
            "*",
            new ProposalField(rightField));

        candidate = new IntentProposalCandidate(
            new SumProjectionProposal(collection, iterator, projection),
            Rationale: $"Sum of {leftField} * {rightField} over {collection}.",
            Assumptions: ["times means multiplication", "fields refer to iterator record fields"]);
        return true;
    }

    private static bool TryMatchFilter(
        string text,
        SemanticResolutionRequest request,
        out IntentProposalCandidate candidate)
    {
        candidate = null!;
        // items where quantity > 0
        // filter items where quantity is greater than 0
        var m = FilterCompareRegex().Match(text);
        if (!m.Success)
        {
            m = FilterWordsRegex().Match(text);
        }

        if (!m.Success)
        {
            return false;
        }

        var collection = m.Groups["coll"].Value;
        if (!SymbolExists(request, collection))
        {
            return false;
        }

        var field = m.Groups["field"].Value;
        var op = NormalizeOp(m.Groups["op"].Value);
        var rawValue = m.Groups["value"].Value;
        object literal = ParseLiteral(rawValue);
        var iterator = GuessIterator(collection);

        candidate = new IntentProposalCandidate(
            new FilterProposal(
                collection,
                iterator,
                new ProposalBinary(new ProposalField(field), op, new ProposalLiteral(literal))),
            Rationale: $"Filter {collection} where {field} {op} {rawValue}.",
            Assumptions: ["where-clause field refers to element fields"]);
        return true;
    }

    private static bool TryFindCollection(SemanticResolutionRequest request, string preferred, out string name)
    {
        if (SymbolExists(request, preferred))
        {
            name = preferred;
            return true;
        }

        var list = request.PermissibleSymbols.FirstOrDefault(s =>
            s.TypeDescription.StartsWith("list<", StringComparison.Ordinal));
        if (list is not null)
        {
            name = list.Name;
            return true;
        }

        name = preferred;
        return false;
    }

    private static bool SymbolExists(SemanticResolutionRequest request, string name) =>
        request.PermissibleSymbols.Any(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    private static string GuessIterator(string collection) =>
        collection.EndsWith('s') && collection.Length > 1
            ? collection[..^1]
            : "item";

    private static string Normalize(string text) =>
        Regex.Replace(text.Trim().ToLowerInvariant(), @"\s+", " ");

    private static string NormalizeOp(string op) => op.ToLowerInvariant() switch
    {
        ">" or "greater than" or "above" => ">",
        ">=" or "at least" => ">=",
        "<" or "less than" or "below" => "<",
        "<=" or "at most" => "<=",
        "==" or "=" or "equals" or "equal to" or "is" => "==",
        "!=" or "not equal" => "!=",
        _ => op
    };

    private static object ParseLiteral(string raw)
    {
        if (raw.StartsWith('"') && raw.EndsWith('"'))
        {
            return raw[1..^1];
        }

        if (bool.TryParse(raw, out var b))
        {
            return b;
        }

        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            return l;
        }

        if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
        {
            return d;
        }

        return raw;
    }

    [GeneratedRegex(
        @"^sum of (?<left>\w+) times (?<right>\w+) for each (?<iter>\w+) in (?<coll>\w+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SumTimesRegex();

    [GeneratedRegex(
        @"^sum of (?<left>\w+) \* (?<right>\w+) for each (?<iter>\w+) in (?<coll>\w+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SumProductRegex();

    [GeneratedRegex(
        @"^(?:filter )?(?<coll>\w+) where (?<field>\w+) (?<op>>=|<=|!=|==|>|<) (?<value>-?\d+(?:\.\d+)?|true|false|""[^""]*""|\w+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex FilterCompareRegex();

    [GeneratedRegex(
        @"^(?:filter )?(?<coll>\w+) where (?<field>\w+) (?<op>is greater than|is less than|equals|is|greater than|less than) (?<value>-?\d+(?:\.\d+)?|true|false|""[^""]*""|\w+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex FilterWordsRegex();
}
