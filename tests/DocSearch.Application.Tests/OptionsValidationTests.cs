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
    public void AiChat_Default_Model_Is_Gemini35FlashLite()
    {
        var options = new AiChatOptions();
        Assert.Equal("gemini-3.5-flash-lite", options.Model);
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
    public void ChunkingOptions_TargetTokens_Must_Be_LessThan_Or_Equal_To_MaxTokens()
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
    public void ChunkingOptions_TargetTokens_Equal_To_MaxTokens_Is_Valid()
    {
        var options = new ChunkingOptions
        {
            TargetTokens = 500,
            MaxTokens = 500
        };
        var results = ValidateModel(options);

        Assert.Empty(results);
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

    [Theory]
    [InlineData(71)]
    [InlineData(0)]
    [InlineData(-10)]
    public void ExtractionOptions_OcrDpi_Below_72_Fails_Validation(int dpi)
    {
        var options = new ExtractionOptions { OcrDpi = dpi };
        var results = ValidateModel(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ExtractionOptions.OcrDpi)));
    }

    [Theory]
    [InlineData(601)]
    [InlineData(1200)]
    public void ExtractionOptions_OcrDpi_Above_600_Fails_Validation(int dpi)
    {
        var options = new ExtractionOptions { OcrDpi = dpi };
        var results = ValidateModel(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ExtractionOptions.OcrDpi)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ExtractionOptions_Empty_Or_Whitespace_OcrLanguages_Fails_Validation(string? lang)
    {
        var options = new ExtractionOptions { OcrLanguages = lang! };
        var results = ValidateModel(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ExtractionOptions.OcrLanguages)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(51)]
    public void ExtractionOptions_OcrBatchSize_OutOfRange_Fails_Validation(int batchSize)
    {
        var options = new ExtractionOptions { OcrBatchSize = batchSize };
        var results = ValidateModel(options);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ExtractionOptions.OcrBatchSize)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(50)]
    public void ExtractionOptions_OcrBatchSize_ValidRange_Succeeds(int batchSize)
    {
        var options = new ExtractionOptions { OcrBatchSize = batchSize };
        var results = ValidateModel(options);

        Assert.DoesNotContain(results, r => r.MemberNames.Contains(nameof(ExtractionOptions.OcrBatchSize)));
    }

    [Fact]
    public void ExtractionOptions_OcrMode_Precedence_Over_OcrEnabled()
    {
        // Default: null OcrMode, OcrEnabled=false -> Off
        var defaultOpts = new ExtractionOptions();
        Assert.Null(defaultOpts.OcrMode);
        Assert.False(defaultOpts.OcrEnabled);
        Assert.Equal(OcrMode.Off, defaultOpts.EffectiveOcrMode);

        // Legacy compatibility: OcrMode null + OcrEnabled=true -> Defective
        var legacyOpts = new ExtractionOptions { OcrMode = null, OcrEnabled = true };
        Assert.Equal(OcrMode.Defective, legacyOpts.EffectiveOcrMode);

        // Explicit override: OcrMode=Off overrides OcrEnabled=true
        var overrideOpts = new ExtractionOptions { OcrMode = OcrMode.Off, OcrEnabled = true };
        Assert.Equal(OcrMode.Off, overrideOpts.EffectiveOcrMode);

        // Explicit Always with OcrEnabled=false
        var alwaysOpts = new ExtractionOptions { OcrMode = OcrMode.Always, OcrEnabled = false };
        Assert.Equal(OcrMode.Always, alwaysOpts.EffectiveOcrMode);

        // Explicit Defective with OcrEnabled=false
        var defectiveOpts = new ExtractionOptions { OcrMode = OcrMode.Defective, OcrEnabled = false };
        Assert.Equal(OcrMode.Defective, defectiveOpts.EffectiveOcrMode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short_key_under_32_chars")]
    public void JwtOptions_Empty_Or_Short_SigningKey_Fails_Validation(string key)
    {
        var options = new JwtOptions { SigningKey = key };
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
