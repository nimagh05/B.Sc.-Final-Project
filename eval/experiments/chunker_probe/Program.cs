using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Chunking;
using DocSearch.Application.Options;
using DocSearch.Application.Retrieval;
using DocSearch.Application.Text;
using Microsoft.Extensions.Options;

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

        if (args.Contains("--benchmark-hybrid"))
        {
            return await RunRetrievalBenchmarkAsync(repoRoot, outDir, useHybrid: true);
        }

        if (args.Contains("--benchmark-sanity"))
        {
            return await RunRetrievalBenchmarkAsync(repoRoot, outDir, useHybrid: false);
        }

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
                    DefectFlags: d.DefectFlags,
                    ArticleKeys: d.ArticleKeys
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
        using (var sw = new StreamWriter(csvPath, false, new System.Text.UTF8Encoding(false)))
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

    private static async Task<int> RunRetrievalBenchmarkAsync(string repoRoot, string outDir, bool useHybrid)
    {
        var tag = useHybrid ? "hybrid" : "sanity";
        Console.WriteLine($"\n=== Running Offline Retrieval Benchmark (Arm: {tag}, UseHybrid: {useHybrid}) ===");

        var chunksPath = Path.Combine(outDir, "chunks_legal.json");
        var cachePath = Path.Combine(outDir, "embed_cache.json");
        var questionsPath = Path.Combine(repoRoot, "eval", "questions.json");

        if (!File.Exists(chunksPath))
        {
            Console.Error.WriteLine($"Error: Chunks file not found at {chunksPath}");
            return 1;
        }

        if (!File.Exists(cachePath))
        {
            Console.Error.WriteLine($"Error: Embedding cache file not found at {cachePath}. Run python eval/experiments/export_embedding_cache.py first.");
            return 1;
        }

        if (!File.Exists(questionsPath))
        {
            Console.Error.WriteLine($"Error: Questions file not found at {questionsPath}");
            return 1;
        }

        Console.WriteLine($"Loading chunks from {chunksPath}...");
        var chunksJson = await File.ReadAllTextAsync(chunksPath);
        var rawChunks = JsonSerializer.Deserialize<List<BenchmarkChunkRecord>>(chunksJson, JsonOpts) ?? [];
        Console.WriteLine($"  Loaded {rawChunks.Count} chunks.");

        Console.WriteLine($"Loading embedding cache from {cachePath}...");
        var cacheJson = await File.ReadAllTextAsync(cachePath);
        var embedCache = JsonSerializer.Deserialize<Dictionary<string, float[]>>(cacheJson, JsonOpts) ?? [];
        Console.WriteLine($"  Loaded {embedCache.Count} embedding vectors.");

        Console.WriteLine($"Loading questions from {questionsPath}...");
        var questionsJson = await File.ReadAllTextAsync(questionsPath);
        var questions = JsonSerializer.Deserialize<List<BenchmarkQuestionRecord>>(questionsJson, JsonOpts) ?? [];
        Console.WriteLine($"  Loaded {questions.Count} questions.");

        var normalizer = new PersianTextNormalizer();

        // Prepare chunk data
        var chunkDataList = new List<BenchmarkChunkData>();
        for (var i = 0; i < rawChunks.Count; i++)
        {
            var c = rawChunks[i];
            var hash = ComputeSha256(c.Text);
            if (!embedCache.TryGetValue(hash, out var emb))
            {
                Console.Error.WriteLine($"Error: Missing embedding in cache for chunk {c.ChunkId} (hash {hash})");
                return 2;
            }

            var searchText = normalizer.NormalizeForSearch(c.DraftText);
            var discreteTokens = TsQueryBuilder.TokenizeForCoverage(searchText);
            var tokenFreq = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in discreteTokens)
            {
                tokenFreq[t] = tokenFreq.GetValueOrDefault(t, 0) + 1;
            }

            chunkDataList.Add(new BenchmarkChunkData
            {
                Index = i,
                ChunkId = Guid.NewGuid(),
                DocumentId = Guid.NewGuid(),
                FileName = c.Pdf,
                PageStart = c.PageStart,
                PageEnd = c.PageEnd,
                SectionTitle = c.SectionTitle,
                Text = c.DraftText,
                SearchText = searchText,
                ArticleKeys = c.ArticleKeys ?? [],
                Embedding = emb,
                DiscreteTokens = new HashSet<string>(discreteTokens, StringComparer.Ordinal),
                TokenFrequency = tokenFreq
            });
        }

        var collectionId = Guid.NewGuid();
        var vectorStore = new InMemoryBenchmarkVectorStore(chunkDataList);
        var queryEmbedder = new BenchmarkQueryEmbedder(embedCache, normalizer);

        // For sanity check (useHybrid=false), CandidateK is all chunks so 100% of ranks match embed_results_legal.csv
        var candidateK = useHybrid ? 30 : chunkDataList.Count;
        var topK = useHybrid ? 100 : chunkDataList.Count;

        var ragOptions = Microsoft.Extensions.Options.Options.Create(new RagOptions
        {
            UseHybrid = useHybrid,
            CandidateK = candidateK,
            TopK = topK,
            RrfK = 60
        });

        var aiOptions = Microsoft.Extensions.Options.Options.Create(new AiOptions
        {
            Embedding = new AiEmbeddingOptions { Model = "bge-m3" }
        });

        var retriever = new Retriever(queryEmbedder, vectorStore, ragOptions, aiOptions, normalizer);

        Console.WriteLine($"\n--- Running retrieval across {questions.Count} questions ---");
        var csvRows = new List<BenchmarkResultRow>();

        foreach (var q in questions)
        {
            var isAns = q.Type != "not-in-docs";
            var srcFile = q.SourceFile;
            var srcPages = new HashSet<int>(q.SourcePages ?? []);

            var res = await retriever.RetrieveAsync(q.Question, [collectionId], topK, CancellationToken.None);

            int? rankFirstHit = null;
            int? firstHitSpan = null;
            int? rankFirstHitStrict = null;

            for (var r = 0; r < res.Chunks.Count; r++)
            {
                var chunk = res.Chunks[r];
                if (isAns && chunk.FileName == srcFile)
                {
                    var chunkPages = Enumerable.Range(chunk.PageStart, chunk.PageEnd - chunk.PageStart + 1);
                    if (chunkPages.Any(srcPages.Contains))
                    {
                        var span = chunk.PageEnd - chunk.PageStart + 1;
                        if (!rankFirstHit.HasValue)
                        {
                            rankFirstHit = r + 1;
                            firstHitSpan = span;
                        }
                        if (!rankFirstHitStrict.HasValue && span <= 3)
                        {
                            rankFirstHitStrict = r + 1;
                        }
                        if (rankFirstHit.HasValue && rankFirstHitStrict.HasValue) break;
                    }
                }
            }

            var hit1 = isAns && rankFirstHit == 1;
            var hit3 = isAns && rankFirstHit.HasValue && rankFirstHit.Value <= 3;
            var hit5 = isAns && rankFirstHit.HasValue && rankFirstHit.Value <= 5;
            var hit10 = isAns && rankFirstHit.HasValue && rankFirstHit.Value <= 10;
            var rr = isAns && rankFirstHit.HasValue ? 1.0 / rankFirstHit.Value : 0.0;

            var top1Sim = res.BestVectorScore > 0 ? res.BestVectorScore : (res.Chunks.Count > 0 ? res.Chunks[0].VectorScore : 0.0);

            csvRows.Add(new BenchmarkResultRow(
                Id: q.Id,
                Split: q.Split,
                Type: q.Type,
                IsAnswerable: isAns,
                SourceFile: q.SourceFile ?? "",
                SourcePages: q.SourcePages ?? [],
                Top1Similarity: top1Sim,
                RankFirstHit: rankFirstHit ?? -1,
                FirstHitSpan: firstHitSpan ?? -1,
                RankFirstHitStrict: rankFirstHitStrict ?? -1,
                Hit1: hit1,
                Hit3: hit3,
                Hit5: hit5,
                Hit10: hit10,
                ReciprocalRank: rr
            ));
        }

        var csvFileName = useHybrid ? "embed_results_hybrid.csv" : "embed_results_sanity.csv";
        var csvPath = Path.Combine(outDir, csvFileName);

        using (var sw = new StreamWriter(csvPath, false, new System.Text.UTF8Encoding(false)))
        {
            sw.WriteLine("id,split,type,is_answerable,source_file,source_pages,top1_similarity,rank_first_hit,first_hit_span,rank_first_hit_strict,hit@1,hit@3,hit@5,hit@10,reciprocal_rank");
            foreach (var r in csvRows)
            {
                var pagesStr = "\"[" + string.Join(", ", r.SourcePages) + "]\"";
                sw.WriteLine(FormattableString.Invariant($"{r.Id},{r.Split},{r.Type},{r.IsAnswerable},{r.SourceFile},{pagesStr},{r.Top1Similarity:F4},{r.RankFirstHit},{r.FirstHitSpan},{r.RankFirstHitStrict},{r.Hit1},{r.Hit3},{r.Hit5},{r.Hit10},{r.ReciprocalRank:F4}"));
            }
        }
        Console.WriteLine($"Results saved to {csvPath}");

        PrintBenchmarkSummary(csvRows, tag, outDir);

        return 0;
    }

    private static void PrintBenchmarkSummary(List<BenchmarkResultRow> rows, string tag, string outDir)
    {
        var ansRows = rows.Where(r => r.IsAnswerable).ToList();
        var splits = new[] { "dev", "test", "all" };

        Console.WriteLine($"\n--- Retrieval Performance by Split (Answerable Questions: {ansRows.Count}) ---");
        Console.WriteLine("| Split | Q Count | Recall@1 | Recall@3 | Recall@5 | Recall@10 | MRR | Strict R@1 | Strict R@5 | Strict MRR |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|");

        foreach (var s in splits)
        {
            var splitRows = s == "all" ? ansRows : ansRows.Where(r => r.Split == s).ToList();
            if (splitRows.Count == 0) continue;

            var r1 = splitRows.Count(r => r.Hit1) * 100.0 / splitRows.Count;
            var r3 = splitRows.Count(r => r.Hit3) * 100.0 / splitRows.Count;
            var r5 = splitRows.Count(r => r.Hit5) * 100.0 / splitRows.Count;
            var r10 = splitRows.Count(r => r.Hit10) * 100.0 / splitRows.Count;
            var mrr = splitRows.Average(r => r.ReciprocalRank);

            var sr1 = splitRows.Count(r => r.RankFirstHitStrict == 1) * 100.0 / splitRows.Count;
            var sr5 = splitRows.Count(r => r.RankFirstHitStrict > 0 && r.RankFirstHitStrict <= 5) * 100.0 / splitRows.Count;
            var smrr = splitRows.Average(r => r.RankFirstHitStrict > 0 ? 1.0 / r.RankFirstHitStrict : 0.0);

            Console.WriteLine($"| **{s}** | {splitRows.Count,2} | {r1:F1}% | {r3:F1}% | {r5:F1}% | {r10:F1}% | {mrr:F4} | {sr1:F1}% | {sr5:F1}% | {smrr:F4} |");
        }

        Console.WriteLine("\n--- Target Questions Breakdown ---");
        Console.WriteLine("| ID | Split | Type | Rank | Strict Rank | Hit@1 | Hit@5 | RR | Top-1 Sim |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|---|");
        var targets = new[] { "q015", "q016", "q034", "q050", "q055", "q056" };
        foreach (var tid in targets)
        {
            var r = rows.FirstOrDefault(x => x.Id == tid);
            if (r != null)
            {
                Console.WriteLine($"| `{r.Id}` | {r.Split} | {r.Type} | {r.RankFirstHit,2} | {r.RankFirstHitStrict,2} | {r.Hit1,5} | {r.Hit5,5} | {r.ReciprocalRank:F4} | {r.Top1Similarity:F4} |");
            }
        }

        Console.WriteLine("\n--- Question Type Breakdown ---");
        Console.WriteLine("| Question Type | Count | Recall@1 | Recall@5 | MRR |");
        Console.WriteLine("|---|---|---|---|---|");
        var byType = ansRows.GroupBy(r => r.Type).OrderBy(g => g.Key);
        foreach (var g in byType)
        {
            var gList = g.ToList();
            var r1 = gList.Count(r => r.Hit1) * 100.0 / gList.Count;
            var r5 = gList.Count(r => r.Hit5) * 100.0 / gList.Count;
            var mrr = gList.Average(r => r.ReciprocalRank);
            Console.WriteLine($"| `{g.Key}` | {gList.Count,2} | {r1:F1}% | {r5:F1}% | {mrr:F4} |");
        }

        // If sanity check, compare row-by-row with embed_results_legal.csv
        if (tag == "sanity")
        {
            var legalCsv = Path.Combine(outDir, "embed_results_legal.csv");
            if (File.Exists(legalCsv))
            {
                var legalLines = File.ReadAllLines(legalCsv).Skip(1).ToList();
                var mismatches = 0;
                foreach (var line in legalLines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = Regex.Split(line, @",(?=(?:[^""]*""[^""]*"")*[^""]*$)");
                    var qid = parts[0];
                    var expectedRank = int.Parse(parts[7], CultureInfo.InvariantCulture);
                    var actualRow = rows.FirstOrDefault(r => r.Id == qid);
                    if (actualRow != null && actualRow.RankFirstHit != expectedRank)
                    {
                        Console.WriteLine($"  Sanity mismatch for {qid}: expected rank {expectedRank}, actual {actualRow.RankFirstHit}");
                        mismatches++;
                    }
                }
                if (mismatches == 0)
                {
                    Console.WriteLine("\n[SANITY CHECK PASSED] 100% parity with embed_results_legal.csv across all answerable questions!");
                }
                else
                {
                    Console.WriteLine($"\n[SANITY CHECK WARNING] Found {mismatches} rank differences with embed_results_legal.csv");
                }
            }
        }
    }

    private static string ComputeSha256(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hashBytes);
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        var dot = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }
        return dot;
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
    [property: JsonPropertyName("defect_flags")] ChunkDefectFlags DefectFlags,
    [property: JsonPropertyName("article_keys")] IReadOnlyList<string>? ArticleKeys = null
);

public sealed record TokenValidationRecord(
    string ChunkId,
    string Pdf,
    string Language,
    int EstimatedTokens,
    int RealTokens,
    bool Exceeds500
);

public sealed record BenchmarkQuestionRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("split")] string Split,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("source_file")] string? SourceFile,
    [property: JsonPropertyName("source_pages")] List<int>? SourcePages
);

public sealed record BenchmarkResultRow(
    string Id,
    string Split,
    string Type,
    bool IsAnswerable,
    string SourceFile,
    List<int> SourcePages,
    double Top1Similarity,
    int RankFirstHit,
    int FirstHitSpan,
    int RankFirstHitStrict,
    bool Hit1,
    bool Hit3,
    bool Hit5,
    bool Hit10,
    double ReciprocalRank
);

public sealed class BenchmarkChunkData
{
    public required int Index { get; init; }
    public required Guid ChunkId { get; init; }
    public required Guid DocumentId { get; init; }
    public required string FileName { get; init; }
    public required int PageStart { get; init; }
    public required int PageEnd { get; init; }
    public required string? SectionTitle { get; init; }
    public required string Text { get; init; }
    public required string SearchText { get; init; }
    public required IReadOnlyList<string> ArticleKeys { get; init; }
    public required float[] Embedding { get; init; }
    public required HashSet<string> DiscreteTokens { get; init; }
    public required Dictionary<string, int> TokenFrequency { get; init; }
}

public sealed class InMemoryBenchmarkVectorStore(List<BenchmarkChunkData> chunks) : IVectorStore
{
    public Task AddChunksAsync(Guid documentId, Guid collectionId, IReadOnlyList<ChunkRecord> chunks, CancellationToken ct) => Task.CompletedTask;

    public Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<ScoredChunk>> SearchAsync(SearchRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<HybridSearchResult> SearchHybridAsync(HybridSearchRequest request, CancellationToken ct)
    {
        // Vector candidates: scored by cosine similarity, tie-broken by original index
        var scoredList = chunks
            .Select(c => new
            {
                Chunk = c,
                VectorScore = (double)CosineSimilarity(request.QueryEmbedding, c.Embedding)
            })
            .OrderByDescending(x => x.VectorScore)
            .ThenBy(x => x.Chunk.Index)
            .ToList();

        var vectorCandidates = scoredList
            .Take(request.CandidateK)
            .Select(x => new CandidateChunk(
                ChunkId: x.Chunk.ChunkId,
                DocumentId: x.Chunk.DocumentId,
                FileName: x.Chunk.FileName,
                PageStart: x.Chunk.PageStart,
                PageEnd: x.Chunk.PageEnd,
                SectionTitle: x.Chunk.SectionTitle,
                Text: x.Chunk.Text,
                SearchText: x.Chunk.SearchText,
                ArticleKeys: x.Chunk.ArticleKeys,
                VectorScore: x.VectorScore,
                FtsScore: null
            ))
            .ToList();

        if (!request.UseHybrid)
        {
            var bestVec = vectorCandidates.Count > 0 ? vectorCandidates[0].VectorScore : 0.0;
            return Task.FromResult(new HybridSearchResult(vectorCandidates, [], [], bestVec));
        }

        // FTS candidates
        var ftsCandidates = new List<CandidateChunk>();
        if (!string.IsNullOrWhiteSpace(request.TsQuery))
        {
            var queryTokens = TsQueryBuilder.ExtractQueryTokens(request.QueryText);

            var scoredFts = chunks
                .Select(c =>
                {
                    var matchCount = queryTokens.Count(t => c.DiscreteTokens.Contains(t));
                    if (matchCount == 0) return null;

                    var tf = queryTokens.Sum(t => c.TokenFrequency.GetValueOrDefault(t, 0));
                    var score = ((double)matchCount / queryTokens.Count) * 10.0 + (double)tf / Math.Max(1, c.DiscreteTokens.Count);
                    var vscore = (double)CosineSimilarity(request.QueryEmbedding, c.Embedding);

                    return new
                    {
                        Chunk = c,
                        FtsScore = score,
                        VectorScore = vscore
                    };
                })
                .Where(x => x != null)
                .Select(x => x!)
                .OrderByDescending(x => x.FtsScore)
                .ThenBy(x => x.Chunk.Index)
                .Take(request.CandidateK)
                .Select(x => new CandidateChunk(
                    ChunkId: x.Chunk.ChunkId,
                    DocumentId: x.Chunk.DocumentId,
                    FileName: x.Chunk.FileName,
                    PageStart: x.Chunk.PageStart,
                    PageEnd: x.Chunk.PageEnd,
                    SectionTitle: x.Chunk.SectionTitle,
                    Text: x.Chunk.Text,
                    SearchText: x.Chunk.SearchText,
                    ArticleKeys: x.Chunk.ArticleKeys,
                    VectorScore: x.VectorScore,
                    FtsScore: x.FtsScore
                ))
                .ToList();

            ftsCandidates = scoredFts;
        }

        // Ref candidates: matched against extracted article keys, ordered by vscore desc, index asc
        var refCandidates = new List<CandidateChunk>();
        if (request.ExtractedArticleKeys.Count > 0)
        {
            var targetKeys = new HashSet<string>(request.ExtractedArticleKeys, StringComparer.Ordinal);
            refCandidates = chunks
                .Where(c => c.ArticleKeys.Any(targetKeys.Contains))
                .Select(c => new
                {
                    Chunk = c,
                    VectorScore = (double)CosineSimilarity(request.QueryEmbedding, c.Embedding)
                })
                .OrderByDescending(x => x.VectorScore)
                .ThenBy(x => x.Chunk.Index)
                .Take(request.CandidateK)
                .Select(x => new CandidateChunk(
                    ChunkId: x.Chunk.ChunkId,
                    DocumentId: x.Chunk.DocumentId,
                    FileName: x.Chunk.FileName,
                    PageStart: x.Chunk.PageStart,
                    PageEnd: x.Chunk.PageEnd,
                    SectionTitle: x.Chunk.SectionTitle,
                    Text: x.Chunk.Text,
                    SearchText: x.Chunk.SearchText,
                    ArticleKeys: x.Chunk.ArticleKeys,
                    VectorScore: x.VectorScore,
                    FtsScore: null
                ))
                .ToList();
        }

        var allCandidates = vectorCandidates.Concat(ftsCandidates).Concat(refCandidates).ToList();
        var bestOverall = allCandidates.Count > 0 ? allCandidates.Max(c => c.VectorScore) : 0.0;

        return Task.FromResult(new HybridSearchResult(vectorCandidates, ftsCandidates, refCandidates, bestOverall));
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        var dot = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }
        return dot;
    }
}

public sealed class BenchmarkQueryEmbedder(Dictionary<string, float[]> cache, PersianTextNormalizer normalizer) : IQueryEmbedder
{
    public Task<float[]> EmbedQueryAsync(string query, CancellationToken ct)
    {
        var norm = normalizer.NormalizeForEmbedding(query);
        var hash = ComputeSha256(norm);
        if (cache.TryGetValue(hash, out var vec))
        {
            return Task.FromResult(vec);
        }
        throw new InvalidOperationException($"Query embedding not found in cache for hash {hash}: {query}");
    }

    private static string ComputeSha256(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hashBytes);
    }
}
