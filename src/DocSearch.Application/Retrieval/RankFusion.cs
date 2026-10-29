using DocSearch.Application.Abstractions;

namespace DocSearch.Application.Retrieval;

public static class RankFusion
{
    public const int DefaultK = 60;

    public static IReadOnlyList<ScoredChunk> Fuse(
        IEnumerable<IReadOnlyList<ScoredChunk>> rankedLists,
        int topK,
        int rrfK = DefaultK)
    {
        if (topK <= 0 || rankedLists == null)
        {
            return [];
        }

        var entries = new Dictionary<Guid, FusedEntry>();

        foreach (var list in rankedLists)
        {
            if (list == null || list.Count == 0)
            {
                continue;
            }

            var seenInCurrentList = new HashSet<Guid>();
            var rank = 1;

            foreach (var chunk in list)
            {
                if (chunk == null || !seenInCurrentList.Add(chunk.ChunkId))
                {
                    continue;
                }

                var rrfScore = 1.0 / (rrfK + rank);
                rank++;

                if (!entries.TryGetValue(chunk.ChunkId, out var entry))
                {
                    entry = new FusedEntry
                    {
                        BaseChunk = chunk,
                        FusedScore = rrfScore,
                        MaxVectorScore = chunk.VectorScore,
                        MaxFtsScore = chunk.FtsScore
                    };
                    entries[chunk.ChunkId] = entry;
                }
                else
                {
                    entry.FusedScore += rrfScore;
                    entry.MaxVectorScore = Math.Max(entry.MaxVectorScore, chunk.VectorScore);
                    if (chunk.FtsScore.HasValue)
                    {
                        entry.MaxFtsScore = entry.MaxFtsScore.HasValue
                            ? Math.Max(entry.MaxFtsScore.Value, chunk.FtsScore.Value)
                            : chunk.FtsScore.Value;
                    }
                }
            }
        }

        if (entries.Count == 0)
        {
            return [];
        }

        return entries.Values
            .OrderByDescending(e => e.FusedScore)
            .ThenByDescending(e => e.MaxVectorScore)
            .ThenBy(e => e.BaseChunk.ChunkId)
            .Take(topK)
            .Select(e => e.BaseChunk with
            {
                VectorScore = e.MaxVectorScore,
                FtsScore = e.MaxFtsScore,
                FusedScore = e.FusedScore
            })
            .ToList();
    }

    private sealed class FusedEntry
    {
        public required ScoredChunk BaseChunk { get; init; }
        public double FusedScore { get; set; }
        public double MaxVectorScore { get; set; }
        public double? MaxFtsScore { get; set; }
    }
}
