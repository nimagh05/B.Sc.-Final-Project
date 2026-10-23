using System.Text.RegularExpressions;

namespace DocSearch.Application.Chunking;

public static partial class LegalStructureMarkers
{
    private static readonly char[] _terminalPunctuation = ['.', ':', '؛', ';', '؟', '?', '!'];
    private static readonly char[] _trailingClosingChars = ['»', '«', ')', '(', ']', '[', '"', '\'', ' ', '\t'];

    // Chapter: (فصل|بخش|باب) followed strictly by valid chapter/part identifier, or (Chapter|Part|Section)
    [GeneratedRegex(
        @"^(?:(?:فصل|بخش|باب)\s+(?:اول|یکم|نخست|دوم|سوم|چهارم|پنجم|ششم|هفتم|هشتم|نهم|دهم|یازدهم|دوازدهم|[\p{Nd}]+|[IVXLC]+|[الف-ی](?=\s|$|[\.\)\-–]))|(?:Chapter|Part|Section)\s+(?:[\p{Nd}]+|[IVXLC]+|[A-Za-z]+))",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000)]
    public static partial Regex ChapterRegex();

    // Article: handles "ماده واحده", "ماده ۵ مکرر", "ماده (۵)", "ماده ۱", "Article 5"
    [GeneratedRegex(@"^(?:ماده\s*(?:واحده|(?:\(\s*[\p{Nd}]+\s*\)|[\p{Nd}]+))(?:\s+مکرر)?|Article\s+[\p{Nd}]+)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    public static partial Regex ArticleRegex();

    // Note: تبصره optionally followed by digits or parenthesized digits
    [GeneratedRegex(@"^تبصره\s*(?:[\p{Nd}]+|\(\s*[\p{Nd}]+\s*\))?", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    public static partial Regex NoteRegex();

    // Clause: بند followed by identifier, or single Persian letter with closing bracket, or number with dot/bracket
    [GeneratedRegex(@"^(?:بند\s+\S+|[الف-ی]\s*[\)\-–]|[\p{Nd}]+\s*[\.\)\-–]\s)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    public static partial Regex ClauseRegex();

    // Numbered heading in academic papers: e.g. "1 Introduction", "2.1 Background" (short lines <= 12 words)
    [GeneratedRegex(@"^[\p{Nd}]+(?:\.[\p{Nd}]+)*\s+\S", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    public static partial Regex NumberedHeadingRegex();

    public static bool EndsWithTerminalPunctuation(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        var trimmed = line.TrimEnd(_trailingClosingChars);
        if (trimmed.Length == 0)
        {
            return true;
        }

        var lastChar = trimmed[^1];
        return Array.IndexOf(_terminalPunctuation, lastChar) >= 0;
    }
}
