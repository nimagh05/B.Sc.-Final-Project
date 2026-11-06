using DocSearch.Application.Retrieval;
using Xunit;

namespace DocSearch.Application.Tests.Retrieval;

public sealed class TsQueryBuilderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildTsQuery_NullOrWhitespace_ReturnsNull(string? input)
    {
        var result = TsQueryBuilder.BuildTsQuery(input);
        Assert.Null(result);
    }

    [Fact]
    public void BuildTsQuery_FiltersPersianAndEnglishStopwords()
    {
        // "در", "به", "از", "the", "and" are stopwords
        var input = "در رابطه با ماده 5 به دانشگاه the and";
        var result = TsQueryBuilder.BuildTsQuery(input);

        Assert.NotNull(result);
        var tokens = result.Split([" | "], StringSplitOptions.None);
        Assert.DoesNotContain("در", tokens);
        Assert.DoesNotContain("به", tokens);
        Assert.DoesNotContain("با", tokens);
        Assert.DoesNotContain("the", tokens);
        Assert.DoesNotContain("and", tokens);
        Assert.Contains("رابطه", tokens);
        Assert.Contains("دانشگاه", tokens);
        Assert.Contains("ماده", tokens);
        Assert.Contains("5", tokens);
        Assert.Contains("ماده5", tokens);
    }

    [Fact]
    public void BuildTsQuery_AttachedAndSeparatedLegalTokens_EmitsBoth()
    {
        // Separated query: "ماده 7"
        var separated = TsQueryBuilder.BuildTsQuery("شرایط ماده 7 آیین نامه");
        Assert.NotNull(separated);
        var sepTokens = separated.Split([" | "], StringSplitOptions.None);
        Assert.Contains("ماده", sepTokens);
        Assert.Contains("7", sepTokens);
        Assert.Contains("ماده7", sepTokens);

        // Attached query: "ماده7"
        var attached = TsQueryBuilder.BuildTsQuery("شرایط ماده7 آیین نامه");
        Assert.NotNull(attached);
        var attTokens = attached.Split([" | "], StringSplitOptions.None);
        Assert.Contains("ماده", attTokens);
        Assert.Contains("7", attTokens);
        Assert.Contains("ماده7", attTokens);
    }

    [Fact]
    public void BuildTsQuery_NoteTokens_EmitsBothSeparatedAndAttached()
    {
        var query = TsQueryBuilder.BuildTsQuery("تبصره 1 ماده 12");
        Assert.NotNull(query);
        var tokens = query.Split([" | "], StringSplitOptions.None);
        Assert.Contains("تبصره", tokens);
        Assert.Contains("1", tokens);
        Assert.Contains("تبصره1", tokens);
        Assert.Contains("ماده", tokens);
        Assert.Contains("12", tokens);
        Assert.Contains("ماده12", tokens);
    }

    [Fact]
    public void BuildTsQuery_StripsDangerousTsQueryCharacters()
    {
        // Characters: ' & | : ! ( ) * \ " should never appear in output tokens
        var input = "دانشجو's & (نمره: 12 | 14) !test* \\ dangerous\"";
        var result = TsQueryBuilder.BuildTsQuery(input);

        Assert.NotNull(result);
        Assert.DoesNotContain("'", result);
        Assert.DoesNotContain("&", result);
        Assert.DoesNotContain(":", result);
        Assert.DoesNotContain("!", result);
        Assert.DoesNotContain("(", result);
        Assert.DoesNotContain(")", result);
        Assert.DoesNotContain("*", result);
        Assert.DoesNotContain("\\", result);
        Assert.DoesNotContain("\"", result);
    }

    [Fact]
    public void BuildTsQuery_PreservesDigitsAndDropsSingleLetters()
    {
        // Single letters like 'w', 'a', 'ب' dropped; single digits '7', '0' kept per SPEC §6.3
        var input = "ماده 7 بند الف w x 9";
        var result = TsQueryBuilder.BuildTsQuery(input);

        Assert.NotNull(result);
        var tokens = result.Split([" | "], StringSplitOptions.None);
        Assert.Contains("7", tokens);
        Assert.Contains("9", tokens);
        Assert.DoesNotContain("w", tokens);
        Assert.DoesNotContain("x", tokens);
    }

    [Fact]
    public void TokenizeForCoverage_IntersectsExactTokensWithoutSubstringErrors()
    {
        // Demonstrates refinement 2: "7" does NOT match "1397" or "27", "رد" does NOT match "دارد"
        var candidateSearchText = "دانشجو در سال 1397 تعداد 27 واحد دارد و مشروط می شود.";
        var candidateTokens = TsQueryBuilder.TokenizeForCoverage(candidateSearchText);

        Assert.DoesNotContain("7", candidateTokens);
        Assert.DoesNotContain("رد", candidateTokens);
        Assert.Contains("1397", candidateTokens);
        Assert.Contains("27", candidateTokens);
        Assert.Contains("واحد", candidateTokens);
        Assert.Contains("دارد", candidateTokens);
        Assert.Contains("مشروط", candidateTokens);

        var queryTokens = TsQueryBuilder.ExtractQueryTokens("ماده 7 شرایط رد");
        var matched = queryTokens.Intersect(candidateTokens).ToList();

        // Neither "7" nor "رد" match the candidate!
        Assert.DoesNotContain("7", matched);
        Assert.DoesNotContain("رد", matched);
    }
}
