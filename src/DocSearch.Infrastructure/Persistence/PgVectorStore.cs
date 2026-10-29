using System.Data;
using DocSearch.Application.Abstractions;
using DocSearch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Pgvector;

namespace DocSearch.Infrastructure.Persistence;

public sealed class PgVectorStore(AppDbContext dbContext) : IVectorStore
{
    public async Task AddChunksAsync(
        Guid documentId,
        Guid collectionId,
        IReadOnlyList<ChunkRecord> chunks,
        CancellationToken ct)
    {
        if (chunks == null || chunks.Count == 0)
        {
            return;
        }

        var entities = chunks.Select(chunk => new Chunk
        {
            Id = Guid.CreateVersion7(),
            DocumentId = documentId,
            CollectionId = collectionId,
            ChunkIndex = chunk.Draft.Index,
            PageStart = chunk.Draft.PageStart,
            PageEnd = chunk.Draft.PageEnd,
            SectionTitle = chunk.Draft.SectionTitle,
            Text = chunk.Draft.Text,
            SearchText = chunk.Draft.SearchText,
            TokenCount = chunk.Draft.TokenCount,
            Embedding = new Vector(chunk.Embedding),
            EmbeddingModel = chunk.EmbeddingModel
        }).ToList();

        IDbContextTransaction? localTx = null;
        if (dbContext.Database.CurrentTransaction == null)
        {
            localTx = await dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        }

        try
        {
            await dbContext.Chunks.AddRangeAsync(entities, ct).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

            if (localTx != null)
            {
                await localTx.CommitAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (localTx != null)
            {
                await localTx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct)
    {
        await dbContext.Chunks
            .Where(c => c.DocumentId == documentId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScoredChunk>> SearchAsync(SearchRequest request, CancellationToken ct)
    {
        if (request.CollectionIds == null || request.CollectionIds.Count == 0)
        {
            return [];
        }

        var limit = request.UseHybrid
            ? (request.CandidateK > 0 ? request.CandidateK : (request.TopK > 0 ? request.TopK : 30))
            : (request.TopK > 0 ? request.TopK : (request.CandidateK > 0 ? request.CandidateK : 6));

        if (limit <= 0)
        {
            return [];
        }

        var connection = dbContext.Database.GetDbConnection();

        IDbContextTransaction? localTx = null;
        if (dbContext.Database.CurrentTransaction == null)
        {
            localTx = await dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var activeDbTx = (dbContext.Database.CurrentTransaction ?? localTx)!.GetDbTransaction();

            await using (var setCmd = connection.CreateCommand())
            {
                setCmd.Transaction = activeDbTx;
                var efSearch = Math.Clamp(limit, 100, 1000);
                setCmd.CommandText = $"SET LOCAL hnsw.ef_search = {efSearch}; SET LOCAL hnsw.iterative_scan = strict_order;";
                await setCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using var queryCmd = connection.CreateCommand();
            queryCmd.Transaction = activeDbTx;
            queryCmd.CommandText = """
                SELECT c.id, c.document_id, d.file_name, c.page_start, c.page_end, c.section_title, c.text,
                       (1.0 - (c.embedding <=> @q)) AS vscore
                FROM chunks c
                JOIN documents d ON d.id = c.document_id
                WHERE c.collection_id = ANY(@collections)
                  AND c.embedding_model = @model
                  AND d.status = 'Ready'
                ORDER BY c.embedding <=> @q ASC, c.id ASC
                LIMIT @limit;
                """;

            queryCmd.Parameters.Add(new NpgsqlParameter("q", new Vector(request.QueryEmbedding)));
            queryCmd.Parameters.Add(new NpgsqlParameter("collections", request.CollectionIds.Distinct().ToArray()));
            queryCmd.Parameters.Add(new NpgsqlParameter("model", request.EmbeddingModel));
            queryCmd.Parameters.Add(new NpgsqlParameter("limit", limit));

            var results = new List<ScoredChunk>();
            await using (var reader = await queryCmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var chunkId = reader.GetGuid(0);
                    var documentId = reader.GetGuid(1);
                    var fileName = reader.GetString(2);
                    var pageStart = reader.GetInt32(3);
                    var pageEnd = reader.GetInt32(4);
                    var sectionTitle = reader.IsDBNull(5) ? null : reader.GetString(5);
                    var text = reader.GetString(6);
                    var vscore = reader.GetDouble(7);

                    results.Add(new ScoredChunk(
                        ChunkId: chunkId,
                        DocumentId: documentId,
                        FileName: fileName,
                        PageStart: pageStart,
                        PageEnd: pageEnd,
                        SectionTitle: sectionTitle,
                        Text: text,
                        VectorScore: vscore,
                        FtsScore: null,
                        FusedScore: vscore
                    ));
                }
            }

            if (localTx != null)
            {
                await localTx.CommitAsync(ct).ConfigureAwait(false);
            }

            return results
                .OrderByDescending(r => r.VectorScore)
                .ThenBy(r => r.ChunkId)
                .ToList();
        }
        finally
        {
            if (localTx != null)
            {
                await localTx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
