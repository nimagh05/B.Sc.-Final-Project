using System.Diagnostics;
using System.Text;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DocSearch.Application.Tests;

public sealed class PersianTextNormalizerTests
{
    private readonly PersianTextNormalizer _normalizer = new();

    #region Mandatory Test Cases (SPEC-PERSIAN-AND-RAG.md §1.5)

    [Fact]
    public void MandatoryCase01_ArabicKafAndYeh_AreUnifiedInDisplay()
    {
        // 1. كتاب يك (Arabic kaf U+0643 / yeh U+064A) -> کتاب یک
        var input = "\u0643\u062A\u0627\u0628 \u064A\u0643";
        var expected = "کتاب یک";

        var actual = _normalizer.NormalizeForDisplay(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MandatoryCase02_Tatweel_IsRemovedInDisplay()
    {
        // 2. كــتاب (Arabic kaf + tatweel U+0640) -> کتاب
        var input = "\u0643\u0640\u0640\u062A\u0627\u0628";
        var expected = "کتاب";

        var actual = _normalizer.NormalizeForDisplay(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MandatoryCase03_PresentationForms_AreNormalizedByNfkcInDisplay()
    {
        // 3. ﻣﺎﺩﻩ (presentation forms U+FEE3 U+FE8E U+FEA9 U+FEE9) -> ماده
        var input = "\uFEE3\uFE8E\uFEA9\uFEE9";
        var expected = "ماده";

        var actual = _normalizer.NormalizeForDisplay(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MandatoryCase04_PersianDigits_AreKeptInDisplay()
    {
        // 4. ماده ۱۲ -> Display -> ماده ۱۲ (digits kept)
        var input = "ماده ۱۲";
        var expected = "ماده ۱۲";

        var actual = _normalizer.NormalizeForDisplay(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MandatoryCase05_PersianDigits_AreConvertedToAsciiInEmbedding()
    {
        // 5. ماده ۱۲ -> Embedding -> ماده 12
        var input = "ماده ۱۲";
        var expected = "ماده 12";

        var actual = _normalizer.NormalizeForEmbedding(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MandatoryCase06_ArabicIndicDigits_AreConvertedToAsciiInEmbedding()
    {
        // 6. ماده ١٢ (Arabic-Indic U+0661 U+0662) -> Embedding -> ماده 12
        var input = "ماده \u0661\u0662";
        var expected = "ماده 12";

        var actual = _normalizer.NormalizeForEmbedding(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MandatoryCase07_TwoZwnj_AreCollapsedToOneInDisplay()
    {
        // 7. می‌‌شود (two ZWNJ) -> Display -> می‌شود (one ZWNJ)
        var input = "می\u200C\u200Cشود";
        var expected = "می\u200Cشود";

        var actual = _normalizer.NormalizeForDisplay(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MandatoryCase08_ZwnjBeforeSpace_IsRemovedInDisplay()
    {
        // 8. نیم‌ فاصله (ZWNJ before space) -> Display -> نیم فاصله
        var input = "نیم\u200C فاصله";
        var expected = "نیم فاصله";

        var actual = _normalizer.NormalizeForDisplay(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MandatoryCase09_Zwnj_BecomesSpaceInSearch()
    {
        // 9. می‌شود -> Search -> می شود
        var input = "می\u200Cشود";
        var expected = "می شود";

        var actual = _normalizer.NormalizeForSearch(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MandatoryCase10_LatinTextAndPunctuation_AreUnchangedInDisplay()
    {
        // 10. Article 5. -> Display -> Article 5.
        var input = "Article 5.";
        var expected = "Article 5.";

        var actual = _normalizer.NormalizeForDisplay(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MandatoryCase11_LatinText_IsLowercasedInSearch()
    {
        // 11. HELLO World -> Search -> hello world
        var input = "HELLO World";
        var expected = "hello world";

        var actual = _normalizer.NormalizeForSearch(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MandatoryCase12_PunctuationAndSpacing_AreNormalizedInSearch()
    {
        // 12. سلام،  دنیا ؟ -> Search -> سلام, دنیا ?
        var input = "سلام،  دنیا ؟";
        var expected = "سلام, دنیا ?";

        var actual = _normalizer.NormalizeForSearch(input);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\t\t")]
    [InlineData("\r\n   \r\n")]
    public void MandatoryCase13_WhitespaceOnly_ReturnsEmptyString(string whitespace)
    {
        // 13. whitespace -> any -> empty string
        Assert.Equal(string.Empty, _normalizer.NormalizeForDisplay(whitespace));
        Assert.Equal(string.Empty, _normalizer.NormalizeForEmbedding(whitespace));
        Assert.Equal(string.Empty, _normalizer.NormalizeForSearch(whitespace));
    }

    [Fact]
    public void MandatoryCase14_RightToLeftMark_IsRemovedInDisplay()
    {
        // 14. text with U+200F RLM inside -> Display -> RLM removed
        var input = "سلام\u200Fدنیا";
        var expected = "سلامدنیا";

        var actual = _normalizer.NormalizeForDisplay(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MandatoryCase15_LatinHyphenatedLineBreak_IsJoinedInEmbedding()
    {
        // 15. a-\nb -> Embedding -> ab
        var input = "a-\nb";
        var expected = "ab";

        var actual = _normalizer.NormalizeForEmbedding(input);

        Assert.Equal(expected, actual);
    }

    #endregion

    #region Additional Edge Cases (>= 25 cases)

    [Fact]
    public void NullInput_ReturnsEmptyString_ForEveryLayer()
    {
        Assert.Equal(string.Empty, _normalizer.NormalizeForDisplay(null));
        Assert.Equal(string.Empty, _normalizer.NormalizeForEmbedding(null));
        Assert.Equal(string.Empty, _normalizer.NormalizeForSearch(null));
    }

    [Fact]
    public void EmptyInput_ReturnsEmptyString_ForEveryLayer()
    {
        Assert.Equal(string.Empty, _normalizer.NormalizeForDisplay(string.Empty));
        Assert.Equal(string.Empty, _normalizer.NormalizeForEmbedding(string.Empty));
        Assert.Equal(string.Empty, _normalizer.NormalizeForSearch(string.Empty));
    }

    [Fact]
    public void InvisibleOnlyInput_ReturnsEmptyString_ForEveryLayer()
    {
        var input = "\u200B\u200E\u200F\uFEFF\u00AD";
        Assert.Equal(string.Empty, _normalizer.NormalizeForDisplay(input));
        Assert.Equal(string.Empty, _normalizer.NormalizeForEmbedding(input));
        Assert.Equal(string.Empty, _normalizer.NormalizeForSearch(input));
    }

    [Fact]
    public void ZwnjOnlyInput_ReturnsEmptyString_ForEveryLayer()
    {
        var input = "\u200C\u200C\u200C";
        Assert.Equal(string.Empty, _normalizer.NormalizeForDisplay(input));
        Assert.Equal(string.Empty, _normalizer.NormalizeForEmbedding(input));
        Assert.Equal(string.Empty, _normalizer.NormalizeForSearch(input));
    }

    [Fact]
    public void ArabicAlefMaksura_IsNormalizedToPersianYeh()
    {
        // موسی with U+0649 -> موسی with U+06CC
        var input = "\u0645\u0648\u0633\u0649";
        var expected = "موسی";

        var actual = _normalizer.NormalizeForDisplay(input);

        Assert.Equal(expected, actual);
        Assert.Equal(PersianUnicodeConstants.PersianYeh, actual[^1]);
    }

    [Fact]
    public void TehMarbuta_IsPreservedInDisplay_AndMappedToHehInEmbeddingAndSearch()
    {
        var input = "منطقة"; // with U+0629

        var display = _normalizer.NormalizeForDisplay(input);
        var embedding = _normalizer.NormalizeForEmbedding(input);
        var search = _normalizer.NormalizeForSearch(input);

        Assert.Equal("منطقة", display);
        Assert.Equal("منطقه", embedding);
        Assert.Equal("منطقه", search);
    }

    [Fact]
    public void Diacritics_AreRemovedInEmbedding_AndSearch()
    {
        // مدرس with fatha, kasra, shadda, damma
        var input = "مُدَرِّسٌ";

        var display = _normalizer.NormalizeForDisplay(input);
        var embedding = _normalizer.NormalizeForEmbedding(input);
        var search = _normalizer.NormalizeForSearch(input);

        Assert.Equal("مُدَرِّسٌ", display);
        Assert.Equal("مدرس", embedding);
        Assert.Equal("مدرس", search);
    }

    [Fact]
    public void ArabicSuperscriptAlef_IsRemovedInEmbedding()
    {
        // إلٰه with dagger alef U+0670 -> إله
        var input = "إل\u0670ه";
        var expected = "إله";

        var embedding = _normalizer.NormalizeForEmbedding(input);

        Assert.Equal(expected, embedding);
    }

    [Fact]
    public void PersianNumber_WithSeparators_IsNormalizedToAsciiInEmbedding()
    {
        // ۱٬۲۳۴٫۵۶ -> 1,234.56
        var input = "۱\u066C۲۳۴\u066B۵۶";
        var expected = "1,234.56";

        var actual = _normalizer.NormalizeForEmbedding(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ArabicIndicDigits_AllTenDigits_AreNormalizedToAscii()
    {
        // ٠١٢٣٤٥٦٧٨٩ -> 0123456789
        var input = "\u0660\u0661\u0662\u0663\u0664\u0665\u0666\u0667\u0668\u0669";
        var expected = "0123456789";

        var actual = _normalizer.NormalizeForEmbedding(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PersianDigits_AllTenDigits_AreNormalizedToAscii()
    {
        // ۰۱۲۳۴۵۶۷۸۹ -> 0123456789
        var input = "۰۱۲۳۴۵۶۷۸۹";
        var expected = "0123456789";

        var actual = _normalizer.NormalizeForEmbedding(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Dehyphenation_MultiLetterWord_IsJoinedInEmbedding()
    {
        var input = "inter-\nnational";
        var expected = "international";

        var actual = _normalizer.NormalizeForEmbedding(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Dehyphenation_UnicodeHyphen_IsJoinedInEmbedding()
    {
        var input = "multi\u2010\nlingual";
        var expected = "multilingual";

        var actual = _normalizer.NormalizeForEmbedding(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Dehyphenation_PersianAroundHyphen_IsNotJoined()
    {
        var input = "ماده-\n۱۲";

        var display = _normalizer.NormalizeForDisplay(input);
        var embedding = _normalizer.NormalizeForEmbedding(input);

        Assert.Equal("ماده-\n۱۲", display);
        Assert.Equal("ماده- 12", embedding);
    }

    [Fact]
    public void HyphenInsideLatinText_WithoutNewline_IsPreserved()
    {
        var input = "state-of-the-art";

        var display = _normalizer.NormalizeForDisplay(input);
        var embedding = _normalizer.NormalizeForEmbedding(input);

        Assert.Equal(input, display);
        Assert.Equal(input, embedding);
    }

    [Fact]
    public void SingleNewlineInsideParagraph_BecomesSpaceInEmbedding_WhileBlankLinesArePreserved()
    {
        var input = "خط اول\nخط دوم\n\nپاراگراف دوم\nخط بعدی";
        var expected = "خط اول خط دوم\n\nپاراگراف دوم خط بعدی";

        var actual = _normalizer.NormalizeForEmbedding(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ThreeOrMoreNewlines_AreCollapsedToTwoInDisplay()
    {
        var input = "پاراگراف اول\n\n\n\n\nپاراگراف دوم";
        var expected = "پاراگراف اول\n\nپاراگراف دوم";

        var actual = _normalizer.NormalizeForDisplay(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CarriageReturnLineFeed_IsNormalizedToSingleLineFeed()
    {
        var input = "خط اول\r\nخط دوم\rخط سوم";
        var expected = "خط اول\nخط دوم\nخط سوم";

        var actual = _normalizer.NormalizeForDisplay(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ZwnjAtStartOfLine_IsRemovedInDisplay()
    {
        var input = "\u200Cکتاب";
        var expected = "کتاب";

        var actual = _normalizer.NormalizeForDisplay(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ZwnjAtEndOfLine_IsRemovedInDisplay()
    {
        var input = "کتاب\u200C";
        var expected = "کتاب";

        var actual = _normalizer.NormalizeForDisplay(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ZwnjAfterSpace_IsRemovedInDisplay()
    {
        var input = "نیم \u200Cفاصله";
        var expected = "نیم فاصله";

        var actual = _normalizer.NormalizeForDisplay(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ZwnjBetweenPersianLetters_IsPreservedInDisplayAndEmbedding()
    {
        var input = "دانش\u200Cآموز";

        var display = _normalizer.NormalizeForDisplay(input);
        var embedding = _normalizer.NormalizeForEmbedding(input);
        var search = _normalizer.NormalizeForSearch(input);

        Assert.Equal("دانش\u200Cآموز", display);
        Assert.Equal("دانش\u200Cآموز", embedding);
        Assert.Equal("دانش آموز", search);
    }

    [Fact]
    public void MultipleZwnjRuns_AreCollapsedInDisplay()
    {
        var input = "می\u200C\u200C\u200C\u200Cشود";
        var expected = "می\u200Cشود";

        var actual = _normalizer.NormalizeForDisplay(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PunctuationMapping_InSearch_MapsAllSpecifiedChars()
    {
        // «سلام»؛ آیا می‌آیی؟ قیمت٪۱۰…
        var input = "«سلام»؛ آیا می‌آیی؟ قیمت٪۱۰…";
        var expected = "\"سلام\"; آیا می آیی? قیمت%10...";

        var actual = _normalizer.NormalizeForSearch(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void LatinLetters_AreLowercasedInSearch_WithAccents()
    {
        var input = "ASP.NET Core & C# in Café";
        var expected = "asp.net core & c# in café";

        var actual = _normalizer.NormalizeForSearch(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PresentationFormAllah_IsNormalizedByNfkc()
    {
        // ﷲ (U+FDF2) -> الله
        var input = "\uFDF2";
        var expected = "الله";

        var actual = _normalizer.NormalizeForDisplay(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PresentationFormsKeheh_AreNormalized()
    {
        // ﮎ (U+FB8E) -> ک
        var input = "\uFB8E";
        var expected = "ک";

        var actual = _normalizer.NormalizeForDisplay(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SoftHyphen_IsRemovedInDisplay()
    {
        var input = "docu\u00ADment";
        var expected = "document";

        var actual = _normalizer.NormalizeForDisplay(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ZeroWidthSpace_AndBidiControls_AreRemovedInDisplay()
    {
        var input = "\uFEFF\u200B\u202Aمتن\u202C \u2066آزمایشی\u2069";
        var expected = "متن آزمایشی";

        var actual = _normalizer.NormalizeForDisplay(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void UnicodeSpaceSeparators_AreConvertedToAsciiSpace()
    {
        // Non-breaking space U+00A0, Em space U+2003, Ideographic space U+3000
        var input = "کلمه۱\u00A0کلمه۲\u2003کلمه۳\u3000کلمه۴";
        var expected = "کلمه۱ کلمه۲ کلمه۳ کلمه۴";

        var actual = _normalizer.NormalizeForDisplay(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RunsOfSpacesAndTabs_AreCollapsedToSingleSpace()
    {
        var input = "  این   یک    \t   متن      است.  ";
        var expected = "این یک متن است.";

        var actual = _normalizer.NormalizeForDisplay(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MixedPersianEnglishTechnicalText_PreservesIntegrity()
    {
        var input = "مدل bge-m3 برای Embeddings در net10.0 مناسب است.";

        var display = _normalizer.NormalizeForDisplay(input);
        var embedding = _normalizer.NormalizeForEmbedding(input);
        var search = _normalizer.NormalizeForSearch(input);

        Assert.Equal(input, display);
        Assert.Equal(input, embedding);
        Assert.Equal("مدل bge-m3 برای embeddings در net10.0 مناسب است.", search);
    }

    [Fact]
    public void DependencyInjection_AddApplication_RegistersPersianTextNormalizerAsSingleton()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddApplication(configuration);

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ITextNormalizer));

        Assert.NotNull(descriptor);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(typeof(PersianTextNormalizer), descriptor.ImplementationType);
    }

    #endregion

    #region Property-Style Idempotency Tests (>= 200 random strings)

    [Fact]
    public void Normalization_IsIdempotent_AcrossAllThreeMethods_ForOver200GeneratedStrings()
    {
        var random = new Random(1337);
        const int testStringCount = 250;

        var charPool = new List<char>
        {
            // Persian letters
            'ا', 'ب', 'پ', 'ت', 'ث', 'ج', 'چ', 'ح', 'خ', 'د', 'ذ', 'ر', 'ز', 'ژ', 'س', 'ش',
            'ص', 'ض', 'ط', 'ظ', 'ع', 'غ', 'ف', 'ق', 'ک', 'گ', 'ل', 'م', 'ن', 'و', 'ه', 'ی',
            // Arabic letters & variants
            'ك', 'ي', 'ى', 'ة', 'ء', 'أ', 'إ', 'ؤ', 'ئ',
            // Presentation forms
            '\uFEE3', '\uFE8E', '\uFEA9', '\uFEE9', '\uFDF2', '\uFB8E',
            // Diacritics
            '\u064B', '\u064C', '\u064D', '\u064E', '\u064F', '\u0650', '\u0651', '\u0652', '\u0670',
            // Tatweel & ZWNJ
            '\u0640', '\u200C',
            // Invisible & Bidi
            '\u200B', '\u200E', '\u200F', '\u202A', '\u202C', '\u2066', '\uFEFF', '\u00AD',
            // Persian digits
            '۰', '۱', '۲', '۳', '۴', '۵', '۶', '۷', '۸', '۹',
            // Arabic-Indic digits
            '٠', '١', '٢', '٣', '٤', '٥', '٦', '٧', '٨', '٩',
            // Separators & Punctuation
            '٫', '٬', '،', '؛', '؟', '«', '»', '…', '٪', '.', ',', ';', '?', '!', ':', '-',
            // Whitespace
            ' ', '\t', '\r', '\n', '\u00A0',
            // Latin
            'A', 'B', 'c', 'd', 'E', 'f', 'G', 'x', 'Y', 'Z',
            // ASCII digits
            '0', '1', '2', '8', '9'
        };

        for (var i = 0; i < testStringCount; i++)
        {
            var length = random.Next(1, 150);
            var sb = new StringBuilder(length);

            // Periodically inject Latin line breaks for hyphenation test
            if (i % 5 == 0)
            {
                sb.Append("word-\nnext ");
            }

            for (var j = 0; j < length; j++)
            {
                var ch = charPool[random.Next(charPool.Count)];
                sb.Append(ch);
            }

            var sample = sb.ToString();

            // Test Display Idempotency: N(N(x)) == N(x)
            var display1 = _normalizer.NormalizeForDisplay(sample);
            var display2 = _normalizer.NormalizeForDisplay(display1);
            Assert.Equal(display1, display2);

            // Test Embedding Idempotency: N(N(x)) == N(x)
            var embed1 = _normalizer.NormalizeForEmbedding(sample);
            var embed2 = _normalizer.NormalizeForEmbedding(embed1);
            Assert.Equal(embed1, embed2);

            // Test Search Idempotency: N(N(x)) == N(x)
            var search1 = _normalizer.NormalizeForSearch(sample);
            var search2 = _normalizer.NormalizeForSearch(search1);
            Assert.Equal(search1, search2);
        }
    }

    #endregion

    #region Performance Sanity Test (>= 100 KB in < 50 ms)

    [Fact]
    public void Normalization_PerformanceSanity_Normalizes100KbTextInLessThan50Ms()
    {
        // Build >= 100 KB text with realistic mixed Persian, English, numbers, ZWNJs, tatweel, diacritics
        var paragraph = new StringBuilder();
        paragraph.AppendLine("ماده ۱۲: کلیهٔ دانشجویان مقطع کارشناسی و کارشناسی‌ارشد دانشگاه صنعتی شریف موظف به رعایت مقررات آموزشی هستند.");
        paragraph.AppendLine("Student obligations and academic regulations must be adhered to in full accordance with section 4.2-\nsubpart A.");
        paragraph.AppendLine("آیا می‌دانید شهریهٔ ثابت برای سال تحصیلی ۱۴۰۲-۱۴۰۳ معادل ۱٬۲۵۰٫۰۰۰ ریال است؟ (پاراگراف دوم: «ضوابط ثبت‌نام»؛ بند ۳)");
        paragraph.AppendLine("كتابهای درسی با تخفیف ٪۲۰ ارائه می‌گردد و متقاضیان می‌توانند به آدرس https://sharif.edu مراجعه نمایند.");
        paragraph.AppendLine("مُدَرِّس مربوطه در ساعت ۱۰:۳۰ صبح در اتاق شمارهٔ ۴۰۵ حضور خواهد داشت.\n");

        var unitText = paragraph.ToString();
        var bigTextBuilder = new StringBuilder();
        var sample100Kb = string.Empty;
        while (Encoding.UTF8.GetByteCount(sample100Kb) < 105_000)
        {
            bigTextBuilder.Append(unitText);
            sample100Kb = bigTextBuilder.ToString();
        }

        Assert.True(Encoding.UTF8.GetByteCount(sample100Kb) >= 100_000);

        // Warm-up JIT and source-generated regexes
        _ = _normalizer.NormalizeForDisplay(sample100Kb);
        _ = _normalizer.NormalizeForEmbedding(sample100Kb);
        _ = _normalizer.NormalizeForSearch(sample100Kb);

        // Benchmark Display
        var swDisplay = Stopwatch.StartNew();
        var displayResult = _normalizer.NormalizeForDisplay(sample100Kb);
        swDisplay.Stop();

        // Benchmark Embedding
        var swEmbedding = Stopwatch.StartNew();
        var embeddingResult = _normalizer.NormalizeForEmbedding(sample100Kb);
        swEmbedding.Stop();

        // Benchmark Search
        var swSearch = Stopwatch.StartNew();
        var searchResult = _normalizer.NormalizeForSearch(sample100Kb);
        swSearch.Stop();

        Assert.NotEmpty(displayResult);
        Assert.NotEmpty(embeddingResult);
        Assert.NotEmpty(searchResult);

        // Assert all completed in under 50 ms
        Assert.True(swDisplay.ElapsedMilliseconds < 50, $"NormalizeForDisplay took {swDisplay.ElapsedMilliseconds} ms, expected < 50 ms.");
        Assert.True(swEmbedding.ElapsedMilliseconds < 50, $"NormalizeForEmbedding took {swEmbedding.ElapsedMilliseconds} ms, expected < 50 ms.");
        Assert.True(swSearch.ElapsedMilliseconds < 50, $"NormalizeForSearch took {swSearch.ElapsedMilliseconds} ms, expected < 50 ms.");
    }

    #endregion
}
