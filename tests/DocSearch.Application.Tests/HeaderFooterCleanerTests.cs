using DocSearch.Application.Abstractions;
using DocSearch.Application.Chunking;
using DocSearch.Application.Text;
using Xunit;

namespace DocSearch.Application.Tests;

public sealed class HeaderFooterCleanerTests
{
    private readonly HeaderFooterCleaner _cleaner = new(new PersianTextNormalizer());

    [Fact]
    public void Clean_WhenLessThanFourPages_LeavesPagesUntouched()
    {
        // Arrange: 3 pages with repeating headers and page numbers
        var pages = new List<PageText>
        {
            new(1, "دانشگاه تهران\nصفحه ۱\nمتن صفحه اول\nپاورقی تکراری", 0.95, false),
            new(2, "دانشگاه تهران\nصفحه ۲\nمتن صفحه دوم\nپاورقی تکراری", 0.95, false),
            new(3, "دانشگاه تهران\nصفحه ۳\nمتن صفحه سوم\nپاورقی تکراری", 0.95, false),
        };

        // Act
        var cleaned = _cleaner.Clean(pages);

        // Assert: completely untouched
        Assert.Equal(pages.Count, cleaned.Count);
        for (var i = 0; i < pages.Count; i++)
        {
            Assert.Equal(pages[i].Text, cleaned[i].Text);
            Assert.Equal(pages[i].Quality, cleaned[i].Quality);
            Assert.Equal(pages[i].NeedsOcr, cleaned[i].NeedsOcr);
        }
    }

    [Fact]
    public void Clean_WhenHeaderAppearsOnAtLeastFiftyPercentOfPages_RemovesHeader()
    {
        // Arrange: 4 pages where "آیین نامه آموزشی" appears at top on 3 pages (75%)
        var pages = new List<PageText>
        {
            new(1, "آیین نامه آموزشی\nمتن مهم صفحه اول", 0.9, false),
            new(2, "آیین نامه آموزشی\nمتن مهم صفحه دوم", 0.9, false),
            new(3, "آیین نامه آموزشی\nمتن مهم صفحه سوم", 0.9, false),
            new(4, "عنوان متفرقه\nمتن مهم صفحه چهارم", 0.9, false),
        };

        // Act
        var cleaned = _cleaner.Clean(pages);

        // Assert: header removed on pages 1, 2, 3; page 4 keeps "عنوان متفرقه"
        Assert.DoesNotContain("آیین نامه آموزشی", cleaned[0].Text);
        Assert.Contains("متن مهم صفحه اول", cleaned[0].Text);

        Assert.DoesNotContain("آیین نامه آموزشی", cleaned[1].Text);
        Assert.Contains("متن مهم صفحه دوم", cleaned[1].Text);

        Assert.DoesNotContain("آیین نامه آموزشی", cleaned[2].Text);
        Assert.Contains("متن مهم صفحه سوم", cleaned[2].Text);

        Assert.Contains("عنوان متفرقه", cleaned[3].Text);
        Assert.Contains("متن مهم صفحه چهارم", cleaned[3].Text);
    }

    [Fact]
    public void Clean_WhenFooterAppearsOnAtLeastFiftyPercentOfPages_RemovesFooter()
    {
        // Arrange: 4 pages where footer appears on 2 pages (50% threshold)
        var pages = new List<PageText>
        {
            new(1, "متن ۱\nشماره تماس دبیرخانه: ۱۲۳۴۵", 0.9, false),
            new(2, "متن ۲\nشماره تماس دبیرخانه: ۱۲۳۴۵", 0.9, false),
            new(3, "متن ۳\nپاورقی اختصاصی صفحه سه", 0.9, false),
            new(4, "متن ۴\nپاورقی اختصاصی صفحه چهار", 0.9, false),
        };

        // Act
        var cleaned = _cleaner.Clean(pages);

        // Assert: repeating footer removed on pages 1 and 2
        Assert.DoesNotContain("شماره تماس دبیرخانه", cleaned[0].Text);
        Assert.DoesNotContain("شماره تماس دبیرخانه", cleaned[1].Text);
        Assert.Contains("پاورقی اختصاصی صفحه سه", cleaned[2].Text);
        Assert.Contains("پاورقی اختصاصی صفحه چهار", cleaned[3].Text);
    }

    [Fact]
    public void Clean_WhenPageNumberInFirstOrLastTwoLines_RemovesLine()
    {
        // Arrange: 4 pages with page number variations in top or bottom 2 lines
        var pages = new List<PageText>
        {
            new(1, "1\nمتن صفحه یک\n– 1 –", 0.9, false),
            new(2, "صفحه ۲\nمتن صفحه دو", 0.9, false),
            new(3, "متن صفحه سه\nPage 3", 0.9, false),
            new(4, "متن صفحه چهار\n- ۴ -", 0.9, false),
        };

        // Act
        var cleaned = _cleaner.Clean(pages);

        // Assert: all standalone page numbers removed from top/bottom lines
        Assert.Equal("متن صفحه یک", cleaned[0].Text.Trim());
        Assert.Equal("متن صفحه دو", cleaned[1].Text.Trim());
        Assert.Equal("متن صفحه سه", cleaned[2].Text.Trim());
        Assert.Equal("متن صفحه چهار", cleaned[3].Text.Trim());
    }

    [Fact]
    public void Clean_WhenStandaloneNumberInMiddleOfPage_NeverRemovesNumber()
    {
        // Requirement 5: "remove standalone page-number lines only among the first/last 2 non-empty lines of a page, never in the middle (table cells). Add a test."
        var pages = new List<PageText>
        {
            new(1, "سربرگ یک\nسطر دوم\nجدول مشخصات:\nردیف\n1\nتعداد واحد\n3\nپایان جدول\nپاورقی یک", 0.9, false),
            new(2, "سربرگ دو\nسطر دوم\nمتن عادی دو\nپاورقی دو", 0.9, false),
            new(3, "سربرگ سه\nسطر دوم\nمتن عادی سه\nپاورقی سه", 0.9, false),
            new(4, "سربرگ چهار\nسطر دوم\nمتن عادی چهار\nپاورقی چهار", 0.9, false),
        };

        // Act
        var cleaned = _cleaner.Clean(pages);

        // Assert: the standalone numbers "1" and "3" in the middle table cells are preserved
        var page1Text = cleaned[0].Text;
        Assert.Contains("جدول مشخصات:", page1Text);
        Assert.Contains("1", page1Text);
        Assert.Contains("3", page1Text);
        Assert.Contains("پایان جدول", page1Text);
    }

    [Fact]
    public void Clean_PreservesQualityAndPageMetadata()
    {
        var pages = new List<PageText>
        {
            new(10, "متن ۱", 0.88, true),
            new(11, "متن ۲", 0.92, false),
            new(12, "متن ۳", 0.75, true),
            new(13, "متن ۴", 0.99, false),
        };

        var cleaned = _cleaner.Clean(pages);

        Assert.Equal(10, cleaned[0].PageNumber);
        Assert.Equal(0.88, cleaned[0].Quality);
        Assert.True(cleaned[0].NeedsOcr);

        Assert.Equal(11, cleaned[1].PageNumber);
        Assert.Equal(0.92, cleaned[1].Quality);
        Assert.False(cleaned[1].NeedsOcr);
    }
}
