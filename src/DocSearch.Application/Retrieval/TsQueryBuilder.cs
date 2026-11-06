using System.Text;
using System.Text.RegularExpressions;

namespace DocSearch.Application.Retrieval;

/// <summary>
/// Builds sanitized PostgreSQL 'simple' dictionary OR tsquery strings and provides
/// tokenization helpers for token-coverage evaluation per SPEC §6.3 and SPEC §6.6.
/// Pure, thread-safe, and stateless.
/// </summary>
public static partial class TsQueryBuilder
{
    private static readonly HashSet<string> _persianStopwords =
    [
        "و", "در", "به", "از", "که", "این", "را", "با", "است", "برای", "آن", "یک",
        "یا", "تا", "هر", "بر", "شد", "می", "های", "ها", "هم", "همه", "نیز", "بود",
        "باید", "شود", "کرد", "چه", "چند", "چنده", "کدام", "کدوم", "کی"
    ];

    private static readonly HashSet<string> _englishStopwords =
    [
        "the", "a", "an", "of", "to", "in", "is", "are", "and", "or", "for", "on", "with", "by"
    ];

    private static readonly HashSet<string> _legalKeywords =
    [
        "ماده", "تبصره", "بند", "فصل", "جزء", "اصل", "article", "section", "clause", "chapter", "part"
    ];

    [GeneratedRegex(@"^([^\p{L}0-9]*)([\p{L}0-9]+)([^\p{L}0-9]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex AlphanumericTokenRegex();

    [GeneratedRegex(@"^(ماده|تبصره|بند|فصل|جزء|اصل|article|section|clause|chapter|part)([0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AttachedLegalKeywordRegex();

    /// <summary>
    /// Builds an OR tsquery string from search-normalized query text (NormalizeForSearch).
    /// Emits both separated and attached legal keyword+number tokens (e.g. 'ماده' | '7' | 'ماده7'),
    /// strips stopwords, preserves digits, sanitizes all tokens to [a-zA-Z0-9\p{L}], and joins with '|'.
    /// Returns null if no valid tokens remain.
    /// </summary>
    public static string? BuildTsQuery(string? searchNormalizedText)
    {
        if (string.IsNullOrWhiteSpace(searchNormalizedText))
        {
            return null;
        }

        var tokens = ExtractTokens(searchNormalizedText);
        if (tokens.Count == 0)
        {
            return null;
        }

        return string.Join(" | ", tokens);
    }

    /// <summary>
    /// Tokenizes document or query search text into discrete non-stopword tokens for token coverage
    /// intersection in C#, handling attached/separated legal keywords consistently.
    /// </summary>
    public static HashSet<string> TokenizeForCoverage(string? searchNormalizedText)
    {
        if (string.IsNullOrWhiteSpace(searchNormalizedText))
        {
            return [];
        }

        return [.. ExtractTokens(searchNormalizedText)];
    }

    /// <summary>
    /// Extracts distinct significant query tokens used for evaluating token coverage against candidates.
    /// </summary>
    public static HashSet<string> ExtractQueryTokens(string? searchNormalizedQuery)
    {
        return TokenizeForCoverage(searchNormalizedQuery);
    }

    private static List<string> ExtractTokens(string text)
    {
        var rawTokens = text.Split([' ', '\t', '\r', '\n', ',', '،', ';', '؛', '.', ':', '?', '؟', '!', '"', '\'', '«', '»', '(', ')', '[', ']', '{', '}', '/', '\\', '-', '–', '—', '_'],
            StringSplitOptions.RemoveEmptyEntries);

        var cleanedTokens = new List<string>(rawTokens.Length);

        for (var i = 0; i < rawTokens.Length; i++)
        {
            var raw = rawTokens[i];
            var sanitized = SanitizeToken(raw);
            if (string.IsNullOrEmpty(sanitized))
            {
                continue;
            }

            cleanedTokens.Add(sanitized);
        }

        var resultTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < cleanedTokens.Count; i++)
        {
            var current = cleanedTokens[i];

            // Check if current token itself is an attached legal keyword + number: e.g. "ماده7" or "article5"
            var attachedMatch = AttachedLegalKeywordRegex().Match(current);
            if (attachedMatch.Success)
            {
                var kw = attachedMatch.Groups[1].Value.ToLowerInvariant();
                var num = attachedMatch.Groups[2].Value;
                var attached = kw + num;

                if (IsValidToken(kw))
                {
                    resultTokens.Add(kw);
                }
                if (IsValidToken(num))
                {
                    resultTokens.Add(num);
                }
                if (IsValidToken(attached))
                {
                    resultTokens.Add(attached);
                }
                continue;
            }

            // Check if current token is a legal keyword followed by a numeric token: e.g. "ماده" followed by "7"
            if (_legalKeywords.Contains(current) && i + 1 < cleanedTokens.Count)
            {
                var next = cleanedTokens[i + 1];
                if (IsAllDigits(next))
                {
                    var attached = current.ToLowerInvariant() + next;
                    if (IsValidToken(current))
                    {
                        resultTokens.Add(current.ToLowerInvariant());
                    }
                    if (IsValidToken(next))
                    {
                        resultTokens.Add(next);
                    }
                    if (IsValidToken(attached))
                    {
                        resultTokens.Add(attached);
                    }
                    i++; // Consume next token
                    continue;
                }
            }

            if (IsValidToken(current))
            {
                resultTokens.Add(current.ToLowerInvariant());
            }
        }

        return [.. resultTokens];
    }

    private static string SanitizeToken(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsLetter(ch) || (ch >= '0' && ch <= '9'))
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    private static bool IsValidToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var lower = token.ToLowerInvariant();

        if (_persianStopwords.Contains(lower) || _englishStopwords.Contains(lower))
        {
            return false;
        }

        // Digits (0-9) are always kept even if length == 1 (article numbers matter per SPEC §6.3)
        if (IsAllDigits(token))
        {
            return true;
        }

        // Drop non-numeric tokens of length <= 1
        return token.Length > 1;
    }

    private static bool IsAllDigits(string token)
    {
        if (token.Length == 0) return false;
        foreach (var ch in token)
        {
            if (ch < '0' || ch > '9')
            {
                return false;
            }
        }
        return true;
    }
}
