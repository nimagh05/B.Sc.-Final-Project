using System.Globalization;
using System.Text.RegularExpressions;

namespace DocSearch.Application.Retrieval;

/// <summary>
/// Result of extracting legal article keys and optional note numbers from query text.
/// </summary>
public sealed record LegalReferenceExtraction(
    IReadOnlyList<string> ArticleKeys,
    int? NoteNumber);

/// <summary>
/// Extracts canonical legal article keys and note numbers from embedding-normalized text
/// per SPEC §6.1 and SPEC §6.4. Pure, thread-safe, and stateless.
/// </summary>
public static partial class LegalReferenceExtractor
{
    // Article matching on embedding-normalized text (digits are ASCII 0-9)
    // Matches: "ماده 12 مکرر", "ماده 12", "ماده (12)", "ماده واحده", "article 5"
    [GeneratedRegex(@"(?:ماده\s*\(?\s*([0-9]+)\s*\)?(?:\s*مکرر)?|ماده\s+واحده|article\s+([0-9]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArticlePattern();

    // Note matching: "تبصره 1", "تبصره (1)", "تبصره 2"
    [GeneratedRegex(@"تبصره\s*\(?\s*([0-9]+)\s*\)?", RegexOptions.CultureInvariant)]
    private static partial Regex NotePattern();

    [GeneratedRegex(@"^ماده\s*\(?\s*([0-9]+)\s*\)?\s*مکرر$", RegexOptions.CultureInvariant)]
    private static partial Regex MokarrarRegex();

    [GeneratedRegex(@"^ماده\s*\(?\s*([0-9]+)\s*\)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SimpleArticleRegex();

    [GeneratedRegex(@"^article\s+([0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishArticleRegex();

    /// <summary>
    /// Extracts canonical article keys (e.g. "ماده 7", "ماده واحده", "ماده 5 مکرر", "article 3")
    /// and an optional note number (e.g. 1 from "تبصره 1") from query text.
    /// Input is expected to be embedding-normalized text (where Persian/Arabic digits are ASCII 0-9).
    /// </summary>
    public static LegalReferenceExtraction Extract(string? queryEmbeddingText)
    {
        if (string.IsNullOrWhiteSpace(queryEmbeddingText))
        {
            return new LegalReferenceExtraction([], null);
        }

        var articleMatches = ArticlePattern().Matches(queryEmbeddingText);
        var canonicalKeys = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in articleMatches)
        {
            var raw = match.Value.Trim();
            var canonical = CanonicalizeArticleKey(raw);
            if (!string.IsNullOrEmpty(canonical) && seen.Add(canonical))
            {
                canonicalKeys.Add(canonical);
            }
        }

        int? noteNumber = null;
        var noteMatch = NotePattern().Match(queryEmbeddingText);
        if (noteMatch.Success && int.TryParse(noteMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            noteNumber = n;
        }

        // Golden rule: NEVER boost on a note alone when the query has no article reference!
        // If ArticleKeys is empty, any extracted note is dropped so no standalone note boosting occurs.
        if (canonicalKeys.Count == 0)
        {
            return new LegalReferenceExtraction([], null);
        }

        return new LegalReferenceExtraction(canonicalKeys, noteNumber);
    }

    /// <summary>
    /// Standardizes a matched article string to canonical format with a single ASCII space:
    /// "ماده N", "ماده N مکرر", "ماده واحده", "article N".
    /// </summary>
    public static string? CanonicalizeArticleKey(string rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return null;
        }

        var trimmed = rawKey.Trim();

        if (trimmed.Equals("ماده واحده", StringComparison.Ordinal) ||
            Regex.IsMatch(trimmed, @"^ماده\s+واحده$", RegexOptions.CultureInvariant))
        {
            return "ماده واحده";
        }

        var mokarrarMatch = MokarrarRegex().Match(trimmed);
        if (mokarrarMatch.Success)
        {
            return $"ماده {mokarrarMatch.Groups[1].Value} مکرر";
        }

        var simpleMatch = SimpleArticleRegex().Match(trimmed);
        if (simpleMatch.Success)
        {
            return $"ماده {simpleMatch.Groups[1].Value}";
        }

        var englishMatch = EnglishArticleRegex().Match(trimmed);
        if (englishMatch.Success)
        {
            return $"article {englishMatch.Groups[1].Value}";
        }

        return null;
    }
}
