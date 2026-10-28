using System.ComponentModel.DataAnnotations;
using DocSearch.Domain.Constants;

namespace DocSearch.Application.Options;

public sealed class AiOptions : IValidatableObject
{
    public const string SectionName = "Ai";

    [Required]
    public AiEmbeddingOptions Embedding { get; set; } = new();

    [Required]
    public AiChatOptions Chat { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(Embedding, new ValidationContext(Embedding), results, true);
        Validator.TryValidateObject(Chat, new ValidationContext(Chat), results, true);
        return results;
    }
}

public sealed class AiEmbeddingOptions : IValidatableObject
{
    [Required]
    public string Provider { get; set; } = "Ollama";

    [Required]
    public string Model { get; set; } = "bge-m3";

    [Required]
    public string Endpoint { get; set; } = "http://127.0.0.1:11434";

    public string? ApiKey { get; set; }

    [Range(1, 16384)]
    public int Dimensions { get; set; } = EmbeddingConstants.VectorDimensions;

    public string QueryPrefix { get; set; } = "";

    public string PassagePrefix { get; set; } = "";

    [Range(1, 64)]
    public int BatchSize { get; set; } = 4;

    [Range(1, 64)]
    public int NumThread { get; set; } = 3;

    public string KeepAlive { get; set; } = "5m";

    public bool Truncate { get; set; }

    [Range(0, 10000)]
    public int PauseBetweenBatchesMs { get; set; }

    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 120;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Dimensions != EmbeddingConstants.VectorDimensions)
        {
            yield return new ValidationResult(
                $"Embedding Dimensions must equal {EmbeddingConstants.VectorDimensions} (configured: {Dimensions}).",
                [nameof(Dimensions)]);
        }

        if (Provider is not ("Ollama" or "OpenAICompatible" or "Fake"))
        {
            yield return new ValidationResult(
                $"Unknown embedding provider '{Provider}'. Allowed providers: Ollama, OpenAICompatible, Fake.",
                [nameof(Provider)]);
        }

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out _))
        {
            yield return new ValidationResult(
                $"Invalid embedding endpoint URI: '{Endpoint}'.",
                [nameof(Endpoint)]);
        }
    }
}

public sealed class AiChatOptions : IValidatableObject
{
    [Required]
    public string Provider { get; set; } = "OpenAICompatible";

    [Required]
    public string Model { get; set; } = "gemini-3.8-flash";

    [Required]
    public string Endpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta/openai/";

    public string? ApiKey { get; set; }

    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 60;

    [Range(0.0, 2.0)]
    public double Temperature { get; set; } = 0.1;

    [Range(1, 8192)]
    public int MaxOutputTokens { get; set; } = 800;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Provider is not ("OpenAICompatible" or "Ollama" or "Fake"))
        {
            yield return new ValidationResult(
                $"Unknown chat provider '{Provider}'. Allowed providers: OpenAICompatible, Ollama, Fake.",
                [nameof(Provider)]);
        }

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out _))
        {
            yield return new ValidationResult(
                $"Invalid chat endpoint URI: '{Endpoint}'.",
                [nameof(Endpoint)]);
        }
    }
}

public sealed class RagOptions : IValidatableObject
{
    public const string SectionName = "Rag";

    [Range(1, 20)]
    public int TopK { get; set; } = 6;

    [Range(1, 100)]
    public int CandidateK { get; set; } = 30;

    [Range(0.0, 1.0)]
    public double MinSimilarity { get; set; } = 0.55;

    [Range(100, 32000)]
    public int MaxContextTokens { get; set; } = 4000;

    public bool UseHybrid { get; set; } = true;

    [Range(1, 1000)]
    public int RrfK { get; set; } = 60;

    public bool ReturnSourcesWhenNotFound { get; set; }

    [Required]
    public string PromptVersion { get; set; } = "v1";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (CandidateK < TopK)
        {
            yield return new ValidationResult(
                $"Rag:CandidateK ({CandidateK}) must be greater than or equal to TopK ({TopK}).",
                [nameof(CandidateK)]);
        }
    }
}

public sealed class ChunkingOptions : IValidatableObject
{
    public const string SectionName = "Chunking";

    [Range(10, 4000)]
    public int TargetTokens { get; set; } = 425;

    [Range(50, 8000)]
    public int MaxTokens { get; set; } = 500;

    [Range(0, 2000)]
    public int MinTokens { get; set; } = 80;

    [Range(0.0, 0.5)]
    public double OverlapRatio { get; set; } = 0.15;

    public bool RespectLegalStructure { get; set; } = true;

    public bool IncludeSectionTitleInEmbedding { get; set; } = true;

    [Range(0.5, 5.0)]
    public double PersianMultiplier { get; set; } = 1.45;

    [Range(0.5, 5.0)]
    public double LatinMultiplier { get; set; } = 2.25;

    [Range(1, 100)]
    public int SymbolsPerToken { get; set; } = 12;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (TargetTokens > MaxTokens)
        {
            yield return new ValidationResult(
                $"Chunking:TargetTokens ({TargetTokens}) must be less than or equal to MaxTokens ({MaxTokens}).",
                [nameof(TargetTokens), nameof(MaxTokens)]);
        }

        if (MinTokens >= TargetTokens)
        {
            yield return new ValidationResult(
                $"Chunking:MinTokens ({MinTokens}) must be strictly less than TargetTokens ({TargetTokens}).",
                [nameof(MinTokens), nameof(TargetTokens)]);
        }
    }
}

public sealed class ExtractionOptions : IValidatableObject
{
    public const string SectionName = "Extraction";

    [Required]
    public string Provider { get; set; } = "Sidecar";

    [Required]
    public string ExtractorBaseUrl { get; set; } = "http://127.0.0.1:8000";

    public bool OcrEnabled { get; set; }

    [Range(72, 600)]
    public int OcrDpi { get; set; } = 250;

    [Range(0.0, 1.0)]
    public double MinPageQuality { get; set; } = 0.35;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Provider is not "Sidecar")
        {
            yield return new ValidationResult(
                $"Unknown extraction provider '{Provider}'. Allowed providers: Sidecar.",
                [nameof(Provider)]);
        }

        if (Provider == "Sidecar" && !Uri.TryCreate(ExtractorBaseUrl, UriKind.Absolute, out _))
        {
            yield return new ValidationResult(
                $"Invalid sidecar extractor URL: '{ExtractorBaseUrl}'.",
                [nameof(ExtractorBaseUrl)]);
        }
    }
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    [Required]
    public string RootPath { get; set; } = "./data/files";

    [Range(1, 500)]
    public int MaxUploadMb { get; set; } = 50;
}

public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    [Range(1, 32)]
    public int MaxConcurrentDocuments { get; set; } = 1;

    [Range(1, 10000)]
    public int QueueCapacity { get; set; } = 100;
}

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public bool AllowSelfRegistration { get; set; } = true;

    public BootstrapAdminOptions? BootstrapAdmin { get; set; }
}

public sealed class BootstrapAdminOptions
{
    public string? Email { get; set; }

    public string? Password { get; set; }
}

public sealed class JwtOptions : IValidatableObject
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; set; } = "docsearch";

    [Required]
    public string Audience { get; set; } = "docsearch";

    [Required]
    public string SigningKey { get; set; } = "";

    [Range(1, 43200)]
    public int ExpiryMinutes { get; set; } = 60;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(SigningKey) || SigningKey.Length < 32)
        {
            yield return new ValidationResult(
                "Jwt:SigningKey must be at least 32 characters long.",
                [nameof(SigningKey)]);
        }
    }
}

public sealed class CorsOptions
{
    public const string SectionName = "Cors";

    public string[] AllowedOrigins { get; set; } = ["http://localhost:5173"];
}

public sealed class DocsOptions
{
    public const string SectionName = "Docs";

    public bool Enabled { get; set; } = true;
}
