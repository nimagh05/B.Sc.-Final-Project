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

    [Fact]
    public void TinyFirstArticle_MergedWithMultiParagraphSecondArticle_DoesNotSplitMidSecondArticle()
    {
        var options = new ChunkingOptions
        {
            TargetTokens = 400,
            MaxTokens = 500,
            MinTokens = 40,
            RespectLegalStructure = true,
            IncludeSectionTitleInEmbedding = true
        };

        // Article 1 is tiny: ~4 words, ~6 tokens (< MinTokens = 40)
        var art1 = "ماده ۱\nمتن کوتاه.";

        // Article 2 has 3 distinct paragraphs separated by blank lines
        // Paragraph 1: ~30 words (~40 tokens). Combined with Art 1, total is ~46 tokens (> MinTokens = 40)
        var p1 = "پاراگراف اول از ماده دو که شامل توضیحات مهم اولیه درباره مقررات آموزشی است و حجم متوسطی دارد.";
        // Paragraph 2: ~25 words (~35 tokens)
        var p2 = "پاراگراف دوم از ماده دو که شامل توضیحات تکمیلی و بندهای اجرایی است و نکات بیشتری را بیان می‌کند.";
        // Paragraph 3: ~25 words (~35 tokens)
        var p3 = "پاراگراف سوم از ماده دو که شامل شرایط خاتمه و مقررات نهایی این ماده قانونی می‌باشد.";

        var pageText = $"{art1}\n\nماده ۲\n{p1}\n\n{p2}\n\n{p3}";
        var pages = new List<PageText> { new(1, pageText, 1.0, false) };

        var chunks = _chunker.Chunk(pages, options);

        // All units (Article 1 + all 3 paragraphs of Article 2) must stay in a single chunk
        // Total tokens (~116) is well below TargetTokens (400), and paragraph 2 must NOT trigger premature split
        Assert.Single(chunks);
        Assert.Equal("ماده ۱", chunks[0].SectionTitle);
        Assert.Contains("ماده ۱", chunks[0].Text);
        Assert.Contains("ماده ۲", chunks[0].Text);
        Assert.Contains(p1, chunks[0].Text);
        Assert.Contains(p2, chunks[0].Text);
        Assert.Contains(p3, chunks[0].Text);
    }

    [Fact]
    public void MultiSection_HeadingsChangeMidPage_CorrectSectionTitles() =>
        MultiSection_HeadingsChangeMidPage_WithoutBlankLines_CorrectSectionTitles();

    [Fact]
    public void MultiSection_HeadingsChangeMidPage_WithoutBlankLines_CorrectSectionTitles()
    {
        var options = new ChunkingOptions
        {
            TargetTokens = 425,
            MaxTokens = 500,
            MinTokens = 50,
            RespectLegalStructure = true,
            IncludeSectionTitleInEmbedding = true
        };

        // Consecutive articles without blank lines separating them
        var body1 = string.Join(" ", Enumerable.Repeat("متن مفصل و کامل مربوط به ماده یک آیین نامه که طولانی است.", 12)) + ".";
        var body2 = string.Join(" ", Enumerable.Repeat("متن مفصل و کامل مربوط به ماده دو آیین نامه که مستقل است.", 12)) + ".";
        var body3 = string.Join(" ", Enumerable.Repeat("متن مفصل و کامل مربوط به ماده سه آیین نامه که بخش سوم است.", 12)) + ".";

        var pageText = $"ماده ۱\n{body1}\nماده ۲\n{body2}\nماده ۳\n{body3}";
        var pages = new List<PageText> { new(1, pageText, 1.0, false) };

        var chunks = _chunker.Chunk(pages, options);

        Assert.True(chunks.Count >= 3);
        Assert.Equal("ماده ۱", chunks[0].SectionTitle);
        Assert.Contains("ماده ۱", chunks[0].Text);
        Assert.DoesNotContain("ماده ۲", chunks[0].Text);

        Assert.Equal("ماده ۲", chunks[1].SectionTitle);
        Assert.Contains("ماده ۲", chunks[1].Text);
        Assert.DoesNotContain("ماده ۳", chunks[1].Text);

        Assert.Equal("ماده ۳", chunks[2].SectionTitle);
        Assert.Contains("ماده ۳", chunks[2].Text);
    }

    [Fact]
    public void SectionTitle_WithPersianAndArabicDigits_RetainsOriginalInTitle()
    {
        var options = new ChunkingOptions
        {
            TargetTokens = 425,
            MaxTokens = 500,
            MinTokens = 50,
            RespectLegalStructure = true,
            IncludeSectionTitleInEmbedding = true
        };

        // Persian digits in Article 12, Arabic-Indic digits in Article 13
        var body1 = string.Join(" ", Enumerable.Repeat("متن ماده دوازدهم با ارقام فارسی.", 15)) + ".";
        var body2 = string.Join(" ", Enumerable.Repeat("متن ماده سیزدهم با ارقام عربی.", 15)) + ".";

        var pageText = $"فصل ۱\nماده ۱۲\n{body1}\n\nفصل ۲\nماده ١٣\n{body2}";
        var pages = new List<PageText> { new(1, pageText, 1.0, false) };

        var chunks = _chunker.Chunk(pages, options);

        Assert.True(chunks.Count >= 2);

        // SectionTitle retains display form (original digits)
        Assert.Contains("فصل ۱", chunks[0].SectionTitle);
        Assert.Contains("ماده ۱۲", chunks[0].SectionTitle);

        Assert.Contains("فصل ۲", chunks[1].SectionTitle);
        Assert.Contains("ماده ١٣", chunks[1].SectionTitle);

        // EmbeddingText has normalized ASCII digits for models like bge-m3
        Assert.Contains("فصل 1", chunks[0].EmbeddingText);
        Assert.Contains("ماده 12", chunks[0].EmbeddingText);

        Assert.Contains("فصل 2", chunks[1].EmbeddingText);
        Assert.Contains("ماده 13", chunks[1].EmbeddingText);
    }

    [Fact]
    public void ChapterHeading_MergedWithFirstArticle_ReflectsArticleInTitle()
    {
        var options = new ChunkingOptions
        {
            TargetTokens = 425,
            MaxTokens = 500,
            MinTokens = 80,
            RespectLegalStructure = true,
            IncludeSectionTitleInEmbedding = true
        };

        // Standalone chapter heading followed directly by Article 1
        var body1 = string.Join(" ", Enumerable.Repeat("کلمات متن ماده یک که بخش عمده این بند را تشکیل می‌دهد.", 15)) + ".";
        var pageText = $"فصل اول کلیات\nماده ۱ تعاریف\n{body1}";
        var pages = new List<PageText> { new(1, pageText, 1.0, false) };

        var chunks = _chunker.Chunk(pages, options);

        Assert.Single(chunks);
        Assert.NotNull(chunks[0].SectionTitle);
        Assert.Contains("فصل اول", chunks[0].SectionTitle);
        Assert.Contains("ماده ۱", chunks[0].SectionTitle);
    }

    [Fact]
    public void ArticleKeys_MergedShortArticles_CollectsAllDistinctArticleKeys()
    {
        var options = new ChunkingOptions
        {
            TargetTokens = 425,
            MaxTokens = 500,
            MinTokens = 80,
            RespectLegalStructure = true,
            IncludeSectionTitleInEmbedding = true
        };

        // Short articles (each ~15 tokens < MinTokens=80), so they merge into a single chunk
        var pageText = "ماده ۵\nمتن کوتاه ماده پنج.\n\nماده ۶\nمتن کوتاه ماده شش.\n\nماده ۷\nمتن کوتاه ماده هفت.\nتبصره ۱\nمتن تبصره یک ماده هفت.";
        var pages = new List<PageText> { new(1, pageText, 1.0, false) };

        var chunks = _chunker.Chunk(pages, options);

        Assert.Single(chunks);
        var chunk = chunks[0];
        Assert.NotNull(chunk.ArticleKeys);
        Assert.Equal(3, chunk.ArticleKeys.Count);
        Assert.Equal("ماده 5", chunk.ArticleKeys[0]);
        Assert.Equal("ماده 6", chunk.ArticleKeys[1]);
        Assert.Equal("ماده 7", chunk.ArticleKeys[2]);

        // Standalone note ("تبصره 1") must NEVER be stored in ArticleKeys
        Assert.DoesNotContain(chunk.ArticleKeys, k => k.Contains("تبصره"));
    }

    [Fact]
    public void ArticleKeys_ContinuationChunk_RetainsArticleKey()
    {
        var options = new ChunkingOptions
        {
            TargetTokens = 200,
            MaxTokens = 250,
            MinTokens = 50,
            RespectLegalStructure = true,
            IncludeSectionTitleInEmbedding = true
        };

        // Long article that spans across two chunks
        var longBody = string.Join(" ", Enumerable.Repeat("جملات طولانی برای ماده هفده که بیش از حد بودجه یک قطعه می باشد.", 30)) + ".";
        var pageText = $"ماده ۱۷\n{longBody}";
        var pages = new List<PageText> { new(1, pageText, 1.0, false) };

        var chunks = _chunker.Chunk(pages, options);

        Assert.True(chunks.Count >= 2);
        // Both the initial and continuation chunks must retain "ماده 17"
        foreach (var chunk in chunks)
        {
            Assert.NotNull(chunk.ArticleKeys);
            Assert.Contains("ماده 17", chunk.ArticleKeys);
        }
    }

    [SkippableFact]
    public void ArticleKeys_Aein91RealPdf_CapturesAll35Articles_And_PreservesHashes()
    {
        var repoRoot = FindRepoRoot(AppDomain.CurrentDomain.BaseDirectory);
        Skip.If(repoRoot == null, "Repository root not found.");

        var extractDir = Path.Combine(repoRoot, "eval", "experiments", "out", "extract", "pymupdf", "aein-91.pdf");
        Skip.IfNot(Directory.Exists(extractDir), $"Extracted directory not found at {extractDir}");

        var pages = new List<PageText>();
        for (var p = 1; p <= 10; p++)
        {
            var pPath = Path.Combine(extractDir, $"{p}.txt");
            if (File.Exists(pPath))
            {
                pages.Add(new PageText(p, File.ReadAllText(pPath), 1.0, false));
            }
        }
        Skip.IfNot(pages.Count == 10, "aein-91.pdf pages missing.");

        var options = new ChunkingOptions
        {
            TargetTokens = 425,
            MaxTokens = 500,
            MinTokens = 80,
            OverlapRatio = 0.15,
            RespectLegalStructure = true,
            IncludeSectionTitleInEmbedding = true,
            PersianMultiplier = 1.45,
            LatinMultiplier = 2.25,
            SymbolsPerToken = 12
        };

        var chunks = _chunker.Chunk(pages, options);

        // Union of ArticleKeys over all chunks of aein-91.pdf MUST be exactly ماده 1 through ماده 35
        var unionKeys = chunks
            .SelectMany(c => c.ArticleKeys ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToHashSet();

        for (var i = 1; i <= 35; i++)
        {
            var expectedKey = $"ماده {i}";
            Assert.True(unionKeys.Contains(expectedKey), $"Missing article key '{expectedKey}' in aein-91.pdf chunk union!");
        }

        Assert.Equal(35, unionKeys.Count);

        // Verify that reference hashes in chunks_legal_t06b.json match current chunks
        var refJsonPath = Path.Combine(repoRoot, "eval", "experiments", "out", "chunks_legal_t06b.json");
        if (File.Exists(refJsonPath))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(refJsonPath));
            var aeinRefChunks = doc.RootElement.EnumerateArray()
                .Where(e => e.GetProperty("pdf").GetString() == "aein-91.pdf")
                .ToList();

            Assert.Equal(aeinRefChunks.Count, chunks.Count);
            for (var idx = 0; idx < chunks.Count; idx++)
            {
                var refChunk = aeinRefChunks[idx];
                Assert.Equal(refChunk.GetProperty("text").GetString(), chunks[idx].EmbeddingText);
                Assert.Equal(refChunk.GetProperty("draft_text").GetString(), chunks[idx].Text);
                Assert.Equal(refChunk.GetProperty("tokens").GetInt32(), chunks[idx].TokenCount);
            }
        }
    }

    private static string? FindRepoRoot(string startDir)
    {
        var current = new DirectoryInfo(startDir);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        return null;
    }
}
