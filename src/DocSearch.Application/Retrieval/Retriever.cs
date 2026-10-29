using DocSearch.Application.Abstractions;
using DocSearch.Application.Options;
using Microsoft.Extensions.Options;

namespace DocSearch.Application.Retrieval;

public sealed class Retriever(
    IQueryEmbedder queryEmbedder,
    IVectorStore vectorStore,
    IOptions<RagOptions> ragOptions,
    IOptions<AiOptions> aiOptions) : IRetriever
{
    public async Task<RetrievalResult> RetrieveAsync(
        string question,
        IReadOnlyCollection<Guid> collectionIds,
        int? topK,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        if (collectionIds == null || collectionIds.Count == 0)
        {
            return new RetrievalResult([], 0.0, HasFtsHit: false);
        }

        var effectiveTopK = topK.HasValue && topK.Value > 0
            ? topK.Value
            : ragOptions.Value.TopK;

        var candidateK = ragOptions.Value.CandidateK;
        var model = aiOptions.Value.Embedding.Model;

        var queryEmbedding = await queryEmbedder.EmbedQueryAsync(question, ct).ConfigureAwait(false);

        var searchRequest = new SearchRequest(
            QueryEmbedding: queryEmbedding,
            QueryText: question,
            CollectionIds: collectionIds,
            EmbeddingModel: model,
            CandidateK: candidateK,
            TopK: effectiveTopK,
            UseHybrid: false);

        var candidates = await vectorStore.SearchAsync(searchRequest, ct).ConfigureAwait(false);

        var bestVectorScore = candidates.Count > 0
            ? candidates.Max(c => c.VectorScore)
            : 0.0;

        var finalChunks = candidates.Take(effectiveTopK).ToList();

        return new RetrievalResult(finalChunks, bestVectorScore, HasFtsHit: false);
    }
}
