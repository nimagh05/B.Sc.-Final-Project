using System.Text.RegularExpressions;
using DocSearch.Application.Abstractions;

namespace DocSearch.Application.Extraction;

public sealed record PageQualityMetrics(
    int Chars,
    double LetterRatio,
    double PersianRatio,
    double PresentationRatio,
    double ReplacementRatio,
    bool LooksReversed,
    double Quality,
    bool NeedsOcr);

public sealed partial class PageQualityScorer
{
    private static readonly string[] _forwardWords = ["که", "را", "در", "از", "این", "با", "است"];
    private static readonly string[] _reversedWords = ["هک", "ار", "رد", "زا", "نیا", "اب", "تسا"];

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[^\p{L}\p{Nd}]")]
    private static partial Regex NonWordRegex();

    public static PageText ScorePage(int pageNumber, string? text, double minPageQuality = 0.35)
    {
        var metrics = ComputeMetrics(text, minPageQuality);
        return new PageText(pageNumber, text ?? string.Empty, metrics.Quality, metrics.NeedsOcr, metrics.LooksReversed);
    }

    public static PageQualityMetrics ComputeMetrics(string? text, double minPageQuality = 0.35)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new PageQualityMetrics(0, 0.0, 0.0, 0.0, 0.0, false, 0.0, true);
        }

        var chars = 0;
        var letters = 0;
        var presentation = 0;
        var replacements = 0;
        var persian = 0;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            chars++;

            if (char.IsLetter(c))
            {
                letters++;
                if (IsPersianOrArabic(c))
                {
                    persian++;
                }
            }

            if (IsPresentationForm(c))
            {
                presentation++;
            }

            if (c == '\uFFFD')
            {
                replacements++;
            }
        }

        var letterRatio = chars > 0 ? (double)letters / chars : 0.0;
        var persianRatio = letters > 0 ? (double)persian / letters : 0.0;
        var presentationRatio = chars > 0 ? (double)presentation / chars : 0.0;
        var replacementRatio = chars > 0 ? (double)replacements / chars : 0.0;

        var (forwardCount, reversedCount) = CountKeywords(text);
        var looksReversed = reversedCount > 1.5 * forwardCount && reversedCount >= 5;

        double quality;
        if (chars < 20)
        {
            quality = 0.0;
        }
        else
        {
            quality = 1.0;
            if (letterRatio < 0.5)
            {
                quality -= 0.4;
            }

            if (replacementRatio > 0.01)
            {
                quality -= 0.4;
            }

            if (presentationRatio > 0.2)
            {
                quality -= 0.2;
            }

            if (looksReversed)
            {
                quality -= 0.5;
            }

            quality = Math.Clamp(quality, 0.0, 1.0);
        }

        quality = Math.Round(quality, 4);
        var needsOcr = quality < minPageQuality;

        return new PageQualityMetrics(
            chars,
            Math.Round(letterRatio, 4),
            Math.Round(persianRatio, 4),
            Math.Round(presentationRatio, 4),
            Math.Round(replacementRatio, 4),
            looksReversed,
            quality,
            needsOcr);
    }

    private static bool IsPresentationForm(char c) =>
        (c >= 0xFB50 && c <= 0xFDFF) || (c >= 0xFE70 && c <= 0xFEFF);

    private static bool IsPersianOrArabic(char c) =>
        (c >= 0x0600 && c <= 0x06FF) ||
        (c >= 0x0750 && c <= 0x077F) ||
        (c >= 0xFB50 && c <= 0xFDFF) ||
        (c >= 0xFE70 && c <= 0xFEFF);

    private static (int Forward, int Reversed) CountKeywords(string text)
    {
        var forward = 0;
        var reversed = 0;

        var tokens = WhitespaceRegex().Split(text);
        foreach (var token in tokens)
        {
            var clean = NonWordRegex().Replace(token, "");
            if (string.IsNullOrEmpty(clean))
            {
                continue;
            }

            foreach (var fw in _forwardWords)
            {
                if (clean == fw)
                {
                    forward++;
                    break;
                }
            }

            foreach (var rw in _reversedWords)
            {
                if (clean == rw)
                {
                    reversed++;
                    break;
                }
            }
        }

        return (forward, reversed);
    }
}
