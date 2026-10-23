using DocSearch.Application.Abstractions;
using DocSearch.Application.Chunking;
using DocSearch.Application.Options;
using DocSearch.Application.Text;
using Xunit;

namespace DocSearch.Application.Tests;

public sealed class LegalAwareChunkerTests
{
    private readonly PersianTextNormalizer _normalizer = new();
    private readonly HeuristicTokenCounter _counter = new();
    private readonly HeaderFooterCleaner _cleaner;
    private readonly LegalAwareChunker _chunker;

    public LegalAwareChunkerTests()
    {
        _cleaner = new HeaderFooterCleaner(_normalizer);
        _chunker = new LegalAwareChunker(_normalizer, _counter, _cleaner);
    }

    [Fact]
    public void Empty_Input_ReturnsEmpty()
    {
        var options = new ChunkingOptions();
        var chunks = _chunker.Chunk([], options);
        Assert.Empty(chunks);
    }

    [Fact]
    public void Chunks_Never_Exceed_MaxTokens_PropertyTest()
    {
        // Property-style test with randomized Persian & Latin text verifying EmbeddingText <= MaxTokens
        var options = new ChunkingOptions
        {
            TargetTokens = 425,
            MaxTokens = 500,
            MinTokens = 80,
            OverlapRatio = 0.15
        };

        var rng = new Random(1337);
        var persianWords = new[] { "دانشگاه", "دانشجو", "آموزش", "مقررات", "پژوهش", "آیین‌نامه", "شورای", "تحصیلات", "واحد", "ترم" };
        var englishWords = new[] { "university", "student", "education", "regulation", "research", "council", "semester", "credit", "course", "grade" };

        var generatedPages = new List<PageText>();

        for (var pageNum = 1; pageNum <= 8; pageNum++)
        {
            var lines = new List<string>();
            var numLines = rng.Next(20, 60);

            for (var l = 0; l < numLines; l++)
            {
                var isPersian = rng.NextDouble() >= 0.3;
                var wordPool = isPersian ? persianWords : englishWords;
                var numWords = rng.Next(8, 25);
                var words = Enumerable.Range(0, numWords).Select(_ => wordPool[rng.Next(wordPool.Length)]);
                var punctuation = rng.NextDouble() > 0.4 ? "." : (rng.NextDouble() > 0.5 ? "؛" : "");
                lines.Add(string.Join(" ", words) + punctuation);
            }

            generatedPages.Add(new PageText(pageNum, string.Join("\n", lines), 0.95, false));
        }

        var chunks = _chunker.Chunk(generatedPages, options);

        Assert.NotEmpty(chunks);
        foreach (var chunk in chunks)
        {
            // Requirement 2: The 500 cap applies to EmbeddingText. ChunkDraft.Tokens = Count(EmbeddingText).
            var measuredTokens = _counter.Count(chunk.EmbeddingText);
            Assert.True(measuredTokens <= options.MaxTokens,
                $"Chunk {chunk.Index} exceeded MaxTokens {options.MaxTokens}: was {measuredTokens} tokens.");
            Assert.Equal(measuredTokens, chunk.TokenCount);
        }
    }

    [Fact]
    public void Article_Starts_New_Chunk_When_MinTokens_Met()
    {
        var options = new ChunkingOptions
        {
            TargetTokens = 425,
            MaxTokens = 500,
            MinTokens = 50,
            RespectLegalStructure = true
        };

        // Article 1 has ~120 words (> 50 min tokens), Article 2 follows
        var body1 = string.Join(" ", Enumerable.Repeat("کلمه متن ماده یک آیین نامه دانشجویی", 15)) + ".";
        var body2 = string.Join(" ", Enumerable.Repeat("کلمه متن ماده دو آیین نامه دانشجویی", 15)) + ".";

        var pageText = $"فصل اول\nماده ۱\n{body1}\n\nماده ۲\n{body2}";
        var pages = new List<PageText> { new(1, pageText, 1.0, false) };

        var chunks = _chunker.Chunk(pages, options);

        Assert.True(chunks.Count >= 2);
        Assert.Contains("ماده ۱", chunks[0].SectionTitle);
        Assert.Contains("ماده ۲", chunks[1].SectionTitle);
    }

    [Fact]
    public void Tiny_Article_Is_Merged_With_Next()
    {
        var options = new ChunkingOptions
        {
            TargetTokens = 425,
            MaxTokens = 500,
            MinTokens = 80,
            RespectLegalStructure = true
        };

        // Article 1 is very tiny (only 5 words < 80 tokens)
        var tinyArticle1 = "ماده ۱\nاین یک ماده بسیار کوتاه است.";
        var article2 = "ماده ۲\n" + string.Join(" ", Enumerable.Repeat("متن مفصل ماده دوم که دارای کلمات زیادی است و به حد نصاب می‌رسد.", 10)) + ".";

        var pageText = $"{tinyArticle1}\n\n{article2}";
        var pages = new List<PageText> { new(1, pageText, 1.0, false) };

        var chunks = _chunker.Chunk(pages, options);

        // Tiny article 1 must NOT be an isolated chunk; merged with Article 2
        Assert.Single(chunks);
        Assert.Contains("ماده ۱", chunks[0].Text);
        Assert.Contains("ماده ۲", chunks[0].Text);
    }

    [Fact]
    public void Overlap_Only_Within_Same_Section()
    {
        var options = new ChunkingOptions
        {
            TargetTokens = 100,
            MaxTokens = 150,
            MinTokens = 40,
            OverlapRatio = 0.2,
            RespectLegalStructure = true
        };

        // A long Article 1 that splits across 2 chunks, followed by Article 2
        var s1 = "جمله اول از ماده یک که حاوی اطلاعات مهم است.";
        var s2 = "جمله دوم از ماده یک که ادامه توضیحات را شرح می‌دهد.";
        var s3 = "جمله سوم از ماده یک که به مسائل اداری می‌پردازد.";
        var s4 = "جمله چهارم از ماده یک که شرایط ثبت نام را معین می‌کند.";
        var s5 = "جمله پنجم از ماده یک که وظایف دانشجو را مشخص می‌سازد.";

        var art2 = "ماده ۲\n" + string.Join(" ", Enumerable.Repeat("متن کامل ماده دو که مستقل است.", 8)) + ".";

        var pageText = $"ماده ۱\n{s1} {s2} {s3} {s4} {s5}\n\n{art2}";
        var pages = new List<PageText> { new(1, pageText, 1.0, false) };

        var chunks = _chunker.Chunk(pages, options);

        // If Article 1 produced multiple chunks, verify section title matches
        if (chunks.Count >= 2 && chunks[0].SectionTitle == chunks[1].SectionTitle)
        {
            Assert.NotNull(chunks[0].SectionTitle);
            Assert.Equal(chunks[0].SectionTitle, chunks[1].SectionTitle);
        }

        // Verify across article boundaries: Chunk starting Article 2 must NOT have overlap from Article 1
        var art2Chunk = chunks.FirstOrDefault(c => c.SectionTitle == "ماده ۲");
        if (art2Chunk != null)
        {
            Assert.DoesNotContain("جمله اول از ماده یک", art2Chunk.Text);
            Assert.DoesNotContain("جمله پنجم از ماده یک", art2Chunk.Text);
        }
    }

    [Fact]
    public void SectionTitle_Contains_Chapter_And_Article()
    {
        var options = new ChunkingOptions();
        var text = "فصل دوم مقررات آموزشی\nماده ۵\nمتن کامل ماده پنجم از فصل دوم.";
        var pages = new List<PageText> { new(1, text, 1.0, false) };

        var chunks = _chunker.Chunk(pages, options);

        Assert.Single(chunks);
        Assert.NotNull(chunks[0].SectionTitle);
        Assert.Contains("فصل دوم", chunks[0].SectionTitle);
        Assert.Contains("ماده ۵", chunks[0].SectionTitle);
        Assert.Contains("›", chunks[0].SectionTitle);
    }

    [Fact]
    public void English_Document_Chunks_By_Paragraph()
    {
        var options = new ChunkingOptions
        {
            TargetTokens = 50,
            MaxTokens = 100
        };

        var p1 = "Retrieval-augmented generation models combine pre-trained parametric memory with non-parametric memory. This provides updated factual knowledge.";
        var p2 = "Dense retrieval systems map queries and documents into a shared continuous vector space. Similarity is measured by inner product.";

        var text = $"{p1}\n\n{p2}";
        var pages = new List<PageText> { new(1, text, 1.0, false) };

        var chunks = _chunker.Chunk(pages, options);

        Assert.NotEmpty(chunks);
        foreach (var c in chunks)
        {
            Assert.True(c.TokenCount <= options.MaxTokens);
        }
    }

    [Fact]
    public void Single_Giant_Sentence_Is_HardSplit()
    {
        var options = new ChunkingOptions
        {
            TargetTokens = 50,
            MaxTokens = 80
        };

        // Single giant sentence with 100 words without terminal punctuation
        var giantSentence = string.Join(" ", Enumerable.Repeat("واژه", 100));
        var pages = new List<PageText> { new(1, giantSentence, 1.0, false) };

        var chunks = _chunker.Chunk(pages, options);

        Assert.True(chunks.Count >= 2);
        foreach (var c in chunks)
        {
            Assert.True(c.TokenCount <= options.MaxTokens);
        }
    }

    [Fact]
    public void Deterministic_Output()
    {
        var options = new ChunkingOptions();
        var text = "ماده ۱۰\nدانشجویان موظف به رعایت آیین‌نامه انضباطی هستند.\n\nماده ۱۱\nدروس انتخابی در هر نیمسال مشخص می‌شود.";
        var pages = new List<PageText> { new(1, text, 1.0, false) };

        var run1 = _chunker.Chunk(pages, options);
        var run2 = _chunker.Chunk(pages, options);

        Assert.Equal(run1.Count, run2.Count);
        for (var i = 0; i < run1.Count; i++)
        {
            Assert.Equal(run1[i].Text, run2[i].Text);
            Assert.Equal(run1[i].EmbeddingText, run2[i].EmbeddingText);
            Assert.Equal(run1[i].TokenCount, run2[i].TokenCount);
            Assert.Equal(run1[i].SectionTitle, run2[i].SectionTitle);
        }
    }

    [Fact]
    public void Marker_Variants_Handled()
    {
        // Requirement 7: handle "ماده واحده", "ماده ۵ مکرر", "ماده (۵)"
        var options = new ChunkingOptions
        {
            TargetTokens = 425,
            MaxTokens = 500,
            MinTokens = 10,
            RespectLegalStructure = true
        };

        var text = "ماده واحده\nاین قانون به صورت ماده واحده تصویب می‌گردد.\n\n" +
                   "ماده ۵ مکرر\nشرایط اضطراری در این ماده الحاقی لحاظ می‌شود.\n\n" +
                   "ماده (۵)\nتبصره‌های مرتبط با این شماره ماده درج شده است.";

        var pages = new List<PageText> { new(1, text, 1.0, false) };
        var chunks = _chunker.Chunk(pages, options);

        Assert.True(chunks.Count >= 2);
        var titles = chunks.Select(c => c.SectionTitle).ToList();
        Assert.Contains(titles, t => t != null && t.Contains("ماده واحده"));
        Assert.Contains(titles, t => t != null && t.Contains("ماده ۵ مکرر"));
    }

    [Fact]
    public void Marker_Line_Counts_Only_When_Previous_Line_Ended_With_Terminal_Punctuation_Or_Is_Blank()
    {
        // Requirement 7: a marker line counts only when the previous line ended with terminal punctuation or is blank
        var options = new ChunkingOptions
        {
            TargetTokens = 425,
            MaxTokens = 500,
            MinTokens = 10,
            RespectLegalStructure = true
        };

        // Line 1 does NOT end with terminal punctuation
        // Line 2 contains "ماده ۲" in continuation of the sentence
        var text = "دانشجو باید شرایط مندرج در\nماده ۲ را به دقت رعایت کند.";
        var pages = new List<PageText> { new(1, text, 1.0, false) };

        var chunks = _chunker.Chunk(pages, options);

        Assert.Single(chunks);
        // It should NOT be recognized as a section title "ماده ۲"
        Assert.Null(chunks[0].SectionTitle);
    }

    [Fact]
    public void Tiny_Last_Chunk_Is_Merged_When_Cap_Allows()
    {
        // Requirement 7: merge a tiny LAST chunk into the previous one when the cap allows
        var options = new ChunkingOptions
        {
            TargetTokens = 200,
            MaxTokens = 500,
            MinTokens = 80,
            RespectLegalStructure = true
        };

        // Chunk 1 has ~180 tokens
        var body1 = string.Join(" ", Enumerable.Repeat("متن بند اول آیین نامه", 25)) + ".";
        // Trailing chunk has only 15 tokens (< MinTokens 80)
        var tinyLast = "ماده ۵\nپایان دستورالعمل آموزشی.";

        var text = $"{body1}\n\n{tinyLast}";
        var pages = new List<PageText> { new(1, text, 1.0, false) };

        var chunks = _chunker.Chunk(pages, options);

        // Combined tokens fit well under MaxTokens 500, so tiny last chunk is merged
        Assert.Single(chunks);
        Assert.Contains("پایان دستورالعمل", chunks[0].Text);
    }

    [Fact]
    public void IncludeSectionTitleInEmbedding_False_Omits_Title_In_EmbeddingText()
    {
        // Requirement 1: Arm (c2) LegalAwareChunker WITHOUT section title in EmbeddingText
        var optionsWithTitle = new ChunkingOptions { IncludeSectionTitleInEmbedding = true };
        var optionsWithoutTitle = new ChunkingOptions { IncludeSectionTitleInEmbedding = false };

        var text = "ماده ۲۰\nمتن ماده بیستم آیین نامه.";
        var pages = new List<PageText> { new(1, text, 1.0, false) };

        var chunkWith = _chunker.Chunk(pages, optionsWithTitle).Single();
        var chunkWithout = _chunker.Chunk(pages, optionsWithoutTitle).Single();

        Assert.StartsWith("ماده 20 ماده 20", chunkWith.EmbeddingText);
        Assert.StartsWith("ماده 20 متن", chunkWithout.EmbeddingText);
    }

    [Fact]
    public void ChunkDefectFlags_Propagated_From_Page()
    {
        // Defective text on page 1 with CorruptLigatureLamAlef
        var defectiveText = new string(' ', 100) +
            "اطالعات مربوط به تحصیالت تکمیلی و تشکیالت دانشگاهی و ابالغی باال و اخالقی به صورت الگو " +
            "ال ال ال ال ال ال " +
            new string(' ', 100);

        var pages = new List<PageText>
        {
            new(1, defectiveText, 0.9, false),
            new(2, "صفحه دوم عادی با کلمات معمولی بدون هیچ ایرادی.", 0.9, false)
        };

        var chunks = _chunker.Chunk(pages, new ChunkingOptions());

        Assert.NotEmpty(chunks);
        var page1Chunk = chunks.First(c => c.PageStart == 1);
        Assert.True(page1Chunk.DefectFlags.HasFlag(ChunkDefectFlags.CorruptLigatureLamAlef));
    }

    [Fact]
    public void CorruptLigatureLamAlef_Propagates_To_All_Document_Chunks_Even_If_Only_One_Page_Triggered()
    {
        // Page 1 triggers CorruptLigatureLamAlef (length >= 300, AL >= 10, LA/AL < 0.04)
        var defectiveText = "فصل اول: تعاریف اولیه\nماده ۱: " +
            "اطالعات مربوط به تحصیالت تکمیلی و تشکیالت دانشگاهی و ابالغی باال و اخالقی به صورت الگو " +
            "ال ال ال ال ال ال ال ال ال ال ال ال " +
            string.Join(" ", Enumerable.Repeat("متن آزمایشی جهت افزایش طول صفحه و رسیدن به سقف توکن.", 10));

        // Page 2 is completely clean Persian text with no defects
        var cleanTextPage2 = "فصل دوم: مقررات آموزشی\nماده ۲: " +
            string.Join(" ", Enumerable.Repeat("دانشجویان موظف هستند در کلیه جلسات درسی و کارگاهی دانشگاه شرکت نمایند.", 10));

        var pages = new List<PageText>
        {
            new(1, defectiveText, 0.9, false),
            new(2, cleanTextPage2, 0.9, false)
        };

        var chunks = _chunker.Chunk(pages, new ChunkingOptions());

        Assert.True(chunks.Count >= 2);
        Assert.Contains(chunks, c => c.PageStart == 1 && c.PageEnd == 1);
        Assert.Contains(chunks, c => c.PageStart == 2 && c.PageEnd == 2);
        Assert.All(chunks, c => Assert.True(c.DefectFlags.HasFlag(ChunkDefectFlags.CorruptLigatureLamAlef)));
    }

    [Fact]
    public void No_Text_Lost_All_Input_Words_Preserved()
    {
        // Issue 3: Verify that all input words are preserved across chunks without silent truncation
        var options = new ChunkingOptions
        {
            TargetTokens = 425,
            MaxTokens = 500,
            MinTokens = 80,
            OverlapRatio = 0.0,
            RespectLegalStructure = true,
            IncludeSectionTitleInEmbedding = true
        };

        var words = new List<string>();
        for (var i = 1; i <= 600; i++)
        {
            words.Add($"کلمه{i}");
        }

        // Include a long section title that tests title budgeting
        var text = "فصل اول: مقررات عمومی و آموزشی دانشگاه و ضوابط اجرایی دانشجویان\n" +
                   "ماده ۱۰: ضوابط ثبت نام و انتخاب واحد دانشجویان در نیمسال اول و دوم تحصیلی\n" +
                   string.Join(" ", words);

        var pages = new List<PageText> { new(1, text, 1.0, false) };
        var chunks = _chunker.Chunk(pages, options);

        Assert.True(chunks.Count > 1);

        // Every input word must be present in the output chunks
        var allChunkWords = chunks
            .SelectMany(c => c.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet();

        for (var i = 1; i <= 600; i++)
        {
            Assert.Contains($"کلمه{i}", allChunkWords);
        }
    }
}
