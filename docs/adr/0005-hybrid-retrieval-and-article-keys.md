# 5. Hybrid Retrieval, Legal Reference Boost (ArticleKeys), and Reciprocal Rank Fusion

Date: 2026-11-06  
Status: Accepted  
Amends: ADR 0003, ADR 0004  
References: `docs/SPEC-PERSIAN-AND-RAG.md` §1, §6.1–§6.7; `docs/SPEC-DATA-AND-API.md` §4; `docs/TASKS.md` (T11)

---

## 1. Context & Motivation

In previous tasks (T06 / T06b), Persian legal-aware document chunking (`LegalAwareChunker`) was established to segment regulatory PDFs along structural boundaries (chapters, articles, notes). However, evaluation against the 63 benchmark questions revealed critical retrieval limitations under pure vector search:

1. **Merged Articles & Truncation (`q015` vs `aein-91.pdf`):**
   In Persian regulatory documents, short consecutive articles (`< MinTokens = 80`) are merged into a single chunk to preserve semantic coherence. For example, `aein-91.pdf_5` has `SectionTitle = "ماده5"`, but its body sequentially contains Article 5, Article 6, and Article 7 + Note 1. For question `q015` (*"تبصره ۱ ماده ۷ آیین‌نامه آموزشی کارشناسی چه می‌گوید؟"*), Article 7 starts at character 230 of `SearchText` and is entirely absent from `SectionTitle`. Pure dense vector search ranked this target chunk at **Rank 12** (`Hit@1 = False`, `Hit@5 = False`), failing to answer.
2. **Prose Cross-Reference Collisions:**
   Scanning regular expressions over the entire chunk text produces false-positive boosts on cross-references inside prose (e.g., *"مطابق ماده ۷ آیین‌نامه..."*). A chunk must only be identified by the articles it *defines* or *continues*, not incidental citations.
3. **Citation Score vs RRF Fusion Score Mismatch:**
   Under standard Reciprocal Rank Fusion (RRF), fused scores are reciprocal sums across active candidate lists:
   $$\text{RRF}(d) = \sum_{m \in M(d)} \frac{1}{k + r_m(d)} \approx 0.016 \dots 0.049$$
   Assigning `FusedScore` to `SourceCitation.Score` violated grounding threshold contracts (`RagOptions.MinSimilarity = 0.55`), causing `ParseAndValidate` and `RagService` to inappropriately filter valid sources. Frontend consumers expect cosine similarity ($1 - \text{distance} \approx 0.60 \dots 0.85$).
4. **False Positive Substring Matches in Token Coverage (`HasFtsHit`):**
   Using substring `.Contains(token)` caused false matches for single digits (e.g. `"7"` matching `"1397"` or `"27"`) and short Persian words inside other roots. Discrete word boundary tokenization and exact set intersection was required.

---

## 2. Decisions & Architectural Contracts

### 2.1 Domain & Schema Changes (`ArticleKeys`)
- **ChunkDraft Aggregation:** Extended `ChunkDraft` with an optional trailing parameter `IReadOnlyList<string>? ArticleKeys = null` (defaulting to empty). In `LegalAwareChunker.BuildChunkDraft`, canonical `unit.Article` values across all non-overlap `Units` of a chunk are aggregated and normalized to ASCII-digit canonical format (`"ماده N"`, `"ماده واحده"`, `"ماده N مکرر"`).
- **Parity Verification:** The union of `ArticleKeys` across all chunks of `aein-91.pdf` was verified to capture all 35 canonical articles (`"ماده 1"` through `"ماده 35"`). Chunks, `Text`, `SearchText`, and `EmbeddingText` SHA-256 hashes across all 201 benchmark chunks remained 100% identical to the reference `chunks_legal_t06b.json`.
- **Database Schema:** Added `public string[] ArticleKeys { get; set; } = [];` to `Domain.Chunk`. In `AppDbContext`, mapped to `article_keys text[] NOT NULL DEFAULT '{}'` with a PostgreSQL GIN index (`ix_chunks_article_keys`). Generated EF Core migration `AddChunkArticleKeys`.

### 2.2 Extraction & Query Building
- **`LegalReferenceExtractor`:** Pure class in `DocSearch.Application.Retrieval`. Extracts canonical article keys (`ماده\s*([0-9]+)(?:\s*مکرر)?` $\rightarrow$ `"ماده N"`, `ماده\s+واحده` $\rightarrow$ `"ماده واحده"`, `article\s+([0-9]+)` $\rightarrow$ `"article N"`) and optional note numbers (`تبصره\s*([0-9]+)` $\rightarrow$ `int`). Never boosts on a note alone when no article is extracted.
- **`TsQueryBuilder`:** Pure class in `DocSearch.Application.Retrieval`. Strips Persian/English stopwords, sanitizes tsquery syntax characters (`&`, `|`, `!`, `:`, `*`, `(`, `)`), preserves digit-only tokens, and handles attached/separated legal keywords symmetrically (`"ماده 7"` or `"ماده7"` $\rightarrow$ `'ماده' | '7' | 'ماده7'`). Provides `ExtractQueryTokens` and `TokenizeForCoverage` for discrete token intersection.

### 2.3 Persistence & Hybrid Search (`PgVectorStore`)
- Extended `IVectorStore` with `SearchHybridAsync(HybridSearchRequest request, CancellationToken ct)`.
- **Sequential SQL Execution:** Because Npgsql forbids concurrent queries on a single active connection, the Vector query, FTS query (`ts_rank_cd`), and dedicated Ref query (`article_keys && @refs`) are awaited sequentially in the same transaction after configuring `SET LOCAL hnsw.ef_search = 100; SET LOCAL hnsw.iterative_scan = strict_order;`.
- **Genuine Cosine Scores:** Computes genuine cosine similarity `(1.0 - (c.embedding <=> @q))` for FTS and Ref candidates. Computes `BestVectorScore` as the maximum cosine similarity across all returned candidate lists.

### 2.4 Retrieval, Fusion & Note Ordering (`Retriever`)
- **Note-Based Ordering:** When `ExtractedArticleKeys` and `NoteNumber` are present, `Retriever` partitions `RefCandidates` so chunks matching `تبصره\s*<N>(?![0-9])` in `SearchText` precede non-note chunks (both subgroups ordered by `VectorScore` desc, then `ChunkId` asc).
- **Legacy Fallback:** If `RefCandidates` is empty, scans Vector and FTS candidates for chunks with empty `ArticleKeys` whose `SectionTitle` or first 120 characters of `SearchText` match the target article regex.
- **Reciprocal Rank Fusion:** Fuses candidate lists using `RankFusion.Fuse` with standard unweighted Reciprocal Rank Fusion:
  $$\text{RRF}(d) = \sum_{m \in M(d)} \frac{1}{k + r_m(d)}$$
  with $k = 60$ across the active candidate lists ($M(d) \subseteq \{vec, fts, ref\}$). Ties are broken by `MaxVectorScore` descending, then `ChunkId` ascending.
- **Token Coverage (`HasFtsHit`):** Computes exact token intersection `queryTokens.Count(t => candidateTokens.Contains(t))`. Sets `HasFtsHit = true` if coverage $\ge 0.50$ (or $\ge 2$ tokens for long queries), or if `refList.Count > 0` and either the query has no non-legal terms (e.g., pure article/note lookups) or at least one non-legal query term matches a ref candidate chunk's search text (guarding against off-topic queries that happen to mention a valid article number).
- **Citation Score Contract Fix:** Updated both `CitationParser.ParseAndValidate` and `RagService.FormatSources` to assign `VectorScore` (cosine similarity) to `SourceCitation.Score` instead of `FusedScore`.

---

## 3. Evaluation Benchmark Results

The offline benchmark was executed over the 12 benchmark PDFs (201 chunks) and all 63 benchmark questions.
Sanity check with `UseHybrid = false` verified 100% exact parity with `embed_results_legal.csv` (`Recall@5 = 90.4%`, `MRR = 0.8282`).

### 3.1 Overall Retrieval Performance (52 Answerable Questions)

#### Loose Hit Rule (Any Page Overlap)
| Arm | Split | Q Count | Recall@1 | Recall@3 | Recall@5 | Recall@10 | MRR |
|---|---|---|---|---|---|---|---|
| **Arm (c) Legal Vector (T06b)** | dev / test / **all** | 16 / 36 / **52** | 81.2% / 72.2% / **75.0%** | 87.5% / 88.9% / **88.5%** | 93.8% / 88.9% / **90.4%** | 93.8% / 94.4% / **94.2%** | 0.8651 / 0.8118 / **0.8282** |
| **Arm (d) Hybrid RRF (T11)** | dev / test / **all** | 16 / 36 / **52** | 87.5% / 72.2% / **76.9%** | 93.8% / 97.2% / **96.2%** | 93.8% / 97.2% / **96.2%** | 100.0% / 97.2% / **98.1%** | 0.9141 / 0.8380 / **0.8614** |

- **Recall@3:** jumped from 88.5% to **96.2%** (+7.7 percentage points absolute, 4 gained vs 0 lost).
- **Recall@5:** jumped from 90.4% to **96.2%** (+5.8 percentage points absolute, 3 gained vs 0 lost).
- **MRR:** increased from 0.8282 to **0.8614** (+0.0332 absolute gain).
- **Paired Bootstrap 95% CI (5,000 iterations):**
  $$\text{Mean } \Delta\text{RR} = +0.0332, \quad 95\% \text{ CI} = [-0.0310, +0.1042]$$
  Hits gained at $k=3$: only Arm (d) = 4, only Arm (c) = 0.
  Hits gained at $k=5$: only Arm (d) = 3, only Arm (c) = 0.
  *Statistical Note:* While `Recall@3` (+7.7 pp) and `Recall@5` (+5.8 pp) exhibit a consistent directional improvement across both splits with zero regressions, the overall MRR difference ($\Delta\text{MRR} = +0.0332$, 95% bootstrap CI $[-0.0310, +0.1042]$) is an observed improvement that is not statistically significant at $\alpha = 0.05$ on $n = 52$ because the confidence interval crosses zero.

#### Strict Hit Rule (Page Overlap AND Chunk Span $\le 3$ Pages)
| Arm | Split | Q Count | Recall@1 | Recall@3 | Recall@5 | Recall@10 | Strict MRR |
|---|---|---|---|---|---|---|---|
| **Arm (c) Legal Vector (T06b)** | all | 52 | 75.0% | 86.5% | 88.5% | 92.3% | 0.8222 |
| **Arm (d) Hybrid RRF (T11)** | all | 52 | 76.9% | 94.2% | 94.2% | 96.2% | **0.8562** |

- **Paired Bootstrap 95% CI (Strict):**
  $$\text{Mean } \Delta\text{RR} = +0.0340, \quad 95\% \text{ CI} = [-0.0302, +0.1044]$$
  Similarly, under the strict rule the CI crosses zero, reflecting small-sample variance ($n=52$) despite monotonic recall gains.

---

### 3.2 Question-Type Breakdown

| Question Type | Count | Recall@1 (Legal) | Recall@1 (Hybrid) | Recall@5 (Legal) | Recall@5 (Hybrid) | MRR (Legal) | MRR (Hybrid) | Mean $\Delta$RR |
|---|---|---|---|---|---|---|---|---|
| `legal-reference` | 11 | 81.8% | 81.8% | 90.9% | **100.0%** | 0.8712 | **0.9091** | **+0.0379** |
| `english` | 12 | 41.7% | **66.7%** | 75.0% | **91.7%** | 0.6141 | **0.8021** | **+0.1880** |
| `numeric` | 13 | 92.3% | 84.6% | 100.0% | 100.0% | 0.9615 | 0.9231 | -0.0385 |
| `paraphrase` | 9 | 88.9% | 77.8% | 88.9% | 88.9% | 0.8921 | 0.8148 | -0.0772 |
| `multi-chunk` | 6 | 66.7% | 66.7% | 100.0% | 100.0% | 0.7639 | **0.8056** | **+0.0417** |
| `factual` | 1 | 100.0% | 100.0% | 100.0% | 100.0% | 1.0000 | 1.0000 | 0.0000 |

- **Legal-Reference Questions:** All 11 questions retrieved within Top-2 (`Recall@5 = 100.0%`). Paired bootstrap CI (n=11): mean $\Delta\text{RR} = +0.0379$, $95\% \text{ CI} = [-0.1364, +0.2500]$.
- **Non-Legal Questions (n=41):** Mean $\Delta\text{RR} = +0.0320$, $95\% \text{ CI} = [-0.0352, +0.1072]$.

---

### 3.3 Target Questions Rank Comparison

| ID | Split | Type | Vector Rank (Legal) | Hybrid Rank | Hit@1 (Hybrid) | Hit@5 (Hybrid) | Reciprocal Rank (Hybrid) | Top-1 Cosine Sim |
|---|---|---|---|---|---|---|---|---|
| **`q015`** | test | legal-reference | **12** | **1** | **True** | **True** | **1.0000** | 0.6315 |
| **`q016`** | test | legal-reference | **1** | **1** | **True** | **True** | **1.0000** | 0.6980 |
| **`q034`** | test | paraphrase | 35 | - | False | False | 0.0000 | 0.5930 |
| **`q050`** | dev | english | **2** | **1** | **True** | **True** | **1.0000** | 0.5975 |
| **`q055`** | test | english | **9** | **1** | **True** | **True** | **1.0000** | 0.5962 |
| **`q056`** | dev | english | **11** | **8** | False | False | **0.1250** | 0.6542 |

- For `q015`, the target chunk (`aein-91.pdf_5`), which merges Article 7 after Article 5 and 6, was extracted via `ArticleKeys` and boosted from **Rank 12 to Rank 1**.
- `q050` (English RAG memory) improved from Rank 2 to **Rank 1**.
- `q055` (English open-domain QA) improved from Rank 9 to **Rank 1**.
- `q056` (English M3 text encoder) improved from Rank 11 to **Rank 8**.

---

### 3.4 Dev Split Analysis & `HasFtsHit` Threshold Tuning

On the dev split (16 answerable questions, 4 not-in-docs questions):
- **Answerable Questions (n=16):**
  - Minimum cosine similarity: `0.5902` (all 16 exceed `RagOptions.MinSimilarity = 0.55`).
  - `HasFtsHit` evaluation: 16/16 (100%) exhibit lexical and reference token coverage $\ge 0.50$.
- **Not-in-Docs Questions (n=4):**
  - None match document article references or corpus lexical tokens, validating `HasFtsHit` as a reliable refusal guard against hallucination.

---

## 4. Known Limitations & Operational Requirements

1. **Same-Numbered Articles Across Documents:**
   In collections containing multiple regulatory PDFs (e.g. `aein-91.pdf` and another regulation both having `"ماده 12"`), `ArticleKeys && @refs` returns matching article chunks across all documents. Final disambiguation is achieved by RRF through vector cosine similarity and full-text keyword context terms.
2. **Database Re-Indexing Operational Requirement:**
   Applying the EF Core migration `AddChunkArticleKeys` defaults existing DB rows to `article_keys = '{}'`. Until existing collections are reprocessed with `LegalAwareChunker`, hybrid retrieval operates in legacy fallback mode (regex matching on `SectionTitle` and first 120 characters of `SearchText`).
