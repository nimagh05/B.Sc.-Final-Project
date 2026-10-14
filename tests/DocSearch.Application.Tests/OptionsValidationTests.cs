using System.ComponentModel.DataAnnotations;
using DocSearch.Application.Options;
using Xunit;

namespace DocSearch.Application.Tests;

public sealed class OptionsValidationTests
{
    private static List<ValidationResult> ValidateModel(object model)
    {
        var validationResults = new List<ValidationResult>();
        var ctx = new ValidationContext(model, null, null);
        Validator.TryValidateObject(model, ctx, validationResults, validateAllProperties: true);
        return validationResults;
    }

    [Fact]
    public void Default_AiOptions_Are_Valid()
    {
        var options = new AiOptions();
        var results = ValidateModel(options);
        Assert.Empty(results);
    }

    [Fact]
    public void AiEmbedding_Dimensions_Must_Equal_1024()
    {
        var options = new AiEmbeddingOptions { Dimensions = 512 };
        var results = ValidateModel(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(AiEmbeddingOptions.Dimensions)));
    }

    [Fact]
    public void AiEmbedding_Invalid_Provider_Fails_Validation()
    {
        var options = new AiEmbeddingOptions { Provider = "InvalidProvider" };
        var results = ValidateModel(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(AiEmbeddingOptions.Provider)));
    }

    [Fact]
    public void AiEmbedding_Invalid_Endpoint_Fails_Validation()
    {
        var options = new AiEmbeddingOptions { Endpoint = "not-a-valid-uri" };
        var results = ValidateModel(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(AiEmbeddingOptions.Endpoint)));
    }

    [Fact]
    public void AiChat_Invalid_Provider_Fails_Validation()
    {
        var options = new AiChatOptions { Provider = "InvalidProvider" };
        var results = ValidateModel(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(AiChatOptions.Provider)));
    }

    [Fact]
    public void RagOptions_CandidateK_Must_Be_GreaterThan_Or_Equal_To_TopK()
    {
        var options = new RagOptions
        {
            TopK = 10,
            CandidateK = 5
        };
        var results = ValidateModel(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(RagOptions.CandidateK)));
    }

    [Fact]
    public void RagOptions_MinSimilarity_Outside_Range_Fails_Validation()
    {
        var options = new RagOptions { MinSimilarity = 1.5 };
        var results = ValidateModel(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(RagOptions.MinSimilarity)));
    }

    [Fact]
    public void ChunkingOptions_TargetTokens_Must_Be_LessThan_MaxTokens()
    {
        var options = new ChunkingOptions
        {
            TargetTokens = 800,
            MaxTokens = 700
        };
        var results = ValidateModel(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ChunkingOptions.TargetTokens)));
    }

    [Fact]
    public void ChunkingOptions_MinTokens_Must_Be_LessThan_TargetTokens()
    {
        var options = new ChunkingOptions
        {
            MinTokens = 600,
            TargetTokens = 500
        };
        var results = ValidateModel(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ChunkingOptions.MinTokens)));
    }

    [Fact]
    public void ChunkingOptions_OverlapRatio_Above_Half_Fails_Validation()
    {
        var options = new ChunkingOptions { OverlapRatio = 0.6 };
        var results = ValidateModel(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ChunkingOptions.OverlapRatio)));
    }

    [Fact]
    public void ExtractionOptions_Invalid_Provider_Fails_Validation()
    {
        var options = new ExtractionOptions { Provider = "Unsupported" };
        var results = ValidateModel(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ExtractionOptions.Provider)));
    }

    [Fact]
    public void ExtractionOptions_Sidecar_With_Invalid_Url_Fails_Validation()
    {
        var options = new ExtractionOptions
        {
            Provider = "Sidecar",
            ExtractorBaseUrl = "invalid-url"
        };
        var results = ValidateModel(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ExtractionOptions.ExtractorBaseUrl)));
    }

    [Fact]
    public void JwtOptions_SigningKey_Shorter_Than_32_Chars_Fails_Validation()
    {
        var options = new JwtOptions { SigningKey = "too_short_key" };
        var results = ValidateModel(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(JwtOptions.SigningKey)));
    }

    [Fact]
    public void JwtOptions_SigningKey_With_32_Chars_Succeeds()
    {
        var options = new JwtOptions { SigningKey = "12345678901234567890123456789012" };
        var results = ValidateModel(options);

        Assert.Empty(results);
    }
}
