using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Chunking;
using DocSearch.Application.Options;
using DocSearch.Application.Text;
using Xunit;

namespace DocSearch.Application.Tests.Chunking;

public sealed class ChunkEmbeddingTextBuilderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reindex_Text_Matches_Ingestion_EmbeddingText(bool includeSectionTitleInEmbedding)
    {
        var normalizer = new PersianTextNormalizer();
        var chunker = new LegalAwareChunker(normalizer);
        var options = new ChunkingOptions
        {
            TargetTokens = 120,
            MaxTokens = 180,
            MinTokens = 30,
            OverlapRatio = 0.15,
            RespectLegalStructure = true,
            IncludeSectionTitleInEmbedding = includeSectionTitleInEmbedding
        };

        var pages = new List<PageText>
        {
            new(
                PageNumber: 1,
                Text: """
                      مقدمه بدون عنوان ماده است كه باید بدون سرتیتر بخش قطعه‌بندی شود و شامل ارقام ۱۲۳ و حروف عربي ي ك و نیم‌فاصله می‌گردد.

                      فصل اول: کلیات و تعاریف
                      ماده ۱: حداكثر تعداد واحد مجاز در هر نيم‌سال تحصيلي ۲۰ واحد است و دانشجوياني كه معدل الف كسب كنند می‌توانند تا ۲۴ واحد اخذ نمايند.
                      تبصره ۱: اين قانون شامل كليه دانشجويان كارشناسي پیوسته و ناپيوسته مي‌شود.
                      """,
                Quality: 1.0,
                NeedsOcr: false),
            new(
                PageNumber: 2,
                Text: """
                      ماده ۲: دانشجو موظف است در مهلت مقرر نسبت به ثبت‌نام اقدام نماید.
                      در صورت تأخیر غیرموجه، مرخصی تحصیلی برای وی ثبت خواهد شد و این موضوع در کارنامه درج می‌گردد.
                      """,
                Quality: 1.0,
                NeedsOcr: false)
        };

        var drafts = chunker.Chunk(pages, options);

        Assert.True(drafts.Count >= 2);
        Assert.Contains(drafts, d => string.IsNullOrWhiteSpace(d.SectionTitle));
        Assert.Contains(drafts, d => !string.IsNullOrWhiteSpace(d.SectionTitle));

        foreach (var draft in drafts)
        {
            var reconstructed = ChunkEmbeddingTextBuilder.Build(
                normalizer,
                options,
                draft.SectionTitle,
                draft.Text);

            Assert.Equal(draft.EmbeddingText, reconstructed);

            var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(draft.EmbeddingText)));
            var actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reconstructed)));
            Assert.Equal(expectedHash, actualHash);
        }
    }

    [SkippableFact]
    public void Reindex_Text_Matches_All_201_Benchmark_Chunks()
    {
        var repoRoot = FindRepoRoot(AppDomain.CurrentDomain.BaseDirectory);
        Skip.If(repoRoot == null, "Repository root not found.");

        var jsonPath = Path.Combine(repoRoot, "eval", "experiments", "out", "chunks_legal_t06b.json");
        Skip.IfNot(File.Exists(jsonPath), $"Benchmark chunk file not found at {jsonPath}");

        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath, Encoding.UTF8));
        var chunks = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(201, chunks.Count);

        var normalizer = new PersianTextNormalizer();

        foreach (var chunk in chunks)
        {
            var chunkId = chunk.GetProperty("chunk_id").GetString();
            var expectedEmbeddingText = chunk.GetProperty("text").GetString()!;
            var draftText = chunk.GetProperty("draft_text").GetString()!;
            var sectionTitle = chunk.TryGetProperty("section_title", out var sectionProp) &&
                               sectionProp.ValueKind == JsonValueKind.String
                ? sectionProp.GetString()
                : null;

            var reconstructed = ChunkEmbeddingTextBuilder.Build(
                normalizer,
                sectionTitle,
                draftText,
                includeSectionTitleInEmbedding: true);

            Assert.True(
                string.Equals(expectedEmbeddingText, reconstructed, StringComparison.Ordinal),
                $"Mismatch for benchmark chunk '{chunkId}' (SectionTitle='{sectionTitle}').");

            var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expectedEmbeddingText)));
            var actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reconstructed)));
            Assert.Equal(expectedHash, actualHash);
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
