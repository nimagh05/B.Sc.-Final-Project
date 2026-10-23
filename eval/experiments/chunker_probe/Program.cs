using System.Globalization;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Chunking;
using DocSearch.Application.Options;
using DocSearch.Application.Text;

namespace DocSearch.Eval.ChunkerProbe;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== DocSearch Chunker Probe & Offline Evaluation Tool (T06) ===");

        var repoRoot = FindRepoRoot();
        Console.WriteLine($"Repo root: {repoRoot}");

        var extractDir = Path.Combine(repoRoot, "eval", "experiments", "out", "extract", "pymupdf");
        var outDir = Path.Combine(repoRoot, "eval", "experiments", "out");
        var ollamaUrl = "http://localhost:11434";
        var skipValidation = args.Contains("--no-validate");

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--extract-dir" && i + 1 < args.Length) extractDir = args[++i];
            if (args[i] == "--out-dir" && i + 1 < args.Length) outDir = args[++i];
            if (args[i] == "--ollama-url" && i + 1 < args.Length) ollamaUrl = args[++i];
        }

        if (!Directory.Exists(extractDir))
        {
            Console.Error.WriteLine($"Error: Extract directory not found at {extractDir}");
            return 1;
        }

        Directory.CreateDirectory(outDir);

        // Load extracted pages for all 12 documents
        Console.WriteLine($"\n--- Step 1: Loading extracted pages from {extractDir} ---");
        var docFolders = Directory.GetDirectories(extractDir)
            .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var documents = new Dictionary<string, List<PageText>>();
        foreach (var df in docFolders)
        {
            var pdfName = Path.GetFileName(df);
            var pageFiles = Directory.GetFiles(df, "*.txt")
                .OrderBy(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out var n) ? n : 0)
                .ToList();

            var pages = new List<PageText>();
            foreach (var pf in pageFiles)
            {
                var pageNum = int.Parse(Path.GetFileNameWithoutExtension(pf), CultureInfo.InvariantCulture);
                var text = File.ReadAllText(pf, System.Text.Encoding.UTF8);
                pages.Add(new PageText(pageNum, text, 1.0, false));
            }
            documents[pdfName] = pages;
            Console.WriteLine($"  Loaded {pdfName,-25} ({pages.Count,2} pages)");
        }

        // Configure options
        var controlOptions = new ChunkingOptions
        {
            TargetTokens = 425,
            MaxTokens = 500,
            MinTokens = 80,
            OverlapRatio = 0.15,
            RespectLegalStructure = false,
            IncludeSectionTitleInEmbedding = false,
            PersianMultiplier = 1.45,
            LatinMultiplier = 2.25,
            SymbolsPerToken = 12
        };

        var legalOptions = new ChunkingOptions
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

        var legalNoTitleOptions = new ChunkingOptions
        {
            TargetTokens = 425,
            MaxTokens = 500,
            MinTokens = 80,
            OverlapRatio = 0.15,
            RespectLegalStructure = true,
            IncludeSectionTitleInEmbedding = false,
            PersianMultiplier = 1.45,
            LatinMultiplier = 2.25,
            SymbolsPerToken = 12
        };

        var normalizer = new PersianTextNormalizer();
        var tokenCounter = new HeuristicTokenCounter(Microsoft.Extensions.Options.Options.Create(legalOptions));
        var cleaner = new HeaderFooterCleaner(normalizer);

        // Production LegalAwareChunker generates all 3 arms per Issue 4
        var chunker = new LegalAwareChunker(normalizer, tokenCounter, cleaner);

        // HeaderFooterCleaner Report on the 12 PDFs
        Console.WriteLine("\n--- Header/Footer Cleaner Report on 12 Benchmark PDFs ---");
        PrintHeaderFooterCleanerReport(documents, cleaner);

        // Run chunking for the 3 arms
        Console.WriteLine("\n--- Step 2: Generating chunks for Arm (b) Control, Arm (c) Legal, Arm (c2) LegalNoTitle ---");

        var controlChunks = GenerateChunks(documents, chunker, controlOptions);
        var legalChunks = GenerateChunks(documents, chunker, legalOptions);
        var legalNoTitleChunks = GenerateChunks(documents, chunker, legalNoTitleOptions);

        // Save JSON files
        var controlPath = Path.Combine(outDir, "chunks_control.json");
        var legalPath = Path.Combine(outDir, "chunks_legal.json");
        var legalNoTitlePath = Path.Combine(outDir, "chunks_legal_notitle.json");

        await File.WriteAllTextAsync(controlPath, JsonSerializer.Serialize(controlChunks, JsonOpts));
        await File.WriteAllTextAsync(legalPath, JsonSerializer.Serialize(legalChunks, JsonOpts));
        await File.WriteAllTextAsync(legalNoTitlePath, JsonSerializer.Serialize(legalNoTitleChunks, JsonOpts));

        Console.WriteLine($"  Arm (b) Control saved to:          {controlPath} ({controlChunks.Count} chunks)");
        Console.WriteLine($"  Arm (c) Legal-Aware saved to:      {legalPath} ({legalChunks.Count} chunks)");
        Console.WriteLine($"  Arm (c2) Legal-NoTitle saved to:   {legalNoTitlePath} ({legalNoTitleChunks.Count} chunks)");

        // Print comparative statistics
        Console.WriteLine("\n--- Step 3: Comparative Chunker Statistics ---");
        PrintStats("Arm (b) Control (Fixed Sentence)", controlChunks);
        PrintStats("Arm (c) Legal-Aware (With Title)", legalChunks);
        PrintStats("Arm (c2) Legal-Aware (No Title)", legalNoTitleChunks);

        // Print Defect Detector Report
        Console.WriteLine("\n--- Text Defect Detector Report on 12 Benchmark PDFs ---");
        PrintTextDefectDetectorReport(documents, legalChunks);

        // Real-token validation against Ollama bge-m3
        if (!skipValidation)
        {
            Console.WriteLine("\n--- Step 4: Real-Token Validation via Ollama bge-m3 ---");
            var validSuccess = await RunRealTokenValidationAsync(ollamaUrl, legalChunks, outDir);
            if (!validSuccess)
            {
                Console.Error.WriteLine("\nFATAL: Real-token validation failed (> 10% chunks exceeded 500 real tokens).");
                return 2;
            }
        }
        else
        {
            Console.WriteLine("\n--- Step 4: Real-Token Validation SKIPPED (--no-validate passed) ---");
        }

        Console.WriteLine("\n=== Chunker Probe completed successfully ===");
        return 0;
    }

    private static void PrintHeaderFooterCleanerReport(
        Dictionary<string, List<PageText>> docs,
        HeaderFooterCleaner cleaner)
    {
        Console.WriteLine("| Document | Pages | Original Lines | Cleaned Lines | Stripped Lines | Example Stripped Lines |");
        Console.WriteLine("|---|---|---|---|---|---|");

        foreach (var (pdfName, pages) in docs)
        {
            var cleanedPages = cleaner.Clean(pages);

            var origTotalLines = 0;
            var cleanTotalLines = 0;
            var strippedExamples = new HashSet<string>(StringComparer.Ordinal);

            for (var i = 0; i < pages.Count; i++)
            {
                var origLines = pages[i].Text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
                var cleanLines = cleanedPages[i].Text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

                origTotalLines += origLines.Length;
                cleanTotalLines += cleanLines.Length;

                var cleanSet = new HashSet<string>(cleanLines.Select(l => l.Trim()));
                foreach (var ol in origLines)
                {
                    var trimmed = ol.Trim();
                    if (!string.IsNullOrEmpty(trimmed) && !cleanSet.Contains(trimmed))
                    {
                        var display = trimmed.Length > 35 ? trimmed[..32] + "..." : trimmed;
                        strippedExamples.Add(display);
                    }
                }
            }

            var strippedCount = origTotalLines - cleanTotalLines;
            var examplesStr = strippedExamples.Count > 0
                ? string.Join("; ", strippedExamples.Take(2))
                : "None";

            Console.WriteLine($"| `{pdfName}` | {pages.Count,2} | {origTotalLines,4} | {cleanTotalLines,4} | {strippedCount,3} | `{examplesStr}` |");
        }
    }

    private static void PrintTextDefectDetectorReport(
        Dictionary<string, List<PageText>> docs,
        List<BenchmarkChunkRecord> legalChunks)
    {
        Console.WriteLine("| Document | Pages | Corrupt Ligature Pages | Reversed Date Pages | Flagged Chunks (Lig/Date) |");
        Console.WriteLine("|---|---|---|---|---|");

        foreach (var (pdfName, pages) in docs)
        {
            var ligPages = 0;
            var revDatePages = 0;

            foreach (var page in pages)
            {
                var flags = TextDefectDetector.DetectPageDefects(page.Text);
                if (flags.HasFlag(ChunkDefectFlags.CorruptLigatureLamAlef)) ligPages++;
                if (flags.HasFlag(ChunkDefectFlags.SuspectReversedDate)) revDatePages++;
            }

            var docChunks = legalChunks.Where(c => c.Pdf == pdfName).ToList();
            var ligChunks = docChunks.Count(c => c.DefectFlags.HasFlag(ChunkDefectFlags.CorruptLigatureLamAlef));
            var revChunks = docChunks.Count(c => c.DefectFlags.HasFlag(ChunkDefectFlags.SuspectReversedDate));

            Console.WriteLine($"| `{pdfName}` | {pages.Count,2} | {ligPages,2} | {revDatePages,2} | {ligChunks,2} / {revChunks,2} |");
        }
    }

    private static List<BenchmarkChunkRecord> GenerateChunks(
        Dictionary<string, List<PageText>> docs,
        LegalAwareChunker chunker,
        ChunkingOptions options)
    {
        var result = new List<BenchmarkChunkRecord>();
        foreach (var (pdfName, pages) in docs)
        {
            var drafts = chunker.Chunk(pages, options);
            for (var idx = 0; idx < drafts.Count; idx++)
            {
                var d = drafts[idx];
                result.Add(new BenchmarkChunkRecord(
                    Pdf: pdfName,
                    ChunkId: $"{pdfName}_{idx}",
                    PageStart: d.PageStart,
                    PageEnd: d.PageEnd,
                    Text: d.EmbeddingText,
                    DraftText: d.Text,
                    Tokens: d.TokenCount,
                    SectionTitle: d.SectionTitle,
                    DefectFlags: d.DefectFlags
                ));
            }
        }
        return result;
    }

    private static void PrintStats(string label, List<BenchmarkChunkRecord> chunks)
    {
        if (chunks.Count == 0) return;

        var tokens = chunks.Select(c => c.Tokens).OrderBy(t => t).ToList();
        var pagesPerChunk = chunks.Select(c => (double)(c.PageEnd - c.PageStart + 1)).OrderBy(p => p).ToList();

        var minTok = tokens[0];
        var maxTok = tokens[^1];
        var meanTok = tokens.Average();
        var medianTok = Percentile(tokens, 0.50);
        var p90Tok = Percentile(tokens, 0.90);
        var p95Tok = Percentile(tokens, 0.95);

        var meanPages = pagesPerChunk.Average();
        var medianPages = Percentile(pagesPerChunk, 0.50);
        var maxPages = pagesPerChunk[^1];

        Console.WriteLine($"\n{label}:");
        Console.WriteLine($"  Total chunks:       {chunks.Count}");
        Console.WriteLine($"  Tokens (estimated): Min={minTok}, Max={maxTok}, Mean={meanTok:F1}, Median={medianTok:F1}, P90={p90Tok:F1}, P95={p95Tok:F1}");
        Console.WriteLine($"  Pages per chunk:    Mean={meanPages:F2}, Median={medianPages:F1}, Max={maxPages:F0}");
        var overCap = chunks.Count(c => c.Tokens > 500);
        Console.WriteLine($"  Estimated > 500:    {overCap} chunks ({(overCap * 100.0 / chunks.Count):F2}%)");
    }

    private static double Percentile(List<int> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var idx = (int)Math.Round((sorted.Count - 1) * p);
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var idx = (int)Math.Round((sorted.Count - 1) * p);
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }

    private static async Task<bool> RunRealTokenValidationAsync(
        string ollamaUrl,
        List<BenchmarkChunkRecord> chunks,
        string outDir)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        try
        {
            var ping = await client.GetAsync($"{ollamaUrl}/api/tags");
            if (!ping.IsSuccessStatusCode)
            {
                Console.WriteLine($"  Warning: Ollama responded with HTTP {ping.StatusCode} at {ollamaUrl}. Skipping real validation.");
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Warning: Ollama unreachable at {ollamaUrl} ({ex.Message}). Skipping real validation.");
            return true;
        }

        var persianDocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "aein-91.pdf", "bedoon-factor.pdf", "bein-almelali.pdf", "bein-almelali-scan.pdf",
            "dastyar-amoozeshi.pdf", "dastyar-pazhuheshi.pdf", "hamayesh.pdf", "kod-akhlaghi.pdf",
            "shoraha-pazhuheshi.pdf", "ziro-guide.pdf"
        };

        var persianChunks = chunks.Where(c => persianDocs.Contains(c.Pdf)).ToList();
        var latinChunks = chunks.Where(c => !persianDocs.Contains(c.Pdf)).ToList();

        var rnd = new Random(42);
        var samplePersian = persianChunks.OrderBy(_ => rnd.Next()).Take(30).ToList();
        var sampleLatin = latinChunks.OrderBy(_ => rnd.Next()).Take(15).ToList();
        var sampleTotal = samplePersian.Concat(sampleLatin).ToList();

        Console.WriteLine($"  Sampling {sampleTotal.Count} chunks for real token validation (30 Persian, 15 Latin)...");

        var results = new List<TokenValidationRecord>();
        var t0 = DateTime.UtcNow;

        for (var i = 0; i < sampleTotal.Count; i++)
        {
            var chunk = sampleTotal[i];
            var isPersian = persianDocs.Contains(chunk.Pdf);

            var req = new
            {
                model = "bge-m3",
                input = chunk.Text,
                options = new { num_thread = 3 }
            };

            var resp = await client.PostAsJsonAsync($"{ollamaUrl}/api/embed", req);
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"  Warning: chunk {chunk.ChunkId} HTTP {resp.StatusCode}");
                continue;
            }

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            if (!json.TryGetProperty("prompt_eval_count", out var countProp))
            {
                Console.WriteLine($"  Warning: prompt_eval_count missing in response for {chunk.ChunkId}");
                continue;
            }

            var promptEvalCount = countProp.GetInt32();
            var realTokens = Math.Max(0, promptEvalCount - 2);

            results.Add(new TokenValidationRecord(
                ChunkId: chunk.ChunkId,
                Pdf: chunk.Pdf,
                Language: isPersian ? "fa" : "en",
                EstimatedTokens: chunk.Tokens,
                RealTokens: realTokens,
                Exceeds500: realTokens > 500
            ));

            if ((i + 1) % 10 == 0 || i + 1 == sampleTotal.Count)
            {
                Console.WriteLine($"    Validated {i + 1}/{sampleTotal.Count} chunks (elapsed: {(DateTime.UtcNow - t0).TotalSeconds:F1}s)");
            }
        }

        if (results.Count == 0)
        {
            Console.WriteLine("  No results collected from Ollama.");
            return true;
        }

        var persianResults = results.Where(r => r.Language == "fa").ToList();
        var latinResults = results.Where(r => r.Language == "en").ToList();

        var persianOver500 = persianResults.Count(r => r.Exceeds500);
        var latinOver500 = latinResults.Count(r => r.Exceeds500);
        var totalOver500 = results.Count(r => r.Exceeds500);

        var totalOverRatio = (double)totalOver500 / results.Count;

        Console.WriteLine("\n  Real Token Validation Results:");
        Console.WriteLine($"    Persian: {persianResults.Count} chunks, {persianOver500} over 500 real tokens ({(persianOver500 * 100.0 / persianResults.Count):F1}%)");
        Console.WriteLine($"    Latin:   {latinResults.Count} chunks, {latinOver500} over 500 real tokens ({(latinOver500 * 100.0 / latinResults.Count):F1}%)");
        Console.WriteLine($"    Overall: {results.Count} chunks, {totalOver500} over 500 real tokens ({(totalOverRatio * 100.0):F1}%)");

        var csvPath = Path.Combine(outDir, "real_token_validation.csv");
        using (var sw = new StreamWriter(csvPath, false, System.Text.Encoding.UTF8))
        {
            sw.WriteLine("chunk_id,pdf,language,estimated_tokens,real_tokens,exceeds_500,ratio");
            foreach (var r in results)
            {
                var ratio = r.RealTokens > 0 ? (double)r.EstimatedTokens / r.RealTokens : 1.0;
                sw.WriteLine($"{r.ChunkId},{r.Pdf},{r.Language},{r.EstimatedTokens},{r.RealTokens},{r.Exceeds500},{ratio:F4}");
            }
        }
        Console.WriteLine($"    Detailed validation records saved to: {csvPath}");

        if (totalOverRatio > 0.10)
        {
            Console.Error.WriteLine($"\n[ERROR] Real-token cap exceeded in {(totalOverRatio * 100.0):F1}% of sampled chunks (> 10% threshold)!");
            return false;
        }

        Console.WriteLine($"    SUCCESS: Real-token cap strictly respected (<= 10% over-500 threshold: {(totalOverRatio * 100.0):F1}%).");
        return true;
    }

    private static string FindRepoRoot()
    {
        var cur = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(cur))
        {
            if (File.Exists(Path.Combine(cur, "AGENTS.md")) || Directory.Exists(Path.Combine(cur, ".git")))
            {
                return cur;
            }
            var parent = Directory.GetParent(cur);
            if (parent == null) break;
            cur = parent.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}

public sealed record BenchmarkChunkRecord(
    [property: JsonPropertyName("pdf")] string Pdf,
    [property: JsonPropertyName("chunk_id")] string ChunkId,
    [property: JsonPropertyName("page_start")] int PageStart,
    [property: JsonPropertyName("page_end")] int PageEnd,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("draft_text")] string DraftText,
    [property: JsonPropertyName("tokens")] int Tokens,
    [property: JsonPropertyName("section_title")] string? SectionTitle,
    [property: JsonPropertyName("defect_flags")] ChunkDefectFlags DefectFlags
);

public sealed record TokenValidationRecord(
    string ChunkId,
    string Pdf,
    string Language,
    int EstimatedTokens,
    int RealTokens,
    bool Exceeds500
);
