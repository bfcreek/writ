namespace Writ.Compiler.Semantics;

/// <summary>
/// Sparse word vectors over catalog phrases. Exact phrase containment is tried first.
/// This index is the fallback when a sentence reorders a phrase, or inflects one of its words,
/// and the phrase still has at least two content words. A near neighbor counts only when
/// every content word of that phrase is present. The longest such phrase wins. Two phrases
/// of that length, within a small score margin, are both returned so the caller can reject
/// the ambiguity. Adding a phrase to the catalog extends the index.
/// </summary>
internal static class TemplateIndex
{
    private const double WordMatch = 0.72;
    private const double Margin = 0.05;

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "of", "to", "in", "on", "for", "and", "or", "with", "from", "by", "at",
        "that", "this", "is", "are", "be", "it", "its"
    };

    public static List<CatalogEntry> Near(string clause, IReadOnlyList<CatalogEntry> operations)
    {
        var query = Words(clause);
        if (query.Count == 0)
        {
            return [];
        }

        var hits = new List<(CatalogEntry Entry, int Words, double Score)>();
        foreach (var operation in operations)
        {
            (int Words, double Score)? best = null;
            foreach (var phrase in Phrases(operation))
            {
                var template = Words(phrase);
                if (template.Count < 2 || !Covers(template, query, out var score))
                {
                    continue;
                }

                if (best is null || template.Count > best.Value.Words ||
                    (template.Count == best.Value.Words && score > best.Value.Score))
                {
                    best = (template.Count, score);
                }
            }

            if (best is not null)
            {
                hits.Add((operation, best.Value.Words, best.Value.Score));
            }
        }

        if (hits.Count == 0)
        {
            return [];
        }

        var longest = hits.Max(hit => hit.Words);
        var specific = hits.Where(hit => hit.Words == longest).ToList();
        var bestScore = specific.Max(hit => hit.Score);
        return specific
            .Where(hit => hit.Score >= bestScore - Margin)
            .Select(hit => hit.Entry)
            .ToList();
    }

    private static bool Covers(List<string> template, List<string> query, out double score)
    {
        double sum = 0;
        foreach (var word in template)
        {
            var best = 0.0;
            foreach (var queryWord in query)
            {
                best = Math.Max(best, Similarity(word, queryWord));
            }

            if (best < WordMatch)
            {
                score = 0;
                return false;
            }

            sum += best;
        }

        score = sum / template.Count;
        return true;
    }

    private static double Similarity(string left, string right)
    {
        if (left == right)
        {
            return 1;
        }

        if (Inflection(left, right))
        {
            return 0.92;
        }

        return Cosine(Trigrams(left), Trigrams(right));
    }

    /// <summary>file/files, process/processes. The extra ending has to be a short inflection.</summary>
    private static bool Inflection(string left, string right)
    {
        var shorter = left.Length <= right.Length ? left : right;
        var longer = left.Length <= right.Length ? right : left;
        if (shorter.Length < 4 || !longer.StartsWith(shorter, StringComparison.Ordinal))
        {
            return false;
        }

        var extra = longer[shorter.Length..];
        return extra is "s" or "es" or "ed" or "d" or "ing";
    }

    private static Dictionary<string, double> Trigrams(string word)
    {
        var padded = "#" + word + "#";
        var vector = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var i = 0; i <= padded.Length - 3; i++)
        {
            var gram = padded.Substring(i, 3);
            vector[gram] = vector.GetValueOrDefault(gram) + 1;
        }

        return vector;
    }

    private static double Cosine(Dictionary<string, double> left, Dictionary<string, double> right)
    {
        double dot = 0, leftNorm = 0, rightNorm = 0;
        foreach (var pair in left)
        {
            leftNorm += pair.Value * pair.Value;
            if (right.TryGetValue(pair.Key, out var other))
            {
                dot += pair.Value * other;
            }
        }

        foreach (var pair in right)
        {
            rightNorm += pair.Value * pair.Value;
        }

        if (leftNorm == 0 || rightNorm == 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }

    private static IEnumerable<string> Phrases(CatalogEntry operation) =>
        operation.Description.Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        void Flush()
        {
            if (current.Length == 0)
            {
                return;
            }

            var word = current.ToString();
            current.Clear();
            if (word.Length > 1 && !Stopwords.Contains(word))
            {
                words.Add(word);
            }
        }

        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetter(ch))
            {
                current.Append(ch);
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return words;
    }
}
