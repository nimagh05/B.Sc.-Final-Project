using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocSearch.Application.Abstractions;

namespace DocSearch.Application.Prompts;

public sealed partial class CitationParser : ICitationParser
{
    private const int _maxSnippetLength = 300;

    [GeneratedRegex(@"\[(.*?)\]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpaceRegex();

    public ParsedCitationsResult ParseAndValidate(
        string answer,
        IReadOnlyList<ScoredChunk> availableChunks)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return new ParsedCitationsResult(string.Empty, [], false);
        }

        ArgumentNullException.ThrowIfNull(availableChunks);

        var maxValidIndex = availableChunks.Count;

        // First pass: identify all valid cited chunk indices in order of appearance
        var citedIndexOrder = new List<int>();
        var citedIndexSet = new HashSet<int>();

        var bracketMatches = BracketRegex().Matches(answer);
        foreach (Match match in bracketMatches)
        {
            var content = match.Groups[1].Value;
            var parts = content.Split([',', '،'], StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                // Strictly parse ASCII digits only - Persian digits or non-digits fail
                if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    if (index >= 1 && index <= maxValidIndex)
                    {
                        if (citedIndexSet.Add(index))
                        {
                            citedIndexOrder.Add(index);
                        }
                    }
                }
            }
        }

        // Build mapping from original chunk index -> sequential 1-based index
        // e.g., if LLM cited [2], map 2 -> 1
        var renumberMap = new Dictionary<int, int>();
        for (var i = 0; i < citedIndexOrder.Count; i++)
        {
            renumberMap[citedIndexOrder[i]] = i + 1;
        }

        // Second pass: rewrite or strip brackets in the answer text
        var cleanedAnswer = BracketRegex().Replace(answer, match =>
        {
            var content = match.Groups[1].Value;
            var parts = content.Split([',', '،'], StringSplitOptions.RemoveEmptyEntries);
            var validRenumberedIndices = new List<int>();

            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    if (index >= 1 && index <= maxValidIndex && renumberMap.TryGetValue(index, out var renumbered))
                    {
                        validRenumberedIndices.Add(renumbered);
                    }
                }
            }

            if (validRenumberedIndices.Count == 0)
            {
                // Strip completely (no valid citations in this bracket)
                return string.Empty;
            }

            // Return renumbered bracket (e.g. "[1]" or "[1, 2]")
            return $"[{string.Join(", ", validRenumberedIndices)}]";
        });

        // Clean up any double spaces caused by bracket removal and dangling punctuation
        cleanedAnswer = CleanUpDanglingPunctuation(cleanedAnswer);

        // Build Sources list
        var sources = new List<SourceCitation>();
        for (var i = 0; i < citedIndexOrder.Count; i++)
        {
            var origIndex = citedIndexOrder[i];
            var newIndex = i + 1;
            var chunk = availableChunks[origIndex - 1];

            var snippet = CreateSnippet(chunk.Text, _maxSnippetLength);
            sources.Add(new SourceCitation(
                Index: newIndex,
                DocumentId: chunk.DocumentId,
                FileName: chunk.FileName,
                Page: chunk.PageStart,
                SectionTitle: chunk.SectionTitle,
                Snippet: snippet,
                Score: chunk.FusedScore));
        }

        var hasCitations = sources.Count > 0;
        return new ParsedCitationsResult(cleanedAnswer, sources, hasCitations);
    }

    public static string CreateSnippet(string text, int maxLength = _maxSnippetLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        if (trimmed.Length <= maxLength)
        {
            return trimmed;
        }

        var lastSpace = trimmed.LastIndexOf(' ', maxLength);
        if (lastSpace > 0)
        {
            return trimmed[..lastSpace].TrimEnd() + "…";
        }

        return trimmed[..maxLength].TrimEnd() + "…";
    }

    private static string CleanUpDanglingPunctuation(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // Clean up any empty brackets that might exist
        text = text.Replace("[]", "", StringComparison.Ordinal);
        text = text.Replace("[ ]", "", StringComparison.Ordinal);

        // Remove extra spaces before closing punctuation
        text = MultiSpaceRegex().Replace(text, " ");
        text = text.Replace(" .", ".", StringComparison.Ordinal);
        text = text.Replace(" ،", "،", StringComparison.Ordinal);
        text = text.Replace(" :", ":", StringComparison.Ordinal);

        return text.Trim();
    }
}
