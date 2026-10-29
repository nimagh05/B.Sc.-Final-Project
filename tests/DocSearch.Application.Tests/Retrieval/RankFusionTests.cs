using DocSearch.Application.Abstractions;
using DocSearch.Application.Retrieval;
using Xunit;

namespace DocSearch.Application.Tests.Retrieval;

public sealed class RankFusionTests
{
    [Fact]
    public void Fuse_HandComputedExample_MatchesExpectedOrderAndScores()
    {
        // Arrange
        var chunkA = CreateChunk(Guid.Parse("11111111-1111-1111-1111-111111111111"), vectorScore: 0.9, ftsScore: null);
        var chunkB = CreateChunk(Guid.Parse("22222222-2222-2222-2222-222222222222"), vectorScore: 0.8, ftsScore: null);
        var chunkC = CreateChunk(Guid.Parse("33333333-3333-3333-3333-333333333333"), vectorScore: 0.0, ftsScore: 3.0);

        var chunkBInFts = CreateChunk(Guid.Parse("22222222-2222-2222-2222-222222222222"), vectorScore: 0.0, ftsScore: 5.0);

        var list1 = new List<ScoredChunk> { chunkA, chunkB };
        var list2 = new List<ScoredChunk> { chunkBInFts, chunkC };

        const int rrfK = 60;
        var expectedScoreA = 1.0 / (rrfK + 1); // 1/61 ~ 0.0163934426
        var expectedScoreB = (1.0 / (rrfK + 2)) + (1.0 / (rrfK + 1)); // 1/62 + 1/61 ~ 0.0325224748
        var expectedScoreC = 1.0 / (rrfK + 2); // 1/62 ~ 0.0161290322

        // Act
        var fused = RankFusion.Fuse([list1, list2], topK: 10, rrfK: rrfK);

        // Assert
        Assert.Equal(3, fused.Count);

        // Rank 1: Chunk B
        Assert.Equal(chunkB.ChunkId, fused[0].ChunkId);
        Assert.Equal(expectedScoreB, fused[0].FusedScore, precision: 6);
        Assert.Equal(0.8, fused[0].VectorScore);
        Assert.Equal(5.0, fused[0].FtsScore);

        // Rank 2: Chunk A
        Assert.Equal(chunkA.ChunkId, fused[1].ChunkId);
        Assert.Equal(expectedScoreA, fused[1].FusedScore, precision: 6);
        Assert.Equal(0.9, fused[1].VectorScore);
        Assert.Null(fused[1].FtsScore);

        // Rank 3: Chunk C
        Assert.Equal(chunkC.ChunkId, fused[2].ChunkId);
        Assert.Equal(expectedScoreC, fused[2].FusedScore, precision: 6);
        Assert.Equal(0.0, fused[2].VectorScore);
        Assert.Equal(3.0, fused[2].FtsScore);
    }

    [Fact]
    public void Fuse_DeterministicTieBreaking_OrdersByVScoreThenChunkId()
    {
        // Arrange
        // Chunk X and Chunk Y have same RRF score (rank 1 in two single-item lists)
        // But Chunk X has higher VectorScore than Chunk Y
        var chunkX = CreateChunk(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), vectorScore: 0.85);
        var chunkY = CreateChunk(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), vectorScore: 0.75);

        var listX = new List<ScoredChunk> { chunkX };
        var listY = new List<ScoredChunk> { chunkY };

        var fused = RankFusion.Fuse([listX, listY], topK: 2, rrfK: 60);

        Assert.Equal(2, fused.Count);
        Assert.Equal(chunkX.ChunkId, fused[0].ChunkId);
        Assert.Equal(chunkY.ChunkId, fused[1].ChunkId);

        // Now test second tie-breaker: same FusedScore AND same VectorScore -> sort by ChunkId asc
        var id1 = Guid.Parse("11111111-0000-0000-0000-000000000000");
        var id2 = Guid.Parse("22222222-0000-0000-0000-000000000000");

        var chunk1 = CreateChunk(id1, vectorScore: 0.8);
        var chunk2 = CreateChunk(id2, vectorScore: 0.8);

        // Even if list has id2 first, tie breaker chunkId asc should put id1 first
        var listTie1 = new List<ScoredChunk> { chunk2 };
        var listTie2 = new List<ScoredChunk> { chunk1 };

        var fusedTie = RankFusion.Fuse([listTie1, listTie2], topK: 2, rrfK: 60);

        Assert.Equal(2, fusedTie.Count);
        Assert.Equal(id1, fusedTie[0].ChunkId);
        Assert.Equal(id2, fusedTie[1].ChunkId);
    }

    [Fact]
    public void Fuse_SingleList_PreservesRankOrder()
    {
        // Arrange
        var chunks = Enumerable.Range(1, 5)
            .Select(i => CreateChunk(Guid.NewGuid(), vectorScore: 1.0 - (i * 0.1)))
            .ToList();

        // Act
        var fused = RankFusion.Fuse([chunks], topK: 5, rrfK: 60);

        // Assert
        Assert.Equal(5, fused.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(chunks[i].ChunkId, fused[i].ChunkId);
            Assert.Equal(1.0 / (60 + i + 1), fused[i].FusedScore, precision: 6);
        }
    }

    [Fact]
    public void Fuse_DuplicatesInSingleList_OnlyFirstOccurrenceCounts()
    {
        // Arrange
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        var chunkA1 = CreateChunk(idA, vectorScore: 0.9);
        var chunkA2 = CreateChunk(idA, vectorScore: 0.5); // duplicate in same list
        var chunkB = CreateChunk(idB, vectorScore: 0.8);

        var list = new List<ScoredChunk> { chunkA1, chunkA2, chunkB };

        // Act
        var fused = RankFusion.Fuse([list], topK: 10, rrfK: 60);

        // Assert
        Assert.Equal(2, fused.Count);
        Assert.Equal(idA, fused[0].ChunkId);
        Assert.Equal(1.0 / (60 + 1), fused[0].FusedScore, precision: 6);

        Assert.Equal(idB, fused[1].ChunkId);
        Assert.Equal(1.0 / (60 + 2), fused[1].FusedScore, precision: 6);
    }

    [Fact]
    public void Fuse_MissingOrEmptyLists_ReturnsExpected()
    {
        Assert.Empty(RankFusion.Fuse([], topK: 5));
        Assert.Empty(RankFusion.Fuse([[]], topK: 5));
        Assert.Empty(RankFusion.Fuse([[], []], topK: 5));

        var validChunk = CreateChunk(Guid.NewGuid(), vectorScore: 0.9);
        var fused = RankFusion.Fuse([[], [validChunk], []], topK: 5);

        Assert.Single(fused);
        Assert.Equal(validChunk.ChunkId, fused[0].ChunkId);
    }

    [Fact]
    public void Fuse_InvalidTopK_ReturnsEmpty()
    {
        var chunk = CreateChunk(Guid.NewGuid(), vectorScore: 0.9);
        Assert.Empty(RankFusion.Fuse([[chunk]], topK: 0));
        Assert.Empty(RankFusion.Fuse([[chunk]], topK: -1));
    }

    [Fact]
    public void Fuse_RespectsTopK_TruncatesResults()
    {
        var chunks = Enumerable.Range(1, 10)
            .Select(i => CreateChunk(Guid.NewGuid(), vectorScore: 0.5))
            .ToList();

        var fused = RankFusion.Fuse([chunks], topK: 3);
        Assert.Equal(3, fused.Count);
    }

    private static ScoredChunk CreateChunk(
        Guid chunkId,
        double vectorScore,
        double? ftsScore = null)
    {
        return new ScoredChunk(
            ChunkId: chunkId,
            DocumentId: Guid.NewGuid(),
            FileName: "test.pdf",
            PageStart: 1,
            PageEnd: 1,
            SectionTitle: "Section",
            Text: "Sample text",
            VectorScore: vectorScore,
            FtsScore: ftsScore,
            FusedScore: 0.0);
    }
}
