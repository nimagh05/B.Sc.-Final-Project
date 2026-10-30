using DocSearch.Application.Abstractions;
using DocSearch.Application.Chunking;
using DocSearch.Application.Options;
using Xunit;

namespace DocSearch.Application.Tests.Chunking;

public sealed class ChunkTextNormalizationTests
{
    private readonly LegalAwareChunker _chunker = new();
    private readonly ChunkingOptions _options = new()
    {
        TargetTokens = 100,
        MaxTokens = 200,
        MinTokens = 20,
        OverlapRatio = 0.1,
        RespectLegalStructure = true,
        IncludeSectionTitleInEmbedding = true
    };

    [Fact]
    public void Chunk_Text_IsDisplayNormalized_PurgingArabicPresentationForms()
    {
        // ﻣﺎﺩﻩ: U+FEE3 U+FE8E U+FEA9 U+FEE9 (Arabic Presentation Forms)
        // كــتاب يك: Arabic kaf, tatweel, Arabic yeh
        const string rawText = "\uFEE3\uFE8E\uFEA9\uFEE9 ۱۲\nكــتاب يك برای دانشجویان كشور است.";

        var pages = new List<PageText>
        {
            new(PageNumber: 1, Text: rawText, Quality: 1.0, NeedsOcr: false)
        };

        var chunks = _chunker.Chunk(pages, _options);

        Assert.NotEmpty(chunks);
        var chunk = chunks[0];

        // Ensure Arabic presentation forms are replaced with standard Persian letters
        Assert.DoesNotContain("\uFEE3", chunk.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\uFE8E", chunk.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\uFEA9", chunk.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\uFEE9", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("ماده", chunk.Text, StringComparison.Ordinal);

        // Ensure Arabic kaf/yeh and tatweel are normalized
        Assert.Contains("کتاب", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("یک", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("کشور", chunk.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ك", chunk.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ـ", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_SearchText_And_EmbeddingText_Differ_AccordingToSpecInvariants()
    {
        // Input contains:
        // - Arabic Presentation forms: ﻣﺎﺩﻩ (\uFEE3\uFE8E\uFEA9\uFEE9)
        // - Persian digits: ۱۲
        // - ZWNJ: می‌‌شود (U+200C)
        // - Persian punctuation: ،
        // - Latin uppercase: REGULATION
        const string rawText = "ﻣﺎﺩﻩ ۱۲: این قانون اجرا می‌شود، برای کلیه دانشجویان REGULATION لازم‌الاجرا است.";

        var pages = new List<PageText>
        {
            new(PageNumber: 1, Text: rawText, Quality: 1.0, NeedsOcr: false)
        };

        var chunks = _chunker.Chunk(pages, _options);

        Assert.NotEmpty(chunks);
        var chunk = chunks[0];

        // 1. Invariants for chunk.Text (Display Normalization):
        // - No Arabic presentation forms (U+FB50–U+FDFF, U+FE70–U+FEFF)
        // - Keeps Persian digits (۱۲)
        // - Keeps ZWNJ in می‌شود
        // - Keeps Persian comma (،)
        // - Keeps uppercase REGULATION
        Assert.DoesNotMatch(@"[\uFB50-\uFDFF\uFE70-\uFEFF]", chunk.Text);
        Assert.Contains("ماده", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("۱۲", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("می‌شود", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("،", chunk.Text, StringComparison.Ordinal);
        Assert.Contains("REGULATION", chunk.Text, StringComparison.Ordinal);

        // 2. Invariants for chunk.EmbeddingText (Embedding Normalization):
        // - Converted Persian digits to ASCII digits (12)
        // - Keeps ZWNJ in می‌شود
        // - Keeps uppercase REGULATION
        // - Prepends section title prefix
        Assert.Contains("12", chunk.EmbeddingText, StringComparison.Ordinal);
        Assert.DoesNotContain("۱۲", chunk.EmbeddingText, StringComparison.Ordinal);
        Assert.Contains("می‌شود", chunk.EmbeddingText, StringComparison.Ordinal);

        // 3. Invariants for chunk.SearchText (Search Normalization):
        // - Converted Persian digits to ASCII digits (12)
        // - Converted ZWNJ to space (می شود)
        // - Punctuation mapped: Persian comma ، -> ASCII comma ,
        // - Lowercase Latin: REGULATION -> regulation
        Assert.Contains("12", chunk.SearchText, StringComparison.Ordinal);
        Assert.Contains("می شود", chunk.SearchText, StringComparison.Ordinal);
        Assert.DoesNotContain("می‌شود", chunk.SearchText, StringComparison.Ordinal);
        Assert.Contains(",", chunk.SearchText, StringComparison.Ordinal);
        Assert.DoesNotContain("،", chunk.SearchText, StringComparison.Ordinal);
        Assert.Contains("regulation", chunk.SearchText, StringComparison.Ordinal);
        Assert.DoesNotContain("REGULATION", chunk.SearchText, StringComparison.Ordinal);

        // 4. Assert all three representations differ per spec
        Assert.NotEqual(chunk.Text, chunk.SearchText);
        Assert.NotEqual(chunk.Text, chunk.EmbeddingText);
        Assert.NotEqual(chunk.SearchText, chunk.EmbeddingText);
    }
}
