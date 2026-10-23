using System.Globalization;
using System.Text.RegularExpressions;
using DocSearch.Application.Abstractions;

namespace DocSearch.Application.Chunking;

public static partial class TextDefectDetector
{
    private const int _minPageLengthForLigatureAnalysis = 300;
    private const int _minAlCountForLigatureAnalysis = 10;
    private const double _maxLaToAlRatioThreshold = 0.04;

    [GeneratedRegex(@"\b(?<d1>\d{4})/(?<d2>\d{1,2})/(?<d3>\d{1,2})\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DatePattern();

    public static ChunkDefectFlags DetectPageDefects(string pageText)
    {
        if (string.IsNullOrWhiteSpace(pageText))
        {
            return ChunkDefectFlags.None;
        }

        var flags = ChunkDefectFlags.None;

        if (HasCorruptLigatureLamAlef(pageText))
        {
            flags |= ChunkDefectFlags.CorruptLigatureLamAlef;
        }

        if (HasSuspectReversedDate(pageText))
        {
            flags |= ChunkDefectFlags.SuspectReversedDate;
        }

        return flags;
    }

    private static bool HasCorruptLigatureLamAlef(string text)
    {
        // Require a minimum page length to avoid false positives on sparse pages
        if (text.Length < _minPageLengthForLigatureAnalysis)
        {
            return false;
        }

        var laCount = CountOccurrences(text, "\u0644\u0627"); // لا (Lam + Alef)
        var alCount = CountOccurrences(text, "\u0627\u0644"); // ال (Alef + Lam)

        // If AL appears frequently but LA is absent or disproportionately suppressed
        if (alCount >= _minAlCountForLigatureAnalysis)
        {
            if (laCount == 0)
            {
                return true;
            }

            var ratio = (double)laCount / alCount;
            if (ratio < _maxLaToAlRatioThreshold)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSuspectReversedDate(string text)
    {
        // Normalize digits to ASCII for regex analysis
        var asciiText = NormalizeDigitsToAscii(text);
        var matches = DatePattern().Matches(asciiText);

        foreach (Match match in matches)
        {
            if (!match.Success) continue;

            var rawMatch = match.Value;
            var p1 = match.Groups["d1"].Value;
            var p2 = match.Groups["d2"].Value;
            var p3 = match.Groups["d3"].Value;

            var originalIsValid = IsValidSolarDate(p1, p2, p3);

            // Check full string reversal
            var charArray = rawMatch.ToCharArray();
            Array.Reverse(charArray);
            var reversedMatch = new string(charArray);

            var revParts = reversedMatch.Split('/');
            if (revParts.Length == 3)
            {
                // In reversedMatch, the 4-digit part may now be at revParts[2] (DD/MM/YYYY) or revParts[0]
                var reversedIsValid = IsValidSolarDate(revParts[2], revParts[1], revParts[0])
                                   || IsValidSolarDate(revParts[0], revParts[1], revParts[2]);

                if (!originalIsValid && reversedIsValid)
                {
                    return true;
                }
            }

            // Also check individual 4-digit year reversal (e.g., 8931 -> 1398)
            var p1RevChars = p1.ToCharArray();
            Array.Reverse(p1RevChars);
            var p1Rev = new string(p1RevChars);
            if (!originalIsValid && IsValidSolarDate(p1Rev, p2, p3))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsValidSolarDate(string yearStr, string monthStr, string dayStr)
    {
        if (int.TryParse(yearStr, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            && int.TryParse(monthStr, NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            && int.TryParse(dayStr, NumberStyles.None, CultureInfo.InvariantCulture, out var day))
        {
            // Valid Iranian solar calendar years: 1300 to 1499
            if (year is >= 1300 and <= 1499 && month is >= 1 and <= 12 && day is >= 1 and <= 31)
            {
                return true;
            }
        }

        return false;
    }

    private static int CountOccurrences(string source, string substring)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(substring, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += substring.Length;
        }
        return count;
    }

    private static string NormalizeDigitsToAscii(string text)
    {
        Span<char> buffer = stackalloc char[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            // Persian digits 0x06F0 - 0x06F9
            if (c is >= '\u06F0' and <= '\u06F9')
            {
                buffer[i] = (char)('0' + (c - '\u06F0'));
            }
            // Arabic-Indic digits 0x0660 - 0x0669
            else if (c is >= '\u0660' and <= '\u0669')
            {
                buffer[i] = (char)('0' + (c - '\u0660'));
            }
            else
            {
                buffer[i] = c;
            }
        }
        return new string(buffer);
    }
}
