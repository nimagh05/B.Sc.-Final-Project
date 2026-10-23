using System.Globalization;
using DocSearch.Application.Chunking;
using DocSearch.Application.Options;
using Xunit;

namespace DocSearch.Application.Tests;

public sealed class HeuristicTokenCounterTests
{
    private readonly HeuristicTokenCounter _defaultCounter = new();

    [Fact]
    public void Count_WhenNullOrWhiteSpace_ReturnsZero()
    {
        Assert.Equal(0, _defaultCounter.Count(null!));
        Assert.Equal(0, _defaultCounter.Count(""));
        Assert.Equal(0, _defaultCounter.Count("   \n\t  "));
    }

    [Fact]
    public void Count_WhenPersianDominant_UsesPersianMultiplier()
    {
        // 10 words, 0 symbols: ceil(10 * 1.45) = 15
        var text = "یک دو سه چهار پنج شش هفت هشت نه ده";
        var count = _defaultCounter.Count(text);

        Assert.Equal(15, count);
    }

    [Fact]
    public void Count_WhenLatinDominant_UsesLatinMultiplier()
    {
        // 10 words, 0 symbols: ceil(10 * 2.25) = 23
        var text = "one two three four five six seven eight nine ten";
        var count = _defaultCounter.Count(text);

        Assert.Equal(23, count);
    }

    [Fact]
    public void Count_WhenSymbolsPresent_AddsSymbolTokens()
    {
        // 1 Persian word with 24 attached symbols:
        // words = 1 => ceil(1 * 1.45) = 2 tokens
        // symbols = 24 => 24 / 12 = 2 tokens
        // total = 2 + 2 = 4 tokens
        var text = "کلمه!@#$%^&*()_+1234567890:;";
        var count = _defaultCounter.Count(text);

        Assert.Equal(4, count);
    }

    [Fact]
    public void Count_ReadsMultipliersFromOptions_NotConstants()
    {
        var customOptions = new ChunkingOptions
        {
            PersianMultiplier = 2.0,
            LatinMultiplier = 3.0,
            SymbolsPerToken = 10
        };
        var counter = new HeuristicTokenCounter(Microsoft.Extensions.Options.Options.Create(customOptions));

        // 10 Persian words => ceil(10 * 2.0) = 20
        var persianText = "یک دو سه چهار پنج شش هفت هشت نه ده";
        Assert.Equal(20, counter.Count(persianText));

        // 10 Latin words => ceil(10 * 3.0) = 30
        var latinText = "one two three four five six seven eight nine ten";
        Assert.Equal(30, counter.Count(latinText));
    }

    [SkippableFact]
    public void Parity_With_TokenCalibrationCsv()
    {
        // Requirement 3: Add a [SkippableFact] parity test against eval/experiments/out/token_calibration.csv
        // (60 rows: C# estimate equals the Python estimate; coverage >= 90%).
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var repoRoot = FindRepoRoot(baseDir);
        Skip.If(repoRoot == null, "Could not find repository root.");

        var csvPath = Path.Combine(repoRoot!, "eval", "experiments", "out", "token_calibration.csv");
        Skip.IfNot(File.Exists(csvPath), $"Calibration CSV file not found at {csvPath}");

        var lines = File.ReadAllLines(csvPath);
        Assert.True(lines.Length >= 61, "Expected header + at least 60 calibration rows.");

        var coveredCount = 0;
        var rowCount = 0;

        // Skip header line
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var parts = line.Split(',');
            // Format: id,pdf,is_persian,persian_ratio,words,symbols,real_tokens,api_seconds
            Assert.True(parts.Length >= 7, $"Invalid CSV row format: {line}");

            var isPersian = bool.Parse(parts[2]);
            var words = int.Parse(parts[4], CultureInfo.InvariantCulture);
            var symbols = int.Parse(parts[5], CultureInfo.InvariantCulture);
            var realTokens = int.Parse(parts[6], CultureInfo.InvariantCulture);

            var csharpEstimate = _defaultCounter.Estimate(words, symbols, isPersian);

            // Python definition: np.ceil(words * m_val) + int(symbols / 12)
            var pythonMultiplier = isPersian ? 1.45 : 2.25;
            var expectedPythonEstimate = (int)Math.Ceiling(words * pythonMultiplier) + (symbols / 12);

            Assert.Equal(expectedPythonEstimate, csharpEstimate);

            if (csharpEstimate >= realTokens)
            {
                coveredCount++;
            }

            rowCount++;
        }

        Assert.Equal(60, rowCount);

        var coverage = (double)coveredCount / rowCount;
        Assert.True(coverage >= 0.90, $"Coverage {coverage:P1} fell below the required 90.0% threshold.");
        Assert.Equal(56, coveredCount); // Exactly 56/60 = 93.3%
    }

    private static string? FindRepoRoot(string startDir)
    {
        var current = new DirectoryInfo(startDir);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        return null;
    }
}
