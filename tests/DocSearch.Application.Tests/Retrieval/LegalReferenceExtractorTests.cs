using DocSearch.Application.Retrieval;
using Xunit;

namespace DocSearch.Application.Tests.Retrieval;

public sealed class LegalReferenceExtractorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Extract_NullOrWhitespace_ReturnsEmpty(string? input)
    {
        var result = LegalReferenceExtractor.Extract(input);
        Assert.Empty(result.ArticleKeys);
        Assert.Null(result.NoteNumber);
    }

    [Fact]
    public void Extract_SingleArticle_ReturnsCanonicalKey()
    {
        var result = LegalReferenceExtractor.Extract("شرایط ثبت نام در ماده 12 آیین نامه");
        Assert.Single(result.ArticleKeys);
        Assert.Equal("ماده 12", result.ArticleKeys[0]);
        Assert.Null(result.NoteNumber);
    }

    [Fact]
    public void Extract_MokarrarArticle_ReturnsCanonicalMokarrar()
    {
        var result = LegalReferenceExtractor.Extract("بر اساس ماده 5 مکرر آیین نامه آموزشی");
        Assert.Single(result.ArticleKeys);
        Assert.Equal("ماده 5 مکرر", result.ArticleKeys[0]);
    }

    [Fact]
    public void Extract_MadehVahedeh_ReturnsCanonical()
    {
        var result = LegalReferenceExtractor.Extract("بر طبق ماده واحده مصوب شورای عالی");
        Assert.Single(result.ArticleKeys);
        Assert.Equal("ماده واحده", result.ArticleKeys[0]);
    }

    [Fact]
    public void Extract_EnglishArticle_ReturnsCanonical()
    {
        var result = LegalReferenceExtractor.Extract("according to Article 7 of the regulation");
        Assert.Single(result.ArticleKeys);
        Assert.Equal("article 7", result.ArticleKeys[0]);
    }

    [Fact]
    public void Extract_ArticleAndNote_ReturnsBoth()
    {
        var result = LegalReferenceExtractor.Extract("تبصره 1 ماده 7 آیین نامه آموزشی");
        Assert.Single(result.ArticleKeys);
        Assert.Equal("ماده 7", result.ArticleKeys[0]);
        Assert.Equal(1, result.NoteNumber);
    }

    [Fact]
    public void Extract_ParenthesizedArticleAndNote_ReturnsCanonical()
    {
        var result = LegalReferenceExtractor.Extract("تبصره (2) ماده (15) آیین نامه");
        Assert.Single(result.ArticleKeys);
        Assert.Equal("ماده 15", result.ArticleKeys[0]);
        Assert.Equal(2, result.NoteNumber);
    }

    [Fact]
    public void Extract_NoteAloneWithoutArticle_NeverBoosts_ReturnsEmpty()
    {
        // Golden rule: NEVER boost on a note alone when the query has no article reference
        var result = LegalReferenceExtractor.Extract("شرایط تبصره 1 چیست؟");
        Assert.Empty(result.ArticleKeys);
        Assert.Null(result.NoteNumber);
    }

    [Fact]
    public void Extract_MultipleArticles_PreservesDistinctInOrder()
    {
        var result = LegalReferenceExtractor.Extract("تفاوت ماده 5 و ماده 6 و ماده 5 چیست؟");
        Assert.Equal(2, result.ArticleKeys.Count);
        Assert.Equal("ماده 5", result.ArticleKeys[0]);
        Assert.Equal("ماده 6", result.ArticleKeys[1]);
    }
}
