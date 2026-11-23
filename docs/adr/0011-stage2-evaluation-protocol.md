# ADR 0011: Stage 2 End-to-End Evaluation Protocol, Offline Threshold Simulation, and Pre-Registered Generation Endpoints

- **Status:** Accepted
- **Date:** 2026-11-23
- **Tasks:** T19b (Extends T19a / ADR 0010)
- **Extends / Amends:** `docs/EVAL.md`, ADR 0010 (§7 Amendment)
- **Dataset Frozen Tag:** `eval-dataset-v2.2` (`eval/questions.json`, 138 questions across 14 digital PDFs + 1 synthetic scan twin)

---

## 1. Context & Motivation

Stage 1 established the declarative ingestion of multi-arm collections, benchmarked retrieval ranking (Recall@K, MRR), and calibrated per-arm similarity thresholds on the strict `(tuned, dev)` partition ($N=25$).

Stage 2 evaluates the end-to-end question answering pipeline (`POST /api/v1/ask`) with the complete RAG service, prompt builder, chat LLM, and citation parser. To maintain rigorous scientific validity, prevent data leakage, and ensure reproducible benchmark artifacts for the B.Sc. thesis, this ADR establishes the operational protocol for Stage 2.

---

## 2. Experimental Design & Multi-Arm Matrix

Stage 2 evaluates the five multi-arm benchmark configurations established in ADR 0010:

### 2.1 Core System Arms (Regime A: 14 Digital PDFs, 138 Questions)
1. **Arm 1 (`arm1_vector_ocr_off` — Baseline):**
   - Dense vector retrieval only (`hnsw` cosine distance via pgvector).
   - Ingestion extraction mode: `Extraction:OcrMode=Off`.
   - Server configuration: `Rag:UseHybrid=false`.
2. **Arm 2 (`arm2_hybrid_ocr_off`):**
   - Hybrid retrieval fusing Vector, PostgreSQL simple FTS (`ts_rank_cd`), and canonical `ArticleKeys` via RRF ($k=60$).
   - Ingestion extraction mode: `Extraction:OcrMode=Off`.
   - Server configuration: `Rag:UseHybrid=true`.
3. **Arm 3 (`arm3_hybrid_ocr_defective` — Full System):**
   - Hybrid retrieval fusing Vector, FTS, and `ArticleKeys` via RRF ($k=60$).
   - Ingestion extraction mode: `Extraction:OcrMode=Defective` (selective OCR on CMap-corrupted PDFs).
   - Server configuration: `Rag:UseHybrid=true`.

### 2.2 Isolated Scan Twin (Regime B: 1 Scanned PDF, 8 Gold Questions)
4. **`regime_b_scan_off`:**
   - Collection `eval-regime-b-scan-off` (0 text extracted; fails ingestion).
   - Evaluates short-circuit abstention when 0 chunks are retrieved.
5. **`regime_b_scan_defective`:**
   - Collection `eval-regime-b-scan-defective` (Tesseract `fas+eng` OCR on all 8 pages).
   - Evaluates end-to-end answer generation and citation validity from OCR text.
   - Ground truth mapping: `bein-almelali-scan.pdf -> bein-almelali.pdf`.

---

## 3. Two-Phase Server Lifecycle & Ungated Execution

### 3.1 Server-Level `UseHybrid` Lifecycle
Because `Rag:UseHybrid` is configured at the server level, Stage 2 is executed in two clean phases:
- **Phase A (`Rag__UseHybrid=false`, `Rag__MinSimilarity=0`):** Evaluates `arm1_vector_ocr_off`.
- **Phase B (`Rag__UseHybrid=true`, `Rag__MinSimilarity=0`):** Evaluates `arm2_hybrid_ocr_off`, `arm3_hybrid_ocr_defective`, `regime_b_scan_off`, and `regime_b_scan_defective`.
- **Merge Phase:** Merges Phase A and Phase B results offline to generate unified summary tables, reports, and comparative plots.

### 3.2 Pre-Ask Diagnostics via `POST /api/v1/dev/retrieve`
To capture exact retrieval state without relying on Stage 1 logs or modifying C# production endpoints, the harness invokes `POST /api/v1/dev/retrieve` immediately before each `/ask` call:
- Config: `Diagnostics:EnableEvalEndpoints=true` and explicit `topK=6` (matching `Rag:TopK`).
- Records: ordered `chunk_ids`, `bestVectorScore`, `hasFtsHit`, and `retrieved_chunk_count`.

### 3.3 Ungated Execution (`Rag__MinSimilarity=0`)
`RagService.cs` verifies that `MinSimilarity` only controls pre-LLM refusal gating; it does **not** filter or prune candidate chunks passed to the LLM prompt. By running with `Rag__MinSimilarity=0`, all candidate chunks enter the prompt, allowing 3 threshold operating points to be simulated offline without duplicate LLM calls:
1. `prod_055`: $\tau = 0.55$ (baseline production default).
2. `calibrated_tau`: $\tau = \tau^*$ (per-arm locked threshold from Stage 1: $0.70$ for Arm 1, $0.75$ for Arms 2 & 3).
3. `ungated`: $\tau = 0.0$ (raw LLM generation and abstention baseline).

---

## 4. Deterministic Content-Hash Cache & Non-Degraded Policy

### 4.1 Cache Key Specification
To prevent quota exhaustion and support idempotent resumes, LLM calls are cached in `eval/cache/llm_cache/`:
$$\text{key} = \text{SHA256}(\text{norm}(q) \parallel \text{chunk\_ids} \parallel \text{top\_k} \parallel \text{chat\_model} \parallel \text{temperature} \parallel \text{prompt\_sha256})$$
where:
- $\text{chunk\_ids}$ preserves the **exact ranked retrieval order** (no sorting).
- $\text{prompt\_sha256}$ is the SHA-256 digest of `src/DocSearch.Application/Prompts/system.v1.txt`.

### 4.2 Strict Non-Degraded Policy (`degraded == true` is Failure)
When the LLM provider fails, times out, or returns empty text, `RagService` returns HTTP 200 with `degraded=true, answer=null`.
- The harness treats `degraded == true`, HTTP 429, 5xx, or network errors as transient provider failures and retries up to 4 attempts ($2\text{s}, 6\text{s}, 18\text{s}, 45\text{s}$).
- If all 4 retries fail, the question is marked `failed_provider`:
  * It is **never** written to `eval/cache/llm_cache/`.
  * It is **never** written to the checkpoint `.jsonl` as completed.
  * It is **never** counted towards answered or refused totals.
  * The harness continues the run, reporting failed IDs in `report.md`.
- **Streak Halt Rule:** If 3 consecutive questions experience persistent provider failures (`consecutive_failures >= 3`), the harness halts execution immediately (`sys.exit(1)`) to protect quota and runtime.

---

## 5. Pre-Registered Stage 2 Endpoints

To maintain formal pre-registration integrity and avoid p-hacking:

### 5.1 Primary Endpoints (Out-of-Sample Trade-off Evaluation)

As documented in ADR 0010 (§7.3), raising the threshold $\tau$ from the baseline `0.55` to the calibrated $\tau^*$ ($0.70$ / $0.75$) increases rejection rigor: it suppresses false acceptances (lower FAR on Not-In-Docs queries) but incurs a higher false rejection rate (higher FRR on answerable queries). To quantify this operational trade-off without in-sample calibration bias:

1. **Primary Endpoint 2.1 (Answerable Abstention Trade-off):**
   - **Metric:** False Rejection Rate ($\text{FRR}$) on answerable questions in the **`heldout`** partition ($N=52$).
   - **Comparison:** Evaluates $\text{FRR}$ across `prod_055` ($\tau=0.55$), `calibrated_tau` ($\tau=\tau^*$), and `ungated` ($\tau=0.0$), with explicit decomposition into pre-LLM similarity abstention (`LowSimilarity`) versus post-LLM self-refusal (`ModelRefusal`).
   - **Objective:** Quantify the exact cost in lost answer availability on unseen regulations when raising $\tau$ to suppress out-of-domain false acceptances.

2. **Primary Endpoint 2.2 (Out-of-Domain Hallucination Prevention):**
   - **Metric:** False Acceptance Rate ($\text{FAR}$) on Not-In-Docs questions in the **`heldout`** partition ($N=12$).
   - **Comparison:** Evaluates $\text{FAR}$ across `prod_055`, `calibrated_tau`, and `ungated`.
   - **Objective:** Quantify whether higher $\tau^*$ significantly reduces out-of-domain hallucination risk on unseen documents, or whether the lexical FTS disjunction ($s < \tau \land \lnot \text{hasFtsHit}$) limits rejection effectiveness.

### 5.2 Pre-Registered Secondary Endpoint (Granular Extraction Under Font Corruption)
- **Cohort:** All answerable questions on **unbiased heldout CMap documents** (`tarh-dakheli.pdf` and `forsat-motaleati.pdf`, $N=27$).
- **Metrics:** Multi-Digit Number Recall ($N_{\text{num}}=7$) and Unit Phrase Recall ($N_{\text{unit}}=10$, both `strict` and `proximity_10`).
- **Operating Point:** Formally locked at the **`ungated` operating point** ($\tau=0.0$) to evaluate pure extraction fidelity without similarity gating bias. (`prod_055` evaluated as sensitivity check).
- **Comparison:** **Arm 3 (`arm3_hybrid_ocr_defective`) vs Arm 2 (`arm2_hybrid_ocr_off`)**.
- **Status:** **Descriptive only** per ADR 0010 §7.1 due to small cohort size ($N_{\text{num}}=7, N_{\text{unit}}=10$).

---

## 6. Statistical Inference & Small-$N$ Guardrails

1. **Wilson Score Intervals for Proportions:**
   - For all proportions (FAR, FRR, Answer Rate, Number Recall, Unit Recall) on small samples ($N=26, 12, 7, 10$), report **Wilson score 95% confidence intervals**.
2. **Paired Binary Comparisons:**
   - For paired arm comparisons on binary outcomes (e.g., whether Arm 3 answered or had a number hit vs Arm 2), use **McNemar's exact test** (two-sided binomial test on discordant pairs) or paired rate difference CIs instead of empirical bootstrap p-values.
3. **Harmonized Cluster Size Guard ($G < 5$):**
   - Per ADR 0010 §7.2, document-cluster bootstrap resamples require cluster count $G \ge 5$ to avoid discretization artifacts.
   - For $G = 3$ (`heldout_all`, `heldout_test`): Cluster bootstrap CI is marked as **descriptive-only** alongside explicit per-document breakdowns.
   - For $G \le 2$ (`heldout-CMap`): Cluster bootstrap is designated **degenerate** (`"N/A (G=2 clusters)"`), and only question-level CI plus per-document deltas are reported.
4. **Temperature 0 Rationale & Caveat:**
   - Evaluation is executed with `$env:Rag__Temperature="0"` to minimize generation variance and maximize answer reproducibility.
   - *Non-Determinism Caveat:* Frontier hosted LLM APIs (including Gemini) do not guarantee bitwise determinism even at $T=0$ due to mixture-of-experts load routing and floating-point non-associativity across GPU clusters. Minor surface variation across runs is expected and documented.

---

## 7. Metrics Definitions & Anti-Leakage Rules

1. **Anti-Leakage Partitioning for `calibrated_tau`:**
   - Headline metrics for `calibrated_tau` are reported **strictly out-of-sample** on `tuned|test` and `heldout`.
   - The calibration set `tuned|dev` ($N=25$) is reported separately, explicitly labeled as `(in-sample calibration)`.
2. **All 4 Citation Metrics:**
   - *Citation Validity Rate (answer-level):* Fraction of answered questions with $\ge 1$ syntactically valid `[n]` bracket citation in `answer` text.
   - *Citation Page-Hit Rate (answer-level):* Fraction of answered questions where at least one cited source index matches `gold_file` and intersects `gold_pages` (with twin mapping applied).
   - *Citation Precision (source-level):* $\frac{\sum \text{hits among cited sources}}{\sum \text{total cited sources}}$.
   - *Citation Recall (coverage-level):* $\frac{\sum \text{unique gold pages cited}}{\sum \text{total ground-truth gold pages}}$.
3. **Latency & Cache Separation:**
   - Latency percentiles ($p50, p95$) are computed **strictly on live (non-cached) API calls** (`is_cached == False`).
   - Cache hit rate is reported separately in `report.md`.

---

## 8. Reproducibility & Official Run Provenance

- **Official Run Guard:** Official benchmark runs strictly require `git_dirty == False`.
- If uncommitted changes exist, the run is stamped `"unofficial"` in `results.json` and `report.md`.
- Stored records contain: Git commit SHA, dataset hash, hardware/platform summary, chat model name, prompt hash, and UTC timestamp.
