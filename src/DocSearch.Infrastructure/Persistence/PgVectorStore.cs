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
            ArticleKeys = chunk.Draft.ArticleKeys?.ToArray() ?? [],
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

    public async Task<HybridSearchResult> SearchHybridAsync(HybridSearchRequest request, CancellationToken ct)
    {
        if (request.CollectionIds == null || request.CollectionIds.Count == 0)
        {
            return new HybridSearchResult([], [], [], 0.0);
        }

        var candidateLimit = request.CandidateK > 0 ? request.CandidateK : 30;
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
                var efSearch = Math.Clamp(candidateLimit, 100, 1000);
                setCmd.CommandText = $"SET LOCAL hnsw.ef_search = {efSearch}; SET LOCAL hnsw.iterative_scan = strict_order;";
                await setCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var collectionsParam = request.CollectionIds.Distinct().ToArray();
            var qVector = new Vector(request.QueryEmbedding);

            // 1. Vector candidates query (ordered by cosine distance asc)
            var vectorCandidates = new List<CandidateChunk>();
            await using (var vecCmd = connection.CreateCommand())
            {
                vecCmd.Transaction = activeDbTx;
                vecCmd.CommandText = """
                    SELECT c.id, c.document_id, d.file_name, c.page_start, c.page_end, c.section_title, c.text,
                           c.search_text, c.article_keys,
                           (1.0 - (c.embedding <=> @q)) AS vscore
                    FROM chunks c
                    JOIN documents d ON d.id = c.document_id
                    WHERE c.collection_id = ANY(@collections)
                      AND c.embedding_model = @model
                      AND d.status = 'Ready'
                    ORDER BY c.embedding <=> @q ASC, c.id ASC
                    LIMIT @limit;
                    """;

                vecCmd.Parameters.Add(new NpgsqlParameter("q", qVector));
                vecCmd.Parameters.Add(new NpgsqlParameter("collections", collectionsParam));
                vecCmd.Parameters.Add(new NpgsqlParameter("model", request.EmbeddingModel));
                vecCmd.Parameters.Add(new NpgsqlParameter("limit", candidateLimit));

                await using var reader = await vecCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    vectorCandidates.Add(ReadCandidateChunk(reader, hasFtsScore: false));
                }
            }

            var ftsCandidates = new List<CandidateChunk>();
            var refCandidates = new List<CandidateChunk>();

            if (request.UseHybrid)
            {
                // 2. FTS candidates query (executed sequentially on the same connection)
                if (!string.IsNullOrWhiteSpace(request.TsQuery))
                {
                    await using var ftsCmd = connection.CreateCommand();
                    ftsCmd.Transaction = activeDbTx;
                    ftsCmd.CommandText = """
                        SELECT c.id, c.document_id, d.file_name, c.page_start, c.page_end, c.section_title, c.text,
                               c.search_text, c.article_keys,
                               (1.0 - (c.embedding <=> @q)) AS vscore,
                               ts_rank_cd(c.tsv, to_tsquery('simple', @tsq)) AS fscore
                        FROM chunks c
                        JOIN documents d ON d.id = c.document_id
                        WHERE c.collection_id = ANY(@collections)
                          AND c.embedding_model = @model
                          AND d.status = 'Ready'
                          AND c.tsv @@ to_tsquery('simple', @tsq)
                        ORDER BY fscore DESC, c.id ASC
                        LIMIT @limit;
                        """;

                    ftsCmd.Parameters.Add(new NpgsqlParameter("q", qVector));
                    ftsCmd.Parameters.Add(new NpgsqlParameter("collections", collectionsParam));
                    ftsCmd.Parameters.Add(new NpgsqlParameter("model", request.EmbeddingModel));
                    ftsCmd.Parameters.Add(new NpgsqlParameter("tsq", request.TsQuery));
                    ftsCmd.Parameters.Add(new NpgsqlParameter("limit", candidateLimit));

                    await using var reader = await ftsCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        ftsCandidates.Add(ReadCandidateChunk(reader, hasFtsScore: true));
                    }
                }

                // 3. Dedicated Ref candidates query (executed sequentially on the same connection)
                if (request.ExtractedArticleKeys.Count > 0)
                {
                    await using var refCmd = connection.CreateCommand();
                    refCmd.Transaction = activeDbTx;
                    refCmd.CommandText = """
                        SELECT c.id, c.document_id, d.file_name, c.page_start, c.page_end, c.section_title, c.text,
                               c.search_text, c.article_keys,
                               (1.0 - (c.embedding <=> @q)) AS vscore
                        FROM chunks c
                        JOIN documents d ON d.id = c.document_id
                        WHERE c.collection_id = ANY(@collections)
                          AND c.embedding_model = @model
                          AND d.status = 'Ready'
                          AND c.article_keys && @refs
                        ORDER BY c.embedding <=> @q ASC, c.id ASC
                        LIMIT @limit;
                        """;

                    refCmd.Parameters.Add(new NpgsqlParameter("q", qVector));
                    refCmd.Parameters.Add(new NpgsqlParameter("collections", collectionsParam));
                    refCmd.Parameters.Add(new NpgsqlParameter("model", request.EmbeddingModel));
                    refCmd.Parameters.Add(new NpgsqlParameter("refs", request.ExtractedArticleKeys.Distinct().ToArray()));
                    refCmd.Parameters.Add(new NpgsqlParameter("limit", candidateLimit));

                    await using var reader = await refCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        refCandidates.Add(ReadCandidateChunk(reader, hasFtsScore: false));
                    }
                }
            }

            if (localTx != null)
            {
                await localTx.CommitAsync(ct).ConfigureAwait(false);
            }

            // Compute BestVectorScore across ALL returned candidate lists (Vector, FTS, Ref)
            var allCandidates = vectorCandidates.Concat(ftsCandidates).Concat(refCandidates).ToList();
            var bestVectorScore = allCandidates.Count > 0 ? allCandidates.Max(c => c.VectorScore) : 0.0;

            return new HybridSearchResult(
                vectorCandidates,
                ftsCandidates,
                refCandidates,
                bestVectorScore);
        }
        finally
        {
            if (localTx != null)
            {
                await localTx.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static CandidateChunk ReadCandidateChunk(System.Data.Common.DbDataReader reader, bool hasFtsScore)
    {
        var chunkId = reader.GetGuid(0);
        var documentId = reader.GetGuid(1);
        var fileName = reader.GetString(2);
        var pageStart = reader.GetInt32(3);
        var pageEnd = reader.GetInt32(4);
        var sectionTitle = reader.IsDBNull(5) ? null : reader.GetString(5);
        var text = reader.GetString(6);
        var searchText = reader.GetString(7);
        var articleKeys = reader.IsDBNull(8) ? [] : (string[])reader.GetValue(8);
        var vscore = reader.GetDouble(9);
        double? fscore = (hasFtsScore && !reader.IsDBNull(10)) ? reader.GetDouble(10) : null;

        return new CandidateChunk(
            ChunkId: chunkId,
            DocumentId: documentId,
            FileName: fileName,
            PageStart: pageStart,
            PageEnd: pageEnd,
            SectionTitle: sectionTitle,
            Text: text,
            SearchText: searchText,
            ArticleKeys: articleKeys,
            VectorScore: vscore,
            FtsScore: fscore);
    }
}
