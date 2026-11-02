using DocSearch.Application.Abstractions;
using DocSearch.Application.Chunking;
using DocSearch.Application.Options;
using DocSearch.Application.Prompts;
using Xunit;

namespace DocSearch.Application.Tests.Prompts;

public sealed class PromptBuilderTests
{
    private readonly PromptBuilder _builder;

    public PromptBuilderTests()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ChunkingOptions());
        var tokenCounter = new HeuristicTokenCounter(options);
        _builder = new PromptBuilder(tokenCounter);
    }

    [Fact]
    public void BuildPrompt_FormatsContextWithNumberingAndMetadata()
    {
        var chunks = new List<ScoredChunk>
        {
            new(
                ChunkId: Guid.NewGuid(),
                DocumentId: Guid.NewGuid(),
                FileName: "aeen-name.pdf",
                PageStart: 12,
                PageEnd: 12,
                SectionTitle: "ماده ۱۲",
                Text: "حداکثر واحد مجاز در ترم ۲۰ واحد است.",
                VectorScore: 0.85,
                FtsScore: null,
                FusedScore: 0.85),
            new(
                ChunkId: Guid.NewGuid(),
                DocumentId: Guid.NewGuid(),
                FileName: "tabsarah.pdf",
                PageStart: 3,
                PageEnd: 5,
                SectionTitle: null,
                Text: "دانشجویان ممتاز می‌توانند تا ۲۴ واحد اخذ نمایند.",
                VectorScore: 0.75,
                FtsScore: null,
                FusedScore: 0.75)
        };

        var result = _builder.BuildPrompt("تعداد واحد مجاز چند است؟", chunks, maxContextTokens: 4000);

        Assert.Contains("[1] (file: aeen-name.pdf, page: 12, section: ماده ۱۲)", result.UserPrompt);
        Assert.Contains("حداکثر واحد مجاز در ترم ۲۰ واحد است.", result.UserPrompt);

        Assert.Contains("[2] (file: tabsarah.pdf, page: 3-5, section: —)", result.UserPrompt);
        Assert.Contains("دانشجویان ممتاز می‌توانند تا ۲۴ واحد اخذ نمایند.", result.UserPrompt);

        Assert.Contains("QUESTION: تعداد واحد مجاز چند است؟", result.UserPrompt);
        Assert.Contains("You are an assistant that answers questions about institutional documents.", result.SystemPrompt);
        Assert.Equal(2, result.IncludedChunks.Count);
    }

    [Fact]
    public void BuildPrompt_RespectsContextTokenBudget_AndAlwaysIncludesAtLeastFirstChunk()
    {
        var chunks = new List<ScoredChunk>
        {
            new(
                ChunkId: Guid.NewGuid(),
                DocumentId: Guid.NewGuid(),
                FileName: "doc1.pdf",
                PageStart: 1,
                PageEnd: 1,
                SectionTitle: "Section 1",
                Text: "این یک متن طولانی است که چندین کلمه دارد تا حجم توکن آن محاسبه شود.",
                VectorScore: 0.9,
                FtsScore: null,
                FusedScore: 0.9),
            new(
                ChunkId: Guid.NewGuid(),
                DocumentId: Guid.NewGuid(),
                FileName: "doc2.pdf",
                PageStart: 2,
                PageEnd: 2,
                SectionTitle: "Section 2",
                Text: "بخش دوم متن که به علت بودجه ناکافی توکن نباید در خروجی گنجانده شود.",
                VectorScore: 0.8,
                FtsScore: null,
                FusedScore: 0.8)
        };

        // Budget of 1 token is smaller than chunk 1, but chunk 1 MUST still be included
        var result = _builder.BuildPrompt("سوال تست؟", chunks, maxContextTokens: 1);

        Assert.Single(result.IncludedChunks);
        Assert.Equal(chunks[0].ChunkId, result.IncludedChunks[0].ChunkId);
        Assert.Contains("[1]", result.UserPrompt);
        Assert.DoesNotContain("[2]", result.UserPrompt);
    }

    [Fact]
    public void BuildPrompt_ThrowsOnEmptyQuestion()
    {
        Assert.Throws<ArgumentException>(() => _builder.BuildPrompt("   ", [], maxContextTokens: 1000));
    }
}
