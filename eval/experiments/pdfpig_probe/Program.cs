using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace PdfPigProbe;

public static class Program
{
    private static readonly string[] ForwardWords = ["که", "را", "در", "از", "این", "با", "است"];
    private static readonly string[] ReversedWords = ["هک", "ار", "رد", "زا", "نیا", "اب", "تسا"];

    public static int Main(string[] args)
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var repoRoot = FindRepoRoot(baseDir);
        if (repoRoot == null)
        {
            Console.Error.WriteLine("Error: Could not locate repo root.");
            return 1;
        }

        var pdfsDir = Path.Combine(repoRoot, "eval", "pdfs");
        var outDir = Path.Combine(repoRoot, "eval", "experiments", "out", "extract", "pdfpig");
        var csvPath = Path.Combine(repoRoot, "eval", "experiments", "out", "extract", "pdfpig_summary.csv");

        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);

        var pdfFiles = Directory.GetFiles(pdfsDir, "*.pdf", SearchOption.AllDirectories);
        Console.WriteLine($"Found {pdfFiles.Length} PDF files to probe with PdfPig.");

        var records = new List<string>
        {
            "pdf,lib,page,chars,letterRatio,persianRatio,presentationRatio,replacementRatio,looksReversed,quality,empty_page,seconds"
        };

        foreach (var pdfPath in pdfFiles)
        {
            var pdfName = Path.GetFileName(pdfPath);
            var pdfOutDir = Path.Combine(outDir, pdfName);
            Directory.CreateDirectory(pdfOutDir);

            Console.WriteLine($"Processing {pdfName}...");
            try
            {
                using var document = PdfDocument.Open(pdfPath);
                var totalPages = document.NumberOfPages;

                for (var pageNum = 1; pageNum <= totalPages; pageNum++)
                {
                    var sw = Stopwatch.StartNew();
                    string pageText;
                    try
                    {
                        var page = document.GetPage(pageNum);
                        pageText = ContentOrderTextExtractor.GetText(page) ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(pageText))
                        {
                            pageText = page.Text ?? string.Empty;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  Warning: page {pageNum} extraction failed: {ex.Message}");
                        pageText = string.Empty;
                    }
                    sw.Stop();
                    var seconds = sw.Elapsed.TotalSeconds;

                    var pageTxtFile = Path.Combine(pdfOutDir, $"{pageNum}.txt");
                    File.WriteAllText(pageTxtFile, pageText, Encoding.UTF8);

                    var metrics = ComputeMetrics(pageText, seconds);
                    var record = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0},pdfpig,{1},{2},{3:F4},{4:F4},{5:F4},{6:F4},{7},{8:F4},{9},{10:F4}",
                        pdfName,
                        pageNum,
                        metrics.Chars,
                        metrics.LetterRatio,
                        metrics.PersianRatio,
                        metrics.PresentationRatio,
                        metrics.ReplacementRatio,
                        metrics.LooksReversed ? "True" : "False",
                        metrics.Quality,
                        metrics.EmptyPage ? "True" : "False",
                        metrics.Seconds
                    );
                    records.Add(record);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Error opening {pdfName}: {ex.Message}");
            }
        }

        File.WriteAllLines(csvPath, records, Encoding.UTF8);
        Console.WriteLine($"PdfPig extraction complete. Summary written to {csvPath}");
        return 0;
    }

    private static (int Chars, double LetterRatio, double PersianRatio, double PresentationRatio, double ReplacementRatio, bool LooksReversed, double Quality, bool EmptyPage, double Seconds)
        ComputeMetrics(string text, double seconds)
    {
        var chars = 0;
        var letters = 0;
        var presentation = 0;
        var replacements = 0;
        var persian = 0;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            chars++;

            if (char.IsLetter(c))
            {
                letters++;
                if (IsPersianOrArabic(c)) persian++;
            }

            if (IsPresentationForm(c)) presentation++;
            if (c == '\uFFFD') replacements++;
        }

        var letterRatio = chars > 0 ? (double)letters / chars : 0.0;
        var persianRatio = letters > 0 ? (double)persian / letters : 0.0;
        var presentationRatio = chars > 0 ? (double)presentation / chars : 0.0;
        var replacementRatio = chars > 0 ? (double)replacements / chars : 0.0;

        var (forwardCount, reversedCount) = CountKeywords(text);
        var looksReversed = reversedCount > 1.5 * forwardCount && reversedCount >= 5;

        double quality = 1.0;
        if (chars < 20)
        {
            quality = 0.0;
        }
        else
        {
            if (letterRatio < 0.5) quality -= 0.4;
            if (replacementRatio > 0.01) quality -= 0.4;
            if (presentationRatio > 0.2) quality -= 0.2;
            if (looksReversed) quality -= 0.5;
            quality = Math.Clamp(quality, 0.0, 1.0);
        }

        var emptyPage = chars < 20;
        return (chars, letterRatio, persianRatio, presentationRatio, replacementRatio, looksReversed, quality, emptyPage, seconds);
    }

    private static bool IsPresentationForm(char c) =>
        (c >= 0xFB50 && c <= 0xFDFF) || (c >= 0xFE70 && c <= 0xFEFF);

    private static bool IsPersianOrArabic(char c) =>
        (c >= 0x0600 && c <= 0x06FF) ||
        (c >= 0x0750 && c <= 0x077F) ||
        (c >= 0xFB50 && c <= 0xFDFF) ||
        (c >= 0xFE70 && c <= 0xFEFF);

    private static (int Forward, int Reversed) CountKeywords(string text)
    {
        var forward = 0;
        var reversed = 0;

        var tokens = Regex.Split(text, @"\s+");
        foreach (var token in tokens)
        {
            var clean = Regex.Replace(token, @"[^\p{L}\p{Nd}]", "");
            if (string.IsNullOrEmpty(clean)) continue;

            foreach (var fw in ForwardWords)
            {
                if (clean == fw) forward++;
            }
            foreach (var rw in ReversedWords)
            {
                if (clean == rw) reversed++;
            }
        }

        return (forward, reversed);
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
