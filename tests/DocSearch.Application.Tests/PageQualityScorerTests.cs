using System.Globalization;
using System.Text;
using DocSearch.Application.Extraction;
using Xunit;

namespace DocSearch.Application.Tests;

public sealed class PageQualityScorerTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   \t\n\r   ")]
    [InlineData(null)]
    public void Score_EmptyOrWhitespaceText_ReturnsZeroQualityAndNeedsOcr(string? text)
    {
        var page = PageQualityScorer.ScorePage(1, text, 0.35);

        Assert.Equal(0, page.Quality);
        Assert.True(page.NeedsOcr);
    }

    [Fact]
    public void Score_ShortTextUnder20Chars_ReturnsZeroQualityAndNeedsOcr()
    {
        var shortText = "کمتر از بیست حرف"; // 16 characters
        var metrics = PageQualityScorer.ComputeMetrics(shortText, 0.35);

        Assert.True(metrics.Chars < 20);
        Assert.Equal(0.0, metrics.Quality);
        Assert.True(metrics.NeedsOcr);
    }

    [Fact]
    public void Score_GarbageSymbols_PenalizesLetterRatio()
    {
        // 40 characters: 5 letters and 35 symbols/digits -> letterRatio = 5/40 = 0.125 < 0.5
        var garbage = "1234567890!@#$%^&*()_+=-<>?,./;:abcde123";
        var metrics = PageQualityScorer.ComputeMetrics(garbage, 0.35);

        Assert.True(metrics.Chars >= 20);
        Assert.True(metrics.LetterRatio < 0.5);
        Assert.Equal(0.6, metrics.Quality); // 1.0 - 0.4
    }

    [Fact]
    public void Score_ReplacementCharacters_PenalizesQuality()
    {
        // 100 characters total, with 5 \uFFFD (5% > 1%)
        var text = new string('ا', 95) + new string('\uFFFD', 5);
        var metrics = PageQualityScorer.ComputeMetrics(text, 0.35);

        Assert.True(metrics.ReplacementRatio > 0.01);
        Assert.Equal(0.6, metrics.Quality); // 1.0 - 0.4
    }

    [Fact]
    public void Score_PresentationForms_PenalizesQuality()
    {
        // 100 characters total, with 30 Arabic presentation forms (U+FB50..U+FDFF / U+FE70..U+FEFF)
        // 30% > 20% threshold
        var text = new string('ا', 70) + new string('\uFE8D', 30);
        var metrics = PageQualityScorer.ComputeMetrics(text, 0.35);

        Assert.True(metrics.PresentationRatio > 0.2);
        Assert.Equal(0.8, metrics.Quality); // 1.0 - 0.2
    }

    [Fact]
    public void Score_ReversedPersian_DetectsReversedAndPenalizesQuality()
    {
        // Contains reversed keywords: "هک", "ار", "رد", "زا", "نیا", "اب", "تسا" repeated and the ADR sample
        var reversedSample = "ﯽﺳﺎﻨﺷرﺎﮐ يﺎﻫ هرود ﯽﺷزﻮﻣآ ﻪﻣﺎﻧ ﻦﯿﯾآ هک ار رد زا نیا اب تسا هک ار رد زا نیا اب تسا";
        var metrics = PageQualityScorer.ComputeMetrics(reversedSample, 0.35);

        Assert.True(metrics.LooksReversed, "Expected looksReversed to be true for backwards Persian text.");
        Assert.True(metrics.Quality <= 0.5, $"Expected quality <= 0.5, actual: {metrics.Quality}");
    }

    [Fact]
    public void Score_GoodPersianText_ReturnsHighQualityAndNoOcr()
    {
        var cleanPersian = "آیین‌نامه آموزشی دوره‌های کارشناسی دانشگاه صنعتی شریف مصوب شورای دانشگاه به منظور ارتقای کیفیت آموزش و تسهیل امور آموزشی دانشجویان تصویب گردید.";
        var page = PageQualityScorer.ScorePage(1, cleanPersian, 0.35);

        Assert.True(page.Quality >= 0.9, $"Expected quality >= 0.9, actual: {page.Quality}");
        Assert.False(page.NeedsOcr);
    }

    [Fact]
    public void Score_EnglishText_ReturnsHighQualityAndNoOcr()
    {
        var cleanEnglish = "We explore retrieval-augmented generation models that combine pre-trained parametric and non-parametric memory for knowledge-intensive NLP tasks.";
        var page = PageQualityScorer.ScorePage(1, cleanEnglish, 0.35);

        Assert.True(page.Quality >= 0.9, $"Expected quality >= 0.9, actual: {page.Quality}");
        Assert.False(page.NeedsOcr);
    }

    [Fact]
    public void T01_Parity_With_PyMuPDF_Extraction_Summary()
    {
        // Locate repo root by walking parent directories until global.json is found
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
        {
            Assert.Fail("Repository root containing 'global.json' could not be located.");
            return;
        }

        var csvPath = Path.Combine(dir.FullName, "eval", "experiments", "out", "extract_summary.csv");
        var pymupdfDir = Path.Combine(dir.FullName, "eval", "experiments", "out", "extract", "pymupdf");

        if (!File.Exists(csvPath) || !Directory.Exists(pymupdfDir))
        {
            Assert.Fail("Parity test skipped/failed: git-ignored 'eval/experiments/out/' directory is absent on this environment. Run T01 experiments to generate extraction outputs.");
            return;
        }

        var lines = File.ReadAllLines(csvPath, Encoding.UTF8);
        var matchedPages = 0;

        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split(',');
            var pdfName = parts[0];
            var lib = parts[1];
            if (lib != "pymupdf")
            {
                continue;
            }

            var pageNum = int.Parse(parts[2], CultureInfo.InvariantCulture);
            var expectedQuality = double.Parse(parts[9], CultureInfo.InvariantCulture);
            var expectedNeedsOcr = expectedQuality < 0.35;

            var txtPath = Path.Combine(pymupdfDir, pdfName, $"{pageNum}.txt");
            Assert.True(File.Exists(txtPath), $"Extracted text file missing: {txtPath}");

            var pageText = File.ReadAllText(txtPath, Encoding.UTF8);
            var scoredPage = PageQualityScorer.ScorePage(pageNum, pageText, 0.35);

            Assert.True(
                Math.Abs(scoredPage.Quality - expectedQuality) <= 0.01,
                $"Quality mismatch on {pdfName} page {pageNum}: expected {expectedQuality}, actual {scoredPage.Quality}");

            Assert.Equal(expectedNeedsOcr, scoredPage.NeedsOcr);
            matchedPages++;
        }

        Assert.Equal(130, matchedPages);
    }
}
