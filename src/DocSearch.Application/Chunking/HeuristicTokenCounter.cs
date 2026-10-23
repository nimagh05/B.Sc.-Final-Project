using DocSearch.Application.Abstractions;
using DocSearch.Application.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocSearch.Application.Chunking;

public sealed class HeuristicTokenCounter : ITokenCounter
{
    private readonly ChunkingOptions _options;

    public HeuristicTokenCounter(IOptions<ChunkingOptions> options)
    {
        _options = options?.Value ?? new ChunkingOptions();
    }

    public HeuristicTokenCounter()
        : this(Microsoft.Extensions.Options.Options.Create(new ChunkingOptions()))
    {
    }

    public int Count(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var (words, symbols, isPersian) = AnalyzeText(text);
        return Estimate(words, symbols, isPersian);
    }

    public int Estimate(int words, int symbols, bool isPersian)
    {
        if (words <= 0 && symbols <= 0)
        {
            return 0;
        }

        var multiplier = isPersian ? _options.PersianMultiplier : _options.LatinMultiplier;
        var divisor = Math.Max(1, _options.SymbolsPerToken);

        return (int)Math.Ceiling(words * multiplier) + (symbols / divisor);
    }

    public static (int Words, int Symbols, bool IsPersian) AnalyzeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (0, 0, false);
        }

        var words = 0;
        var inWord = false;
        var symbols = 0;
        var letters = 0;
        var persianLetters = 0;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                inWord = false;
            }
            else
            {
                if (!inWord)
                {
                    words++;
                    inWord = true;
                }

                if (char.IsLetter(c))
                {
                    letters++;
                    if (IsPersianOrArabicLetter(c))
                    {
                        persianLetters++;
                    }
                }
                else
                {
                    // Matches Python: c.isdigit() or (not c.isalnum() and not c.isspace())
                    symbols++;
                }
            }
        }

        var persianRatio = letters > 0 ? (double)persianLetters / letters : 0.0;
        var isPersian = persianRatio >= 0.5;

        return (words, symbols, isPersian);
    }

    private static bool IsPersianOrArabicLetter(char c)
    {
        var code = (int)c;
        return (code >= 0x0600 && code <= 0x06FF)
            || (code >= 0x0750 && code <= 0x077F)
            || (code >= 0xFB50 && code <= 0xFDFF)
            || (code >= 0xFE70 && code <= 0xFEFF);
    }
}
