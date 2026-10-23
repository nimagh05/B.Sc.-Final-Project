using System.Text.RegularExpressions;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Options;
using DocSearch.Application.Text;

namespace DocSearch.Application.Chunking;

public sealed partial class LegalAwareChunker : IChunker
{
    private readonly ITextNormalizer _normalizer;
    private readonly ITokenCounter _tokenCounter;
    private readonly HeaderFooterCleaner _cleaner;

    public LegalAwareChunker(
        ITextNormalizer? normalizer = null,
        ITokenCounter? tokenCounter = null,
        HeaderFooterCleaner? cleaner = null)
    {
        _normalizer = normalizer ?? new PersianTextNormalizer();
        _tokenCounter = tokenCounter ?? new HeuristicTokenCounter();
        _cleaner = cleaner ?? new HeaderFooterCleaner(_normalizer);
    }

    [GeneratedRegex(@"(?<=[\.\!\?؟؛])\s+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SentenceSplitRegex();

    public IReadOnlyList<ChunkDraft> Chunk(IReadOnlyList<PageText> pages, ChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(options);

        if (pages.Count == 0)
        {
            return [];
        }

        // 1. Header/Footer cleaning
        var cleanedPages = _cleaner.Clean(pages);

        // 2. Compute defect flags per page; CMap defect belongs to document's fonts, so propagate CorruptLigatureLamAlef document-wide
        var pageDefectFlags = new Dictionary<int, ChunkDefectFlags>();
        var documentHasCorruptLigature = false;
        foreach (var p in cleanedPages)
        {
            var flags = TextDefectDetector.DetectPageDefects(p.Text);
            if (flags.HasFlag(ChunkDefectFlags.CorruptLigatureLamAlef))
            {
                documentHasCorruptLigature = true;
            }
            pageDefectFlags[p.PageNumber] = flags;
        }

        if (documentHasCorruptLigature)
        {
            foreach (var pageNum in pageDefectFlags.Keys.ToList())
            {
                pageDefectFlags[pageNum] |= ChunkDefectFlags.CorruptLigatureLamAlef;
            }
        }

        // 3. Parse lines and track structure markers
        var units = ParseDocumentUnits(cleanedPages, options);
        if (units.Count == 0)
        {
            return [];
        }

        // 4. Greedy packing with legal boundaries
        var chunkBuilders = PackChunks(units, options);
        if (chunkBuilders.Count == 0)
        {
            return [];
        }

        // 5. Finalize chunks, apply overlap, enforce cap on EmbeddingText, set defect flags
        var finalChunks = new List<ChunkDraft>(chunkBuilders.Count);
        for (var idx = 0; idx < chunkBuilders.Count; idx++)
        {
            var cb = chunkBuilders[idx];
            var draft = BuildChunkDraft(idx, cb, options, pageDefectFlags);
            finalChunks.Add(draft);
        }

        return finalChunks;
    }

    private List<DocumentUnit> ParseDocumentUnits(IReadOnlyList<PageText> pages, ChunkingOptions options)
    {
        var units = new List<DocumentUnit>();

        string? currentChapter = null;
        string? currentArticle = null;
        string? currentHeading = null;

        string? previousLine = null;
        var previousWasMarker = false;

        foreach (var page in pages)
        {
            var lines = page.Text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

            var paragraphLines = new List<(string Line, int Page)>();

            void FlushParagraph()
            {
                if (paragraphLines.Count == 0) return;

                var paraText = string.Join(" ", paragraphLines.Select(pl => pl.Line.Trim()));
                var paraStartPage = paragraphLines[0].Page;
                var paraEndPage = paragraphLines[^1].Page;

                // Split paragraph into sentences
                var sentences = SentenceSplitRegex().Split(paraText);
                for (var sIdx = 0; sIdx < sentences.Length; sIdx++)
                {
                    var sent = sentences[sIdx].Trim();
                    if (string.IsNullOrEmpty(sent)) continue;

                    var isFirstInPara = sIdx == 0;
                    var sectionTitle = options.RespectLegalStructure
                        ? FormatSectionTitle(currentChapter, currentArticle, currentHeading)
                        : null;

                    var titleTokens = (options.IncludeSectionTitleInEmbedding && !string.IsNullOrWhiteSpace(sectionTitle))
                        ? _tokenCounter.Count(sectionTitle + "\n")
                        : 0;

                    var maxUnitTokens = Math.Max(1, options.MaxTokens - titleTokens);
                    var targetUnitTokens = Math.Max(1, options.TargetTokens - titleTokens);

                    // If single sentence unit exceeds maxUnitTokens, hard-split at whitespace boundaries
                    var sentTokens = _tokenCounter.Count(sent);
                    if (sentTokens > maxUnitTokens)
                    {
                        var words = sent.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        var subWords = new List<string>();
                        var subStart = isFirstInPara;

                        foreach (var w in words)
                        {
                            subWords.Add(w);
                            var subText = string.Join(" ", subWords);
                            if (_tokenCounter.Count(subText) >= targetUnitTokens && subWords.Count > 1)
                            {
                                subWords.RemoveAt(subWords.Count - 1);
                                var emitText = string.Join(" ", subWords);
                                units.Add(new DocumentUnit(
                                    emitText,
                                    paraStartPage,
                                    paraEndPage,
                                    sectionTitle,
                                    currentArticle,
                                    currentChapter,
                                    subStart,
                                    false));

                                subWords.Clear();
                                subWords.Add(w);
                                subStart = false;
                            }
                        }

                        if (subWords.Count > 0)
                        {
                            units.Add(new DocumentUnit(
                                string.Join(" ", subWords),
                                paraStartPage,
                                paraEndPage,
                                sectionTitle,
                                currentArticle,
                                currentChapter,
                                subStart,
                                false));
                        }
                    }
                    else
                    {
                        units.Add(new DocumentUnit(
                            sent,
                            paraStartPage,
                            paraEndPage,
                            sectionTitle,
                            currentArticle,
                            currentChapter,
                            isFirstInPara,
                            false));
                    }
                }

                paragraphLines.Clear();
            }

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    FlushParagraph();
                    previousLine = "";
                    previousWasMarker = false;
                    continue;
                }

                // Normalizer display form for marker matching
                var displayLine = _normalizer.NormalizeForDisplay(trimmed);

                // A marker line counts ONLY when previous line ended with terminal punctuation, was blank, or was itself a marker
                var canBeMarker = string.IsNullOrWhiteSpace(previousLine)
                                  || previousWasMarker
                                  || LegalStructureMarkers.EndsWithTerminalPunctuation(previousLine);

                var isChapter = false;
                var isArticle = false;
                var isHeading = false;

                if (options.RespectLegalStructure && canBeMarker)
                {
                    var chMatch = LegalStructureMarkers.ChapterRegex().Match(displayLine);
                    if (chMatch.Success)
                    {
                        isChapter = true;
                        currentChapter = chMatch.Value.Trim();
                        currentArticle = null;
                        currentHeading = null;
                    }
                    else
                    {
                        var artMatch = LegalStructureMarkers.ArticleRegex().Match(displayLine);
                        if (artMatch.Success)
                        {
                            isArticle = true;
                            currentArticle = artMatch.Value.Trim();
                            currentHeading = null;
                        }
                        else
                        {
                            var headMatch = LegalStructureMarkers.NumberedHeadingRegex().Match(displayLine);
                            var wordCount = displayLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                            if (headMatch.Success && wordCount <= 12 && currentArticle == null && currentChapter == null)
                            {
                                isHeading = true;
                                currentHeading = displayLine;
                            }
                        }
                    }
                }

                if (isChapter || isArticle || isHeading)
                {
                    FlushParagraph();
                    previousWasMarker = true;
                }
                else
                {
                    previousWasMarker = false;
                }

                paragraphLines.Add((trimmed, page.PageNumber));
                previousLine = trimmed;
            }

            FlushParagraph();
        }

        return units;
    }

    private static string? FormatSectionTitle(string? chapter, string? article, string? heading)
    {
        string? title;
        if (!string.IsNullOrWhiteSpace(chapter) && !string.IsNullOrWhiteSpace(article))
        {
            title = $"{chapter} › {article}";
        }
        else if (!string.IsNullOrWhiteSpace(article))
        {
            title = article;
        }
        else if (!string.IsNullOrWhiteSpace(chapter))
        {
            title = chapter;
        }
        else if (!string.IsNullOrWhiteSpace(heading))
        {
            title = heading;
        }
        else
        {
            title = null;
        }

        if (title != null && title.Length > 200)
        {
            title = title[..200].TrimEnd();
        }

        return title;
    }

    private List<ChunkBuilder> PackChunks(IReadOnlyList<DocumentUnit> units, ChunkingOptions options)
    {
        var chunks = new List<ChunkBuilder>();
        ChunkBuilder? current = null;

        for (var i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            var unitTokens = _tokenCounter.Count(unit.Text);

            var isNewArticle = options.RespectLegalStructure &&
                               !string.IsNullOrEmpty(unit.Article) &&
                               (current == null || current.Article != unit.Article) &&
                               unit.IsParagraphStart;

            var isNewChapter = options.RespectLegalStructure &&
                               !string.IsNullOrEmpty(unit.Chapter) &&
                               (current == null || current.Chapter != unit.Chapter) &&
                               unit.IsParagraphStart;

            if (current != null)
            {
                // Issue 2: require MinTokens before breaking on chapter/article
                var hardBoundary = (isNewChapter || isNewArticle) && current.EstimatedTokens >= options.MinTokens;

                var titleTokens = (options.IncludeSectionTitleInEmbedding && !string.IsNullOrWhiteSpace(current.SectionTitle))
                    ? _tokenCounter.Count(current.SectionTitle + "\n")
                    : 0;

                var tokenLimitExceeded = (titleTokens + current.EstimatedTokens + unitTokens) > options.TargetTokens;

                if (hardBoundary || tokenLimitExceeded)
                {
                    chunks.Add(current);
                    current = null;
                }
            }

            if (current == null)
            {
                current = new ChunkBuilder(unit.SectionTitle, unit.Article, unit.Chapter);

                // Apply overlap from previous chunk if within the same section
                if (options.OverlapRatio > 0 && chunks.Count > 0)
                {
                    var prevChunk = chunks[^1];
                    var sameSection = !options.RespectLegalStructure ||
                                      (prevChunk.Article == unit.Article && prevChunk.Chapter == unit.Chapter);

                    if (sameSection && prevChunk.Units.Count > 0)
                    {
                        var overlapBudget = (int)(options.OverlapRatio * options.TargetTokens);
                        var accumulatedOverlapTokens = 0;
                        var overlapUnits = new List<DocumentUnit>();

                        for (var uIdx = prevChunk.Units.Count - 1; uIdx >= 0; uIdx--)
                        {
                            var cand = prevChunk.Units[uIdx];
                            var candTokens = _tokenCounter.Count(cand.Text);
                            if (accumulatedOverlapTokens + candTokens <= overlapBudget)
                            {
                                overlapUnits.Insert(0, cand);
                                accumulatedOverlapTokens += candTokens;
                            }
                            else
                            {
                                break;
                            }
                        }

                        current.OverlapUnits.AddRange(overlapUnits);
                    }
                }
            }

            current.Units.Add(unit);
            current.EstimatedTokens += unitTokens;
        }

        if (current != null && current.Units.Count > 0)
        {
            // Requirement 7: merge a tiny LAST chunk into the previous one when the cap allows
            if (chunks.Count > 0 && current.EstimatedTokens < options.MinTokens)
            {
                var prev = chunks[^1];
                var prevTitleTokens = (options.IncludeSectionTitleInEmbedding && !string.IsNullOrWhiteSpace(prev.SectionTitle))
                    ? _tokenCounter.Count(prev.SectionTitle + "\n")
                    : 0;
                var candidateCombinedTokens = prevTitleTokens + prev.EstimatedTokens + current.EstimatedTokens;
                if (candidateCombinedTokens <= options.MaxTokens)
                {
                    prev.Units.AddRange(current.Units);
                    prev.EstimatedTokens += current.EstimatedTokens;
                    return chunks;
                }
            }

            chunks.Add(current);
        }

        return chunks;
    }

    private ChunkDraft BuildChunkDraft(
        int index,
        ChunkBuilder builder,
        ChunkingOptions options,
        Dictionary<int, ChunkDefectFlags> pageDefectFlags)
    {
        var overlapTexts = builder.OverlapUnits.Select(u => u.Text).ToList();
        var bodyTexts = builder.Units.Select(u => u.Text).ToList();

        var sectionPrefix = (options.IncludeSectionTitleInEmbedding && !string.IsNullOrWhiteSpace(builder.SectionTitle))
            ? builder.SectionTitle + "\n"
            : "";

        // Enforce the 500 cap on EmbeddingText: drop overlap sentences first if over MaxTokens
        while (overlapTexts.Count > 0)
        {
            var combinedText = string.Join(" ", overlapTexts.Concat(bodyTexts));
            var candidateEmbeddingText = _normalizer.NormalizeForEmbedding(sectionPrefix + combinedText);
            if (_tokenCounter.Count(candidateEmbeddingText) <= options.MaxTokens)
            {
                break;
            }

            // Drop earliest overlap sentence
            overlapTexts.RemoveAt(0);
        }

        var finalText = string.Join(" ", overlapTexts.Concat(bodyTexts));
        var finalEmbeddingText = _normalizer.NormalizeForEmbedding(sectionPrefix + finalText);
        var finalTokens = _tokenCounter.Count(finalEmbeddingText);

        var pageStart = builder.Units.Min(u => u.PageStart);
        var pageEnd = builder.Units.Max(u => u.PageEnd);

        // Aggregate defect flags across all pages spanned by this chunk
        var flags = ChunkDefectFlags.None;
        for (var p = pageStart; p <= pageEnd; p++)
        {
            if (pageDefectFlags.TryGetValue(p, out var pageFlags))
            {
                flags |= pageFlags;
            }
        }

        var searchText = _normalizer.NormalizeForSearch(finalText);

        return new ChunkDraft(
            index,
            pageStart,
            pageEnd,
            builder.SectionTitle,
            finalText,
            searchText,
            finalEmbeddingText,
            finalTokens,
            flags);
    }

    private sealed record DocumentUnit(
        string Text,
        int PageStart,
        int PageEnd,
        string? SectionTitle,
        string? Article,
        string? Chapter,
        bool IsParagraphStart,
        bool IsHardSplit);

    private sealed class ChunkBuilder
    {
        public string? SectionTitle { get; }
        public string? Article { get; }
        public string? Chapter { get; }
        public List<DocumentUnit> OverlapUnits { get; } = [];
        public List<DocumentUnit> Units { get; } = [];
        public int EstimatedTokens { get; set; }

        public ChunkBuilder(string? sectionTitle, string? article, string? chapter)
        {
            SectionTitle = sectionTitle;
            Article = article;
            Chapter = chapter;
        }
    }
}
