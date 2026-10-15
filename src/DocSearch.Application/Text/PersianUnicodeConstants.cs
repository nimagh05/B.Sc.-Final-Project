using System.Globalization;

namespace DocSearch.Application.Text;

/// <summary>
/// Explicitly named Unicode code points, ranges, and helpers for Persian text normalization.
/// Every character literal and code point is centralized here to avoid magic constants.
/// </summary>
public static class PersianUnicodeConstants
{
    // --- Invisible and Bidi Control Characters (SPEC-PERSIAN-AND-RAG.md §1.1 #2) ---
    public const char SoftHyphen = '\u00AD';
    public const char ZeroWidthSpace = '\u200B';
    public const char LeftToRightMark = '\u200E';
    public const char RightToLeftMark = '\u200F';
    public const char LeftToRightEmbedding = '\u202A';
    public const char RightToLeftEmbedding = '\u202B';
    public const char PopDirectionalFormatting = '\u202C';
    public const char LeftToRightOverride = '\u202D';
    public const char RightToLeftOverride = '\u202E';
    public const char LeftToRightIsolate = '\u2066';
    public const char RightToLeftIsolate = '\u2067';
    public const char FirstStrongIsolate = '\u2068';
    public const char PopDirectionalIsolate = '\u2069';
    public const char ZeroWidthNoBreakSpace = '\uFEFF'; // Byte order mark

    // --- Persian and Arabic Letters & Marks ---
    public const char ZeroWidthNonJoiner = '\u200C'; // ZWNJ (U+200C)
    public const char Tatweel = '\u0640'; // Kashida (U+0640)
    public const char ArabicYeh = '\u064A'; // ي
    public const char ArabicAlefMaksura = '\u0649'; // ى
    public const char PersianYeh = '\u06CC'; // ی
    public const char ArabicKaf = '\u0643'; // ك
    public const char PersianKeheh = '\u06A9'; // ک
    public const char ArabicTehMarbuta = '\u0629'; // ة
    public const char ArabicHeh = '\u0647'; // ه

    // --- Arabic Diacritics (SPEC §1.2 #1: U+064B–U+065F and U+0670) ---
    public const char ArabicFathatan = '\u064B';
    public const char ArabicDammatan = '\u064C';
    public const char ArabicKasratan = '\u064D';
    public const char ArabicFatha = '\u064E';
    public const char ArabicDamma = '\u064F';
    public const char ArabicKasra = '\u0650';
    public const char ArabicShadda = '\u0651';
    public const char ArabicSukun = '\u0652';
    public const char ArabicMaddahAbove = '\u0653';
    public const char ArabicHamzaAbove = '\u0654';
    public const char ArabicHamzaBelow = '\u0655';
    public const char ArabicSubscriptAlef = '\u0656';
    public const char ArabicInvertedDamma = '\u0657';
    public const char ArabicMarkNoonGhunna = '\u0658';
    public const char ArabicZwarakay = '\u0659';
    public const char ArabicVowelSignSmallVAbove = '\u065A';
    public const char ArabicVowelSignInvertedSmallVAbove = '\u065B';
    public const char ArabicVowelSignDotBelow = '\u065C';
    public const char ArabicReversedDamma = '\u065D';
    public const char ArabicFathaWithTwoDots = '\u065E';
    public const char ArabicWavyHamzaBelow = '\u065F';
    public const char ArabicSuperscriptAlef = '\u0670'; // Dagger Alef

    public const char ArabicDiacriticsStart = ArabicFathatan;
    public const char ArabicDiacriticsEnd = ArabicWavyHamzaBelow;

    // --- Persian Digits (SPEC §1.2 #2: U+06F0–U+06F9) ---
    public const char PersianZero = '\u06F0';
    public const char PersianOne = '\u06F1';
    public const char PersianTwo = '\u06F2';
    public const char PersianThree = '\u06F3';
    public const char PersianFour = '\u06F4';
    public const char PersianFive = '\u06F5';
    public const char PersianSix = '\u06F6';
    public const char PersianSeven = '\u06F7';
    public const char PersianEight = '\u06F8';
    public const char PersianNine = '\u06F9';

    public const char PersianDigitsStart = PersianZero;
    public const char PersianDigitsEnd = PersianNine;

    // --- Arabic-Indic Digits (SPEC §1.2 #2: U+0660–U+0669) ---
    public const char ArabicIndicZero = '\u0660';
    public const char ArabicIndicOne = '\u0661';
    public const char ArabicIndicTwo = '\u0662';
    public const char ArabicIndicThree = '\u0663';
    public const char ArabicIndicFour = '\u0664';
    public const char ArabicIndicFive = '\u0665';
    public const char ArabicIndicSix = '\u0666';
    public const char ArabicIndicSeven = '\u0667';
    public const char ArabicIndicEight = '\u0668';
    public const char ArabicIndicNine = '\u0669';

    public const char ArabicIndicDigitsStart = ArabicIndicZero;
    public const char ArabicIndicDigitsEnd = ArabicIndicNine;

    // --- Number Separators ---
    public const char ArabicDecimalSeparator = '\u066B'; // ٫
    public const char ArabicThousandsSeparator = '\u066C'; // ٬

    // --- Punctuation (SPEC §1.3 #2) ---
    public const char ArabicComma = '\u060C'; // ،
    public const char ArabicSemicolon = '\u061B'; // ؛
    public const char ArabicQuestionMark = '\u061F'; // ؟
    public const char LeftPointingGuillemet = '\u00AB'; // «
    public const char RightPointingGuillemet = '\u00BB'; // »
    public const char HorizontalEllipsis = '\u2026'; // …
    public const char ArabicPercentSign = '\u066A'; // ٪

    // --- ASCII Equivalents ---
    public const char AsciiZero = '0';
    public const char AsciiDot = '.';
    public const char AsciiComma = ',';
    public const char AsciiSemicolon = ';';
    public const char AsciiQuestionMark = '?';
    public const char AsciiQuote = '"';
    public const char AsciiPercent = '%';
    public const string AsciiEllipsis = "...";

    // --- Whitespace, Newlines, and Hyphens ---
    public const char Space = ' ';
    public const char Tab = '\t';
    public const char CarriageReturn = '\r';
    public const char LineFeed = '\n';
    public const char NonBreakingSpace = '\u00A0';
    public const char HyphenMinus = '-'; // ASCII U+002D
    public const char UnicodeHyphen = '\u2010'; // U+2010

    // --- Arabic Presentation Forms (for documentation / reference) ---
    public const char ArabicPresentationFormsAStart = '\uFB50';
    public const char ArabicPresentationFormsAEnd = '\uFDFF';
    public const char ArabicPresentationFormsBStart = '\uFE70';
    public const char ArabicPresentationFormsBEnd = '\uFEFF';

    /// <summary>
    /// Checks if a character is an invisible or bidi control character that should be removed.
    /// Excludes ZWNJ (U+200C), which is handled separately.
    /// </summary>
    public static bool IsInvisibleOrBidiControl(char c)
    {
        return c is ZeroWidthSpace
            or ZeroWidthNoBreakSpace
            or SoftHyphen
            or LeftToRightMark
            or RightToLeftMark
            or (>= LeftToRightEmbedding and <= RightToLeftOverride)
            or (>= LeftToRightIsolate and <= PopDirectionalIsolate);
    }

    /// <summary>
    /// Checks if a character is an Arabic diacritic in the range U+064B–U+065F or U+0670.
    /// </summary>
    public static bool IsArabicDiacritic(char c)
    {
        return (c >= ArabicDiacriticsStart && c <= ArabicDiacriticsEnd) || c == ArabicSuperscriptAlef;
    }

    /// <summary>
    /// Checks if a character is a Persian digit (U+06F0–U+06F9).
    /// </summary>
    public static bool IsPersianDigit(char c)
    {
        return c >= PersianDigitsStart && c <= PersianDigitsEnd;
    }

    /// <summary>
    /// Checks if a character is an Arabic-Indic digit (U+0660–U+0669).
    /// </summary>
    public static bool IsArabicIndicDigit(char c)
    {
        return c >= ArabicIndicDigitsStart && c <= ArabicIndicDigitsEnd;
    }

    /// <summary>
    /// Checks if a character is a Unicode space separator (other than LineFeed).
    /// </summary>
    public static bool IsUnicodeSpaceSeparator(char c)
    {
        return c != LineFeed && (c == NonBreakingSpace || char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator);
    }
}
