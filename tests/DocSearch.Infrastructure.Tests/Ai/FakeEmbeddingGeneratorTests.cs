using DocSearch.Application.Options;
using DocSearch.Application.Text;
using DocSearch.Domain.Constants;
using DocSearch.Infrastructure.Ai.Fakes;
using Microsoft.Extensions.Options;
using Xunit;

namespace DocSearch.Infrastructure.Tests.Ai;

public sealed class FakeEmbeddingGeneratorTests : IDisposable
{
    private static readonly string[] _threeInputs = ["", "   ", "\t\n"];
    private readonly PersianTextNormalizer _normalizer = new();
    private readonly FakeEmbeddingGenerator _generator;

    public FakeEmbeddingGeneratorTests()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AiOptions
        {
            Embedding = new AiEmbeddingOptions
            {
                Provider = "Fake",
                Dimensions = EmbeddingConstants.VectorDimensions,
                Model = "fake-bge-m3"
            }
        });
        _generator = new FakeEmbeddingGenerator(_normalizer, options);
    }

    public void Dispose()
    {
        _generator.Dispose();
    }

    [Fact]
    public async Task FakeEmbedding_Is_Deterministic()
    {
        const string text = "دانشگاه تهران دانشکده فنی";
        var res1 = await _generator.GenerateAsync([text]);
        var res2 = await _generator.GenerateAsync([text]);

        Assert.Single(res1);
        Assert.Single(res2);
        Assert.Equal(EmbeddingConstants.VectorDimensions, res1[0].Vector.Length);
        Assert.Equal(res1[0].Vector.ToArray(), res2[0].Vector.ToArray());
    }

    [Fact]
    public async Task FakeEmbedding_Returns_Configured_Dimensions()
    {
        var res = await _generator.GenerateAsync(["تست ابعاد و بردار"]);

        Assert.Single(res);
        Assert.Equal(EmbeddingConstants.VectorDimensions, res[0].Vector.Length);
    }

    [Fact]
    public async Task FakeEmbedding_Related_Text_Scores_Higher()
    {
        const string query = "قوانین آموزشی دانشگاه تهران";
        const string related = "مقررات و قوانین آموزشی دانشگاه تهران برای دانشجویان";
        const string unrelated = "دستور پخت غذای سنتی با سبزیجات محلی";

        var res = await _generator.GenerateAsync([query, related, unrelated]);

        var qVec = res[0].Vector.ToArray();
        var relVec = res[1].Vector.ToArray();
        var unrelVec = res[2].Vector.ToArray();

        var relSim = CosineSimilarity(qVec, relVec);
        var unrelSim = CosineSimilarity(qVec, unrelVec);

        Assert.True(relSim > unrelSim, $"Expected related similarity ({relSim}) to be strictly greater than unrelated ({unrelSim})");
        Assert.True(relSim > 0.3, $"Expected related similarity ({relSim}) to be > 0.3");
    }

    [Fact]
    public async Task FakeEmbedding_Empty_Or_Whitespace_Returns_Zero_Vector_Without_NaN()
    {
        var res = await _generator.GenerateAsync(_threeInputs);

        Assert.Equal(3, res.Count);
        foreach (var emb in res)
        {
            Assert.Equal(EmbeddingConstants.VectorDimensions, emb.Vector.Length);
            foreach (var val in emb.Vector.ToArray())
            {
                Assert.False(float.IsNaN(val));
                Assert.Equal(0f, val);
            }
        }
    }

    [Fact]
    public async Task FakeEmbedding_L2_Normalized_To_Unit_Length_For_NonEmpty()
    {
        var res = await _generator.GenerateAsync(["نمونه متن برای بررسی اندازه بردار"]);

        Assert.Single(res);
        var vec = res[0].Vector.ToArray();
        double sumSq = 0.0;
        foreach (var v in vec)
        {
            sumSq += (double)v * v;
        }

        var norm = Math.Sqrt(sumSq);
        Assert.True(Math.Abs(norm - 1.0) < 1e-4, $"Expected norm 1.0, got {norm}");
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0.0;
        double aSq = 0.0;
        double bSq = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            aSq += (double)a[i] * a[i];
            bSq += (double)b[i] * b[i];
        }

        var denom = Math.Sqrt(aSq) * Math.Sqrt(bSq);
        return denom > 0.0 ? dot / denom : 0.0;
    }
}
