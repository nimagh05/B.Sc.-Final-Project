using System.Text.RegularExpressions;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Text;

namespace DocSearch.Application.Chunking;

public sealed partial class HeaderFooterCleaner
{
    private readonly ITextNormalizer _normalizer;

    public HeaderFooterCleaner(ITextNormalizer? normalizer = null)
    {
        _normalizer = normalizer ?? new PersianTextNormalizer();
    }

    [GeneratedRegex(@"\d+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DigitsRegex();

    [GeneratedRegex(@"^\s*[#\-–—\s]+\s*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NumericPagePattern();

    [GeneratedRegex(@"^\s*(صفحه|page)\s*#\s*$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PageWordPattern();

    public IReadOnlyList<PageText> Clean(IReadOnlyList<PageText> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);

        // For documents with < 4 pages: leave completely untouched per SPEC §3
        if (pages.Count < 4)
        {
            return pages;
        }

        var pageCandidateLines = new List<PageLineAnalysis>(pages.Count);
        var headerPatternCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var footerPatternCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var page in pages)
        {
            var analysis = AnalyzePageLines(page.Text);
            pageCandidateLines.Add(analysis);

            foreach (var headerPattern in analysis.FirstTwoNormalizedPatterns.Distinct(StringComparer.Ordinal))
            {
                headerPatternCounts[headerPattern] = headerPatternCounts.GetValueOrDefault(headerPattern) + 1;
            }

            foreach (var footerPattern in analysis.LastTwoNormalizedPatterns.Distinct(StringComparer.Ordinal))
            {
                footerPatternCounts[footerPattern] = footerPatternCounts.GetValueOrDefault(footerPattern) + 1;
            }
        }

        var threshold = (int)Math.Ceiling(pages.Count * 0.5);

        var repeatingHeaders = headerPatternCounts
            .Where(kvp => kvp.Value >= threshold)
            .Select(kvp => kvp.Key)
            .ToHashSet(StringComparer.Ordinal);

        var repeatingFooters = footerPatternCounts
            .Where(kvp => kvp.Value >= threshold)
            .Select(kvp => kvp.Key)
            .ToHashSet(StringComparer.Ordinal);

        var cleanedPages = new List<PageText>(pages.Count);

        for (var i = 0; i < pages.Count; i++)
        {
            var originalPage = pages[i];
            var analysis = pageCandidateLines[i];

            var keptLines = new List<string>();

            for (var lineIdx = 0; lineIdx < analysis.AllLines.Count; lineIdx++)
            {
                var rawLine = analysis.AllLines[lineIdx];
                var trimmed = rawLine.Trim();

                if (string.IsNullOrEmpty(trimmed))
                {
                    keptLines.Add(rawLine);
                    continue;
                }

                var isFirstTwo = analysis.FirstTwoLineIndices.Contains(lineIdx);
                var isLastTwo = analysis.LastTwoLineIndices.Contains(lineIdx);
                var normalizedPattern = NormalizeLinePattern(trimmed);

                // Check repeating header
                if (isFirstTwo && repeatingHeaders.Contains(normalizedPattern))
                {
                    continue;
                }

                // Check repeating footer
                if (isLastTwo && repeatingFooters.Contains(normalizedPattern))
                {
                    continue;
                }

                // Remove standalone page-number lines ONLY among first/last 2 lines, NEVER in the middle
                if ((isFirstTwo || isLastTwo) && IsStandalonePageNumber(normalizedPattern))
                {
                    continue;
                }

                keptLines.Add(rawLine);
            }

            var cleanedText = string.Join("\n", keptLines);
            cleanedPages.Add(new PageText(
                originalPage.PageNumber,
                cleanedText,
                originalPage.Quality,
                originalPage.NeedsOcr));
        }

        return cleanedPages;
    }

    private PageLineAnalysis AnalyzePageLines(string text)
    {
        var rawLines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var allLines = new List<string>(rawLines);

        var nonEmptyIndices = new List<int>();
        for (var i = 0; i < allLines.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(allLines[i]))
            {
                nonEmptyIndices.Add(i);
            }
        }

        var firstTwoIndices = nonEmptyIndices.Take(2).ToHashSet();
        var lastTwoIndices = nonEmptyIndices.TakeLast(2).ToHashSet();

        var firstTwoPatterns = new List<string>();
        foreach (var idx in firstTwoIndices)
        {
            firstTwoPatterns.Add(NormalizeLinePattern(allLines[idx].Trim()));
        }

        var lastTwoPatterns = new List<string>();
        foreach (var idx in lastTwoIndices)
        {
            lastTwoPatterns.Add(NormalizeLinePattern(allLines[idx].Trim()));
        }

        return new PageLineAnalysis(allLines, firstTwoIndices, lastTwoIndices, firstTwoPatterns, lastTwoPatterns);
    }

    private string NormalizeLinePattern(string line)
    {
        var normalized = _normalizer.NormalizeForSearch(line);
        return DigitsRegex().Replace(normalized, "#");
    }

    private static bool IsStandalonePageNumber(string normalizedWithHashes)
    {
        if (PageWordPattern().IsMatch(normalizedWithHashes))
        {
            return true;
        }

        if (NumericPagePattern().IsMatch(normalizedWithHashes))
        {
            // Must contain at least one digit placeholder '#' to be a page number line
            return normalizedWithHashes.Contains('#', StringComparison.Ordinal);
        }

        return false;
    }

    private sealed record PageLineAnalysis(
        IReadOnlyList<string> AllLines,
        HashSet<int> FirstTwoLineIndices,
        HashSet<int> LastTwoLineIndices,
        IReadOnlyList<string> FirstTwoNormalizedPatterns,
        IReadOnlyList<string> LastTwoNormalizedPatterns);
}
