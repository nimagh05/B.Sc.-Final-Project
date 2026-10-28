using DocSearch.Application.Ai;
using DocSearch.Application.Options;
using DocSearch.Application.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Xunit;

namespace DocSearch.Application.Tests.Ai;

public sealed class QueryAndPassageEmbedderTests
{
    private static readonly float[] _sampleVector = [1f, 2f];
    private static readonly float[] _expectedVector0 = [0f];
    private static readonly float[] _expectedVector1 = [1f];
    private static readonly string[] _samplePassages = ["متن بند اول", "متن بند دوم"];
    private static readonly string[] _expectedPrefixedPassages = ["passage: متن بند اول", "passage: متن بند دوم"];

    private readonly PersianTextNormalizer _normalizer = new();

    [Fact]
    public async Task QueryEmbedder_Normalizes_And_Applies_Prefix()
    {
        var recordedInputs = new List<string>();
        var stubGenerator = new TestEmbeddingGenerator((inputs) =>
        {
            recordedInputs.AddRange(inputs);
            return [new Embedding<float>(_sampleVector)];
        });

        var options = Microsoft.Extensions.Options.Options.Create(new AiOptions
        {
            Embedding = new AiEmbeddingOptions
            {
                QueryPrefix = "query: "
            }
        });

        var embedder = new QueryEmbedder(stubGenerator, _normalizer, options);

        var result = await embedder.EmbedQueryAsync("ماده ۱۲ – تبصره ۲", CancellationToken.None);

        Assert.Equal(_sampleVector, result);
        Assert.Single(recordedInputs);
        Assert.Equal("query: ماده 12 – تبصره 2", recordedInputs[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task QueryEmbedder_Throws_On_Null_Or_Whitespace(string query)
    {
        var stubGenerator = new TestEmbeddingGenerator(_ => []);
        var options = Microsoft.Extensions.Options.Options.Create(new AiOptions());
        var embedder = new QueryEmbedder(stubGenerator, _normalizer, options);

        await Assert.ThrowsAsync<ArgumentException>(() => embedder.EmbedQueryAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task PassageEmbedder_Applies_Prefix_And_Returns_Vectors()
    {
        var recordedInputs = new List<string>();
        var stubGenerator = new TestEmbeddingGenerator((inputs) =>
        {
            recordedInputs.AddRange(inputs);
            return inputs.Select((_, idx) => new Embedding<float>(new float[] { idx })).ToList();
        });

        var options = Microsoft.Extensions.Options.Options.Create(new AiOptions
        {
            Embedding = new AiEmbeddingOptions
            {
                PassagePrefix = "passage: "
            }
        });

        var embedder = new PassageEmbedder(stubGenerator, options);

        var result = await embedder.EmbedPassagesAsync(_samplePassages, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(_expectedVector0, result[0]);
        Assert.Equal(_expectedVector1, result[1]);

        Assert.Equal(2, recordedInputs.Count);
        Assert.Equal(_expectedPrefixedPassages, recordedInputs);
    }

    [Fact]
    public async Task PassageEmbedder_Empty_List_Returns_Empty_Without_Calling_Generator()
    {
        var calls = 0;
        var stubGenerator = new TestEmbeddingGenerator(_ =>
        {
            calls++;
            return [];
        });

        var options = Microsoft.Extensions.Options.Options.Create(new AiOptions());
        var embedder = new PassageEmbedder(stubGenerator, options);

        var result = await embedder.EmbedPassagesAsync(Array.Empty<string>(), CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal(0, calls);
    }

    private sealed class TestEmbeddingGenerator(Func<IEnumerable<string>, IList<Embedding<float>>> generatorFunc) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var embeddings = generatorFunc(values);
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
