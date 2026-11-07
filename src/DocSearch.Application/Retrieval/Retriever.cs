using System.Text.RegularExpressions;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Options;
using DocSearch.Application.Text;
using Microsoft.Extensions.Options;

namespace DocSearch.Application.Retrieval;

public sealed partial class Retriever(
    IQueryEmbedder queryEmbedder,
    IVectorStore vectorStore,
    IOptions<RagOptions> ragOptions,
    IOptions<AiOptions> aiOptions,
    ITextNormalizer? normalizer = null) : IRetriever
{
    private readonly ITextNormalizer _normalizer = normalizer ?? new PersianTextNormalizer();

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
        var useHybrid = ragOptions.Value.UseHybrid;

        var queryEmbedding = await queryEmbedder.EmbedQueryAsync(question, ct).ConfigureAwait(false);

        var qEmb = _normalizer.NormalizeForEmbedding(question);
        var refExtraction = LegalReferenceExtractor.Extract(qEmb);

        string? tsQuery = null;
        string? qSearch = null;
        if (useHybrid)
        {
            qSearch = _normalizer.NormalizeForSearch(question);
            tsQuery = TsQueryBuilder.BuildTsQuery(qSearch);
        }

        var hybridRequest = new HybridSearchRequest(
            QueryEmbedding: queryEmbedding,
            QueryText: question,
            TsQuery: tsQuery,
            ExtractedArticleKeys: refExtraction.ArticleKeys,
            ExtractedNoteNumber: refExtraction.NoteNumber,
            CollectionIds: collectionIds,
            EmbeddingModel: model,
            CandidateK: candidateK,
            TopK: effectiveTopK,
            UseHybrid: useHybrid);

        var searchResult = await vectorStore.SearchHybridAsync(hybridRequest, ct).ConfigureAwait(false);

        if (!useHybrid)
        {
            var vectorOnlyChunks = searchResult.VectorCandidates
                .Take(effectiveTopK)
                .Select(c => new ScoredChunk(
                    c.ChunkId, c.DocumentId, c.FileName, c.PageStart, c.PageEnd,
                    c.SectionTitle, c.Text, c.VectorScore, null, c.VectorScore))
                .ToList();

            return new RetrievalResult(vectorOnlyChunks, searchResult.BestVectorScore, HasFtsHit: false);
        }

        // Hybrid mode: build ranked lists for Vector, FTS, and Ref
        var vectorList = searchResult.VectorCandidates
            .Select(c => new ScoredChunk(
                c.ChunkId, c.DocumentId, c.FileName, c.PageStart, c.PageEnd,
                c.SectionTitle, c.Text, c.VectorScore, c.FtsScore, c.VectorScore))
            .ToList();

        var ftsList = searchResult.FtsCandidates
            .Select(c => new ScoredChunk(
                c.ChunkId, c.DocumentId, c.FileName, c.PageStart, c.PageEnd,
                c.SectionTitle, c.Text, c.VectorScore, c.FtsScore, c.FtsScore ?? 0.0))
            .ToList();

        // Ref candidates:
        var refCandidates = searchResult.RefCandidates.ToList();

        // Fallback for legacy chunks: if refCandidates is empty but article keys were extracted,
        // scan vector & FTS candidates for chunks with empty ArticleKeys matching the article in SectionTitle or SearchText[..120]
        if (refCandidates.Count == 0 && refExtraction.ArticleKeys.Count > 0)
        {
            var fallbackCandidates = new List<CandidateChunk>();
            var seenChunkIds = new HashSet<Guid>();

            foreach (var cand in searchResult.VectorCandidates.Concat(searchResult.FtsCandidates))
            {
                if (cand.ArticleKeys.Count == 0 && seenChunkIds.Add(cand.ChunkId))
                {
                    if (MatchesLegacyReference(cand, refExtraction.ArticleKeys))
                    {
                        fallbackCandidates.Add(cand);
                    }
                }
            }

            refCandidates = fallbackCandidates;
        }

        // Note-based ordering for ref candidates:
        // When ExtractedNoteNumber is present, chunks of that article whose SearchText matches تبصره <N> come first
        var orderedRefCandidates = OrderRefCandidates(refCandidates, refExtraction.NoteNumber);

        var refList = orderedRefCandidates
            .Select(c => new ScoredChunk(
                c.ChunkId, c.DocumentId, c.FileName, c.PageStart, c.PageEnd,
                c.SectionTitle, c.Text, c.VectorScore, c.FtsScore, c.VectorScore))
            .ToList();

        // Reciprocal Rank Fusion
        var rankedLists = new List<IReadOnlyList<ScoredChunk>>();
        if (vectorList.Count > 0) rankedLists.Add(vectorList);
        if (ftsList.Count > 0) rankedLists.Add(ftsList);
        if (refList.Count > 0) rankedLists.Add(refList);

        var fusedChunks = RankFusion.Fuse(rankedLists, effectiveTopK, ragOptions.Value.RrfK);

        // Compute HasFtsHit using C# token coverage (and Ref presence)
        var hasFtsHit = ComputeHasFtsHit(searchResult.FtsCandidates, orderedRefCandidates, qSearch);

        return new RetrievalResult(fusedChunks, searchResult.BestVectorScore, hasFtsHit);
    }

    private static List<CandidateChunk> OrderRefCandidates(List<CandidateChunk> candidates, int? noteNumber)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return [];
        }

        if (!noteNumber.HasValue)
        {
            return candidates
                .OrderByDescending(c => c.VectorScore)
                .ThenBy(c => c.ChunkId)
                .ToList();
        }

        var noteRegex = new Regex($@"تبصره\s*{noteNumber.Value}(?![0-9])", RegexOptions.CultureInvariant);

        var noteMatches = new List<CandidateChunk>();
        var nonNoteMatches = new List<CandidateChunk>();

        foreach (var c in candidates)
        {
            if (noteRegex.IsMatch(c.SearchText))
            {
                noteMatches.Add(c);
            }
            else
            {
                nonNoteMatches.Add(c);
            }
        }

        return noteMatches
            .OrderByDescending(c => c.VectorScore)
            .ThenBy(c => c.ChunkId)
            .Concat(nonNoteMatches
                .OrderByDescending(c => c.VectorScore)
                .ThenBy(c => c.ChunkId))
            .ToList();
    }

    private static readonly HashSet<string> _legalStructuralKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ماده", "تبصره", "بند", "فصل", "جزء", "اصل", "واحده", "مکرر",
        "article", "section", "clause", "chapter", "part"
    };

    [GeneratedRegex(@"^(ماده|تبصره|بند|فصل|جزء|اصل|واحده|مکرر|article|section|clause|chapter|part)([0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AttachedLegalKeywordRegex();

    private static bool IsLegalOrNumericToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return true;
        }

        if (_legalStructuralKeywords.Contains(token))
        {
            return true;
        }

        if (token.All(char.IsAsciiDigit))
        {
            return true;
        }

        if (AttachedLegalKeywordRegex().IsMatch(token))
        {
            return true;
        }

        return false;
    }

    private static bool ComputeHasFtsHit(
        IReadOnlyList<CandidateChunk> ftsCandidates,
        List<CandidateChunk> refCandidates,
        string? qSearch)
    {
        if (string.IsNullOrWhiteSpace(qSearch))
        {
            return refCandidates != null && refCandidates.Count > 0;
        }

        var queryTokens = TsQueryBuilder.ExtractQueryTokens(qSearch);
        if (queryTokens.Count == 0)
        {
            return refCandidates != null && refCandidates.Count > 0;
        }

        if (refCandidates != null && refCandidates.Count > 0)
        {
            var nonLegalTokens = queryTokens.Where(t => !IsLegalOrNumericToken(t)).ToList();
            if (nonLegalTokens.Count == 0)
            {
                // Query has NO non-legal tokens (e.g. literally "ماده 7" or "تبصره 1 ماده 7")
                return true;
            }

            // Query HAS non-legal tokens: check if at least 1 non-legal query token is present in SearchText of at least 1 ref candidate
            var hasRefTopicHit = refCandidates.Any(c =>
            {
                var candTokens = TsQueryBuilder.TokenizeForCoverage(c.SearchText);
                return nonLegalTokens.Any(candTokens.Contains);
            });

            if (hasRefTopicHit)
            {
                return true;
            }
        }

        if (ftsCandidates == null || ftsCandidates.Count == 0)
        {
            return false;
        }

        foreach (var cand in ftsCandidates)
        {
            var candTokens = TsQueryBuilder.TokenizeForCoverage(cand.SearchText);
            var matchCount = queryTokens.Count(t => candTokens.Contains(t));
            var coverage = matchCount / (double)queryTokens.Count;

            if (queryTokens.Count == 1 && matchCount >= 1)
            {
                return true;
            }

            if (queryTokens.Count >= 2 && matchCount >= 2 && coverage >= 0.5)
            {
                return true;
            }
        }

        return false;
    }

    private bool MatchesLegacyReference(CandidateChunk cand, IReadOnlyList<string> articleKeys)
    {
        var normTitle = cand.SectionTitle != null ? _normalizer.NormalizeForEmbedding(cand.SectionTitle) : "";
        var searchPrefix = cand.SearchText.Length <= 120 ? cand.SearchText : cand.SearchText[..120];

        foreach (var key in articleKeys)
        {
            var parts = key.Split(' ', 2);
            if (parts.Length == 2)
            {
                var kw = parts[0];
                var num = parts[1];
                var pattern = $@"(?:{Regex.Escape(kw)}\s*{Regex.Escape(num)}(?![0-9]))";
                if (Regex.IsMatch(normTitle, pattern, RegexOptions.CultureInvariant) ||
                    Regex.IsMatch(searchPrefix, pattern, RegexOptions.CultureInvariant))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
