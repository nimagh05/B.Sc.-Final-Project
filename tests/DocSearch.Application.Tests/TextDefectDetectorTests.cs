using DocSearch.Application.Abstractions;
using DocSearch.Application.Chunking;
using Xunit;

namespace DocSearch.Application.Tests;

public sealed class TextDefectDetectorTests
{
    [Fact]
    public void DetectPageDefects_WhenCorruptLigaturePresent_FlagsCorruptLigatureLamAlef()
    {
        // Text >= 300 chars with 12 "ال" occurrences and 0 "لا"
        var defectiveText = new string(' ', 100) +
            "اطالعات مربوط به تحصیالت تکمیلی و تشکیالت دانشگاهی و ابالغی باال و اخالقی به صورت الگو " +
            "ال ال ال ال ال ال " +
            new string(' ', 100);

        var flags = TextDefectDetector.DetectPageDefects(defectiveText);

        Assert.True(flags.HasFlag(ChunkDefectFlags.CorruptLigatureLamAlef));
    }

    [Fact]
    public void DetectPageDefects_WhenCleanPersianText_DoesNotFlagLigature()
    {
        // Text with healthy balance of "لا" and "ال"
        var cleanText = new string(' ', 100) +
            "اطلاعات مربوط به تحصیلات تکمیلی و تشکیلات دانشگاهی و ابلاغی بالا و اخلاقی طبق آیین نامه لازم الاجرا است. " +
            "لا لا لا لا ال ال ال ال " +
            new string(' ', 100);

        var flags = TextDefectDetector.DetectPageDefects(cleanText);

        Assert.False(flags.HasFlag(ChunkDefectFlags.CorruptLigatureLamAlef));
    }

    [Fact]
    public void DetectPageDefects_WhenReversedDatePattern_FlagsSuspectReversedDate()
    {
        // 8931/05/27 reversed year is 1398
        var text = "تاریخ صدور نامه شماره ۱۲۳۴۵ مورخ 8931/05/27 دبیرخانه مرکزی";

        var flags = TextDefectDetector.DetectPageDefects(text);

        Assert.True(flags.HasFlag(ChunkDefectFlags.SuspectReversedDate));
    }

    [Fact]
    public void DetectPageDefects_WhenValidSolarDate_DoesNotFlagReversedDate()
    {
        // Valid date: 1398/05/27
        var text = "تاریخ صدور نامه شماره ۱۲۳۴۵ مورخ 1398/05/27 دبیرخانه مرکزی";

        var flags = TextDefectDetector.DetectPageDefects(text);

        Assert.False(flags.HasFlag(ChunkDefectFlags.SuspectReversedDate));
    }

    [SkippableFact]
    public void Parity_With_BenchmarkPdfs_Separation()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var repoRoot = FindRepoRoot(baseDir);
        Skip.If(repoRoot == null, "Could not find repository root.");

        var extractDir = Path.Combine(repoRoot!, "eval", "experiments", "out", "extract", "pymupdf");
        Skip.IfNot(Directory.Exists(extractDir), $"Extract directory not found at {extractDir}");

        // Defect documents must trigger on at least one substantive page
        var defectDocNames = new[] { "dastyar-pazhuheshi.pdf", "kod-akhlaghi.pdf", "bedoon-factor.pdf" };
        foreach (var docName in defectDocNames)
        {
            var docDir = Path.Combine(extractDir, docName);
            if (!Directory.Exists(docDir)) continue;

            var pageFiles = Directory.GetFiles(docDir, "*.txt");
            var anyFlagged = false;
            foreach (var pageFile in pageFiles)
            {
                var text = File.ReadAllText(pageFile);
                var flags = TextDefectDetector.DetectPageDefects(text);
                if (flags.HasFlag(ChunkDefectFlags.CorruptLigatureLamAlef))
                {
                    anyFlagged = true;
                    break;
                }
            }

            Assert.True(anyFlagged, $"Expected defect document '{docName}' to have at least one page flagged with CorruptLigatureLamAlef.");
        }

        // Clean Persian regulations must have ZERO pages flagged
        var cleanDocNames = new[] { "aein-91.pdf", "bein-almelali.pdf", "shoraha-pazhuheshi.pdf", "dastyar-amoozeshi.pdf" };
        foreach (var docName in cleanDocNames)
        {
            var docDir = Path.Combine(extractDir, docName);
            if (!Directory.Exists(docDir)) continue;

            var pageFiles = Directory.GetFiles(docDir, "*.txt");
            foreach (var pageFile in pageFiles)
            {
                var text = File.ReadAllText(pageFile);
                var flags = TextDefectDetector.DetectPageDefects(text);
                Assert.False(flags.HasFlag(ChunkDefectFlags.CorruptLigatureLamAlef),
                    $"Clean regulation '{docName}' page '{Path.GetFileName(pageFile)}' was unexpectedly flagged with CorruptLigatureLamAlef.");
            }
        }
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
