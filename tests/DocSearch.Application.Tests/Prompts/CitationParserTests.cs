using DocSearch.Application.Abstractions;
using DocSearch.Application.Prompts;
using Xunit;

namespace DocSearch.Application.Tests.Prompts;

public sealed class CitationParserTests
{
    private readonly CitationParser _parser = new();

    private static List<ScoredChunk> CreateSampleChunks(int count)
    {
        var list = new List<ScoredChunk>();
        for (var i = 1; i <= count; i++)
        {
            list.Add(new ScoredChunk(
                ChunkId: Guid.NewGuid(),
                DocumentId: Guid.NewGuid(),
                FileName: $"doc_{i}.pdf",
                PageStart: i * 2,
                PageEnd: i * 2,
                SectionTitle: $"ماده {i}",
                Text: $"متن مستند شماره {i} برای آزمایش استخراج نقل قول و بریده متنی.",
                VectorScore: 0.8,
                FtsScore: null,
                FusedScore: 0.8));
        }
        return list;
    }

    [Fact]
    public void ParseAndValidate_SingleCitation_MapsCorrectly()
    {
        var chunks = CreateSampleChunks(3);
        var answer = "حداکثر ۲۰ واحد است [1].";

        var result = _parser.ParseAndValidate(answer, chunks);

        Assert.True(result.HasCitations);
        Assert.Equal("حداکثر ۲۰ واحد است [1].", result.CleanedAnswer);
        Assert.Single(result.Sources);
        Assert.Equal(1, result.Sources[0].Index);
        Assert.Equal("doc_1.pdf", result.Sources[0].FileName);
    }

    [Fact]
    public void ParseAndValidate_AdjacentAndCommaCitations_ParsedAndRenumbered()
    {
        var chunks = CreateSampleChunks(4);
        var answer = "طبق مقررات [1][2] و مصوبه دیگر [3, 4] است.";

        var result = _parser.ParseAndValidate(answer, chunks);

        Assert.True(result.HasCitations);
        Assert.Equal("طبق مقررات [1][2] و مصوبه دیگر [3, 4] است.", result.CleanedAnswer);
        Assert.Equal(4, result.Sources.Count);
        Assert.Equal([1, 2, 3, 4], result.Sources.Select(s => s.Index));
    }

    [Fact]
    public void ParseAndValidate_ArabicCommaCitation_ParsedCorrectly()
    {
        var chunks = CreateSampleChunks(3);
        var answer = "مطابق بند الف [1، 2] تصمیم‌گیری می‌شود.";

        var result = _parser.ParseAndValidate(answer, chunks);

        Assert.True(result.HasCitations);
        Assert.Equal("مطابق بند الف [1, 2] تصمیم‌گیری می‌شود.", result.CleanedAnswer);
        Assert.Equal(2, result.Sources.Count);
    }

    [Fact]
    public void ParseAndValidate_PersianDigitsInBrackets_AreNotValidCitations()
    {
        var chunks = CreateSampleChunks(3);
        var answer = "پاسخ بر اساس [۱] ارائه شده است.";

        var result = _parser.ParseAndValidate(answer, chunks);

        Assert.False(result.HasCitations);
        Assert.Empty(result.Sources);
        // [۱] should be stripped, leaving no dangling brackets
        Assert.DoesNotContain("[", result.CleanedAnswer);
        Assert.DoesNotContain("]", result.CleanedAnswer);
        Assert.Equal("پاسخ بر اساس ارائه شده است.", result.CleanedAnswer);
    }

    [Fact]
    public void ParseAndValidate_OutOfRangeCitation_IsStrippedWithoutDanglingBrackets()
    {
        var chunks = CreateSampleChunks(2); // Only 2 chunks available
        var answer = "بر اساس منبع معتبر [1, 99] و منبع ناموجود [100].";

        var result = _parser.ParseAndValidate(answer, chunks);

        Assert.True(result.HasCitations);
        Assert.Equal("بر اساس منبع معتبر [1] و منبع ناموجود.", result.CleanedAnswer);
        Assert.Single(result.Sources);
        Assert.Equal(1, result.Sources[0].Index);
        Assert.Equal("doc_1.pdf", result.Sources[0].FileName);
        Assert.DoesNotContain("99", result.CleanedAnswer);
        Assert.DoesNotContain("100", result.CleanedAnswer);
    }

    [Fact]
    public void ParseAndValidate_SubsetCitations_RenumberedSequentiallyToEnsureSourcesMapping()
    {
        // When LLM cites only chunk 2 out of 3, text must renumber to [1] and sources[0].Index == 1
        var chunks = CreateSampleChunks(3);
        var answer = "طبق بند دوم آیین‌نامه [2] تصمیم‌گیری شد.";

        var result = _parser.ParseAndValidate(answer, chunks);

        Assert.True(result.HasCitations);
        Assert.Equal("طبق بند دوم آیین‌نامه [1] تصمیم‌گیری شد.", result.CleanedAnswer);
        Assert.Single(result.Sources);
        Assert.Equal(1, result.Sources[0].Index);
        Assert.Equal("doc_2.pdf", result.Sources[0].FileName); // Points to chunk 2
    }

    [Fact]
    public void CreateSnippet_TruncatesAtWordBoundaryWithEllipsis()
    {
        var longText = string.Join(" ", Enumerable.Repeat("کلمه", 80)); // > 300 chars
        var snippet = CitationParser.CreateSnippet(longText, 300);

        Assert.True(snippet.Length <= 301); // 300 or less plus ellipsis
        Assert.EndsWith("…", snippet);
        Assert.DoesNotContain("کلم…", snippet); // Not cut mid-word
    }

    [Fact]
    public void ParseAndValidate_SetsSourceCitationScoreToVectorScore_NotFusedScore()
    {
        var parser = new CitationParser();
        var chunks = new List<ScoredChunk>
        {
            new(
                ChunkId: Guid.NewGuid(),
                DocumentId: Guid.NewGuid(),
                FileName: "test.pdf",
                PageStart: 1,
                PageEnd: 1,
                SectionTitle: "ماده 1",
                Text: "متن مستند",
                VectorScore: 0.785, // Cosine similarity
                FtsScore: 0.12,
                FusedScore: 0.032)  // RRF score ~1/(60+1)
        };

        var result = parser.ParseAndValidate("پاسخ تستی با ارجاع [1].", chunks);

        Assert.Single(result.Sources);
        // Citation score MUST be VectorScore (0.785), not FusedScore (0.032)
        Assert.Equal(0.785, result.Sources[0].Score);
    }
}
