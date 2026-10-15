using System.Text;
using System.Text.RegularExpressions;
using DocSearch.Application.Abstractions;

namespace DocSearch.Application.Text;

/// <summary>
/// Production-grade normalizer for Persian text handling Display, Embedding, and Search text layers
/// adhering strictly to SPEC-PERSIAN-AND-RAG.md §1.
/// Pure, stateless, and thread-safe.
/// </summary>
public sealed partial class PersianTextNormalizer : ITextNormalizer
{
    private const int _regexTimeoutMs = 1000;

    /// <summary>
    /// Layer 1: Display normalization (SPEC-PERSIAN-AND-RAG.md §1.1).
    /// Used for document chunk text and snippets shown to users. Minimal cleanup; preserves original digits and punctuation.
    /// Pipeline:
    /// 1. NFKC Unicode normalization.
    /// 2. Remove invisible/bidi controls (keep ZWNJ U+200C).
    /// 3. Normalize newlines (\r\n and \r -> \n) and Unicode space separators/tabs -> ASCII space.
    /// 4. Letters: Arabic Kaf/Yeh unification (ي/ى -> ی, ك -> ک). Preserve Teh Marbuta and Hamza forms.
    /// 5. Remove tatweel (U+0640).
    /// 6. ZWNJ cleanup: collapse runs, strip ZWNJ adjacent to whitespace/newlines or at line boundaries.
    /// 7. Collapse space/tab runs, trim each line, collapse 3+ newlines to 2, trim the whole text.
    /// </summary>
    public string NormalizeForDisplay(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // Step 1: Unicode NFKC
        var normalized = text.Normalize(NormalizationForm.FormKC);

        // Steps 2 - 5: Character-by-character pass
        var hasCombiningMarks = false;
        var removedChar = false;
        var sb = new StringBuilder(normalized.Length);
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];

            // Step 2: Remove invisible/bidi control characters (ZWNJ U+200C is explicitly kept)
            if (PersianUnicodeConstants.IsInvisibleOrBidiControl(c))
            {
                removedChar = true;
                continue;
            }

            // Step 3: Replace \r\n and \r with \n
            if (c == PersianUnicodeConstants.CarriageReturn)
            {
                if (i + 1 < normalized.Length && normalized[i + 1] == PersianUnicodeConstants.LineFeed)
                {
                    i++;
                }

                sb.Append(PersianUnicodeConstants.LineFeed);
                continue;
            }

            // Step 3 (cont): Replace Unicode space separators and tabs with ASCII space
            if (c == PersianUnicodeConstants.Tab || PersianUnicodeConstants.IsUnicodeSpaceSeparator(c))
            {
                sb.Append(PersianUnicodeConstants.Space);
                continue;
            }

            // Step 4: Letters: ي(U+064A) -> ی(U+06CC), ى(U+0649) -> ی, ك(U+0643) -> ک(U+06A9)
            // (ة and hamza forms are kept in display)
            if (c is PersianUnicodeConstants.ArabicYeh or PersianUnicodeConstants.ArabicAlefMaksura)
            {
                sb.Append(PersianUnicodeConstants.PersianYeh);
                continue;
            }

            if (c == PersianUnicodeConstants.ArabicKaf)
            {
                sb.Append(PersianUnicodeConstants.PersianKeheh);
                continue;
            }

            // Step 5: Remove tatweel U+0640
            if (c == PersianUnicodeConstants.Tatweel)
            {
                removedChar = true;
                continue;
            }

            if (PersianUnicodeConstants.IsArabicDiacritic(c))
            {
                hasCombiningMarks = true;
            }

            // Step 8: Digits, punctuation, Latin, and Persian word order kept as is
            sb.Append(c);
        }

        var result = (hasCombiningMarks && removedChar)
            ? sb.ToString().Normalize(NormalizationForm.FormKC)
            : sb.ToString();

        // Step 6: ZWNJ clean-up
        if (result.Contains(PersianUnicodeConstants.ZeroWidthNonJoiner))
        {
            result = ZwnjRunsRegex().Replace(result, PersianUnicodeConstants.ZeroWidthNonJoiner.ToString());
            result = ZwnjAdjacentToWhitespaceRegex().Replace(result, string.Empty);
        }

        // Step 7: Whitespace collapsing, line trimming, and newline collapsing
        result = MultipleSpacesRegex().Replace(result, PersianUnicodeConstants.Space.ToString());
        result = LineTrimRegex().Replace(result, string.Empty);
        result = ThreeOrMoreNewlinesRegex().Replace(result, $"{PersianUnicodeConstants.LineFeed}{PersianUnicodeConstants.LineFeed}");
        result = result.Trim();

        return result;
    }

    /// <summary>
    /// Layer 2: Embedding normalization (SPEC-PERSIAN-AND-RAG.md §1.2).
    /// Used for input to the embedding model for documents and queries.
    /// Pipeline: Display +
    /// 1. Remove Arabic diacritics U+064B–U+065F and U+0670.
    /// 2. Convert Persian (۰-۹) and Arabic-Indic (٠-٩) digits to ASCII 0-9;
    ///    Arabic decimal separator U+066B -> '.', thousands separator U+066C -> ','.
    /// 3. Map ة(U+0629) -> ه.
    /// 4. Join hyphenated Latin line breaks (word-\nnext -> wordnext) only for Latin letters on both sides.
    /// 5. Single newlines inside a paragraph -> space; keep blank lines as paragraph separators.
    /// </summary>
    public string NormalizeForEmbedding(string? text)
    {
        var display = NormalizeForDisplay(text);
        if (display.Length == 0)
        {
            return string.Empty;
        }

        // Step 4: Join hyphenated Latin line breaks (word-\nnext -> wordnext) only for Latin letters on both sides
        var unhyphenated = (display.Contains(PersianUnicodeConstants.HyphenMinus) || display.Contains(PersianUnicodeConstants.UnicodeHyphen))
            ? LatinHyphenNewlineRegex().Replace(display, string.Empty)
            : display;

        // Steps 1 - 3 & 5: Diacritics, Digits, Separators, Teh Marbuta, and Single Newlines
        var sb = new StringBuilder(unhyphenated.Length);
        for (var i = 0; i < unhyphenated.Length; i++)
        {
            var c = unhyphenated[i];

            // Step 1: Remove Arabic diacritics U+064B–U+065F and U+0670
            if (PersianUnicodeConstants.IsArabicDiacritic(c))
            {
                continue;
            }

            // Step 2: Persian and Arabic-Indic digits to ASCII 0-9
            if (PersianUnicodeConstants.IsPersianDigit(c))
            {
                sb.Append((char)(PersianUnicodeConstants.AsciiZero + (c - PersianUnicodeConstants.PersianDigitsStart)));
                continue;
            }

            if (PersianUnicodeConstants.IsArabicIndicDigit(c))
            {
                sb.Append((char)(PersianUnicodeConstants.AsciiZero + (c - PersianUnicodeConstants.ArabicIndicDigitsStart)));
                continue;
            }

            if (c == PersianUnicodeConstants.ArabicDecimalSeparator)
            {
                sb.Append(PersianUnicodeConstants.AsciiDot);
                continue;
            }

            if (c == PersianUnicodeConstants.ArabicThousandsSeparator)
            {
                sb.Append(PersianUnicodeConstants.AsciiComma);
                continue;
            }

            // Step 3: Map ة(U+0629) -> ه
            if (c == PersianUnicodeConstants.ArabicTehMarbuta)
            {
                sb.Append(PersianUnicodeConstants.ArabicHeh);
                continue;
            }

            // Step 5: Single newlines inside a paragraph -> space; keep blank lines as paragraph separators
            if (c == PersianUnicodeConstants.LineFeed
                && (i == 0 || unhyphenated[i - 1] != PersianUnicodeConstants.LineFeed)
                && (i + 1 >= unhyphenated.Length || unhyphenated[i + 1] != PersianUnicodeConstants.LineFeed))
            {
                sb.Append(PersianUnicodeConstants.Space);
                continue;
            }

            sb.Append(c);
        }

        var result = sb.ToString();

        // Re-collapse whitespace runs and trim
        if (result.Contains("  ", StringComparison.Ordinal))
        {
            result = MultipleSpacesRegex().Replace(result, PersianUnicodeConstants.Space.ToString());
        }

        result = result.Trim();

        return result;
    }

    /// <summary>
    /// Layer 3: Search normalization (SPEC-PERSIAN-AND-RAG.md §1.3).
    /// Used for full-text and trigram search queries and index text.
    /// Pipeline: Embedding +
    /// 1. ZWNJ -> space (so می‌شود -> می شود; tokens split consistently).
    /// 2. Punctuation: ، -> ,, ؛ -> ;, ؟ -> ?, «» -> ", … -> ..., ٪ -> %.
    /// 3. Lowercase Latin letters (ToLowerInvariant).
    /// 4. Collapse spaces again.
    /// </summary>
    public string NormalizeForSearch(string? text)
    {
        var embedding = NormalizeForEmbedding(text);
        if (embedding.Length == 0)
        {
            return string.Empty;
        }

        // Steps 1 - 3: ZWNJ -> space, punctuation mapping, Latin lowercasing
        var sb = new StringBuilder(embedding.Length);
        for (var i = 0; i < embedding.Length; i++)
        {
            var c = embedding[i];

            // Step 1: ZWNJ -> space
            if (c == PersianUnicodeConstants.ZeroWidthNonJoiner)
            {
                sb.Append(PersianUnicodeConstants.Space);
                continue;
            }

            // Step 2: Punctuation mapping
            if (c == PersianUnicodeConstants.ArabicComma)
            {
                sb.Append(PersianUnicodeConstants.AsciiComma);
                continue;
            }

            if (c == PersianUnicodeConstants.ArabicSemicolon)
            {
                sb.Append(PersianUnicodeConstants.AsciiSemicolon);
                continue;
            }

            if (c == PersianUnicodeConstants.ArabicQuestionMark)
            {
                sb.Append(PersianUnicodeConstants.AsciiQuestionMark);
                continue;
            }

            if (c is PersianUnicodeConstants.LeftPointingGuillemet or PersianUnicodeConstants.RightPointingGuillemet)
            {
                sb.Append(PersianUnicodeConstants.AsciiQuote);
                continue;
            }

            if (c == PersianUnicodeConstants.HorizontalEllipsis)
            {
                sb.Append(PersianUnicodeConstants.AsciiEllipsis);
                continue;
            }

            if (c == PersianUnicodeConstants.ArabicPercentSign)
            {
                sb.Append(PersianUnicodeConstants.AsciiPercent);
                continue;
            }

            // Step 3: Lowercase Latin letters
            sb.Append(char.ToLowerInvariant(c));
        }

        var result = sb.ToString();

        // Step 4: Collapse spaces again and trim
        if (result.Contains("  ", StringComparison.Ordinal))
        {
            result = MultipleSpacesRegex().Replace(result, PersianUnicodeConstants.Space.ToString());
        }

        result = result.Trim();

        return result;
    }

    [GeneratedRegex(@"\u200C{2,}", RegexOptions.None, matchTimeoutMilliseconds: _regexTimeoutMs)]
    private static partial Regex ZwnjRunsRegex();

    [GeneratedRegex(@"(?<=^|\s)\u200C+|\u200C+(?=\s|$)", RegexOptions.Multiline, matchTimeoutMilliseconds: _regexTimeoutMs)]
    private static partial Regex ZwnjAdjacentToWhitespaceRegex();

    [GeneratedRegex(@"[ \t]{2,}", RegexOptions.None, matchTimeoutMilliseconds: _regexTimeoutMs)]
    private static partial Regex MultipleSpacesRegex();

    [GeneratedRegex(@"^[ \t]+|[ \t]+$", RegexOptions.Multiline, matchTimeoutMilliseconds: _regexTimeoutMs)]
    private static partial Regex LineTrimRegex();

    [GeneratedRegex(@"\n{3,}", RegexOptions.None, matchTimeoutMilliseconds: _regexTimeoutMs)]
    private static partial Regex ThreeOrMoreNewlinesRegex();

    [GeneratedRegex(@"(?<=[A-Za-z])[-\u2010]\n(?=[A-Za-z])", RegexOptions.None, matchTimeoutMilliseconds: _regexTimeoutMs)]
    private static partial Regex LatinHyphenNewlineRegex();

    [GeneratedRegex(@"(?<!\n)\n(?!\n)", RegexOptions.None, matchTimeoutMilliseconds: _regexTimeoutMs)]
    private static partial Regex SingleNewlineInsideParagraphRegex();
}
