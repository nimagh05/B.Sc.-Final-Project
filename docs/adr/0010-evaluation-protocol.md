# ADR 0010: Scientific Multi-Arm Evaluation Protocol, Per-Arm Plateau Calibration, and Pre-Registered Primary Endpoints

- **Status:** Accepted
- **Date:** 2026-11-21
- **Tasks:** T19 (T19a / T19b)
- **Extends / Amends:** `docs/EVAL.md`, ADR 0005 (§3 Evaluation), ADR 0008 (§3 Anti-Leakage & OCR Protocol), ADR 0009 (§5 Empirical Verification)
- **Dataset Frozen Tag:** `eval-dataset-v2.2` (`eval/questions.json`, 138 questions: 112 answerable + 26 not-in-docs across 14 digital PDFs + 1 synthetic scan twin)

---

## 1. Context & Motivation

To support the B.Sc. thesis with reproducible scientific rigor, Task T19 establishes an automated multi-arm evaluation harness. Previous tasks established:
1. Legal-aware chunking and token counter calibration (T06 / ADR 0003).
2. Hybrid retrieval fusing dense pgvector embeddings, PostgreSQL FTS, and canonical `ArticleKeys` via Reciprocal Rank Fusion (T11 / ADR 0005).
3. Pre-chunking OCR triggering via sidecar for image-only scans and CMap font-corrupted digital PDFs (T12 / ADR 0009).
4. Frozen evaluation dataset schema v2.2 separating `tuned` (74 Qs) from `heldout` (64 Qs) and tagging text-layer defect mechanisms (ADR 0008).

A disciplined evaluation protocol must address four methodological requirements:
- **Zero Ingestion Contamination:** Chunking or OCR variants alter persistent vector store state. Arms sharing identical extraction and chunking must share collections, while distinct variants must run in isolated collections.
- **Anti-Leakage Calibration:** Hyperparameters (such as `Rag:MinSimilarity`) must be calibrated strictly on `(tuned, dev)` questions to prevent optimistic bias on test and held-out data.
- **Plateau Optimization on Small N:** On small calibration subsets ($N=25$), discrete single-point argmax selection is susceptible to boundary noise.
- **Hypothesis Pre-Registration:** Primary statistical endpoints must be locked a priori before evaluating held-out benchmarks to guard against p-hacking across multiple cohort breakdowns.

---

## 2. Experimental Design & Multi-Arm Matrix

### 2.1 Core System Arms (Regime A: 14 Digital PDFs, 138 Questions)
1. **Arm 1 (`arm1_vector_ocr_off` — Baseline):**
   - Dense vector retrieval only (`hnsw` cosine distance via pgvector).
   - Ingestion extraction mode: `Extraction:OcrMode=Off`.
2. **Arm 2 (`arm2_hybrid_ocr_off`):**
   - Hybrid retrieval fusing Vector, PostgreSQL simple FTS (`ts_rank_cd`), and canonical `ArticleKeys` via RRF ($k=60$).
   - Ingestion extraction mode: `Extraction:OcrMode=Off`.
   - **Shared Collection Architecture:** Because Arm 1 and Arm 2 share identical text extraction and chunking parameters, both arms query the **same collection** (`eval-regime-a-ocr-off`), toggling `useHybrid` dynamically via the gated evaluation endpoint. This eliminates ~20 minutes of redundant CPU embedding computation.
3. **Arm 3 (`arm3_hybrid_ocr_defective` — Full System):**
   - Hybrid retrieval fusing Vector, FTS, and `ArticleKeys` via RRF ($k=60$).
   - Ingestion extraction mode: `Extraction:OcrMode=Defective` (triggers OCR on 6 CMap-corrupted PDFs, preserves clean text on 8 digital PDFs).
   - Ingested in an isolated collection (`eval-regime-a-ocr-defective`).

### 2.2 Regime B: Isolated Scan Twin (`bein-almelali-scan.pdf`, 8 Gold Questions)
- Synthetic 150 DPI rasterized scan of `bein-almelali.pdf` with zero embedded digital text.
- **Collection Isolation:** Ingested in dedicated collections (`eval-regime-b-scan-off` and `eval-regime-b-scan-defective`), strictly isolated from Regime A to prevent SHA-256 duplicate rejection and retrieval cross-contamination.
- **Twin Mapping:** Evaluated against the 8 gold questions targeting `bein-almelali.pdf` (`q008, q009, q010, q020, q021, q029, q030, q037`) using an explicit evaluation harness twin mapping:
  `bein-almelali-scan.pdf -> bein-almelali.pdf`.
- Compares `OcrMode=Off` (0 text, document failed/empty) vs `OcrMode=Defective` (Tesseract `fas+eng` OCR).

---

## 3. Pre-Registered Primary Endpoints

To guard against multiple hypothesis testing inflation across sub-cohorts, **exactly two primary endpoints** are formally pre-registered:

1. **Primary Endpoint 1 (Hybrid Generalization):**
   - **Metric:** Mean Reciprocal Rank difference ($\Delta\text{MRR}$) between **`Arm 2 (Hybrid)`** and **`Arm 1 (Vector-Only)`**.
   - **Cohort:** All answerable questions in the **`heldout`** partition ($N=52$).
   - **Null Hypothesis ($H_0$):** $\mu(\Delta\text{MRR}) \le 0$ on unseen held-out regulations.
2. **Primary Endpoint 2 (Selective OCR Generalization):**
   - **Metric:** Mean Reciprocal Rank difference ($\Delta\text{MRR}$) between **`Arm 3 (Selective OCR)`** and **`Arm 2 (OCR Off)`**, with $\Delta\text{Recall@5}$ as secondary supporting metric.
   - **Cohort:** All answerable questions on **unbiased heldout CMap documents** (`tarh-dakheli.pdf` and `forsat-motaleati.pdf`, $N=27$).
   - **Null Hypothesis ($H_0$):** $\mu(\Delta\text{MRR}) \le 0$ on unseen font-corrupted documents.

*All other cohort breakdowns* (`tuned|dev`, `tuned|test`, question types, Secondary CMap union, Auto-CMap, Screenshot, Clean) are explicitly designated as **exploratory analysis**.

---

## 4. Per-Arm Plateau Threshold Calibration Protocol

### 4.1 Strict Anti-Leakage Partition
`Rag:MinSimilarity` is swept across $\tau \in [0.30, 0.80]$ with step $0.025$.
- Evaluation is restricted **strictly to `docset == "tuned"` and `split == "dev"`** ($N=25$: 18 answerable, 7 not-in-docs).
- A hard programmatic guard raises `ValueError` if any question with `docset != "tuned"` or `split != "dev"` enters calibration.

### 4.2 Per-Arm Independence
Because text-layer OCR alters token embeddings and hybrid fusion produces different similarity dynamics than pure vector search, calibration is conducted **independently per-arm**. Each arm locks its own $\tau_{\text{arm}}^*$.

### 4.3 Plateau Center Selection Rule
On $N=25$, a single question has discrete weight $\epsilon = \frac{1}{25} = 0.04$. Single-point argmax on noisy step functions causes threshold jitter.
- The objective minimizes balanced error:
  $$J(\tau) = \frac{\text{FAR}(\tau) + \text{FRR}(\tau)}{2}$$
  where $\text{FAR}(\tau) = \frac{\text{False Accepts}}{7}$ and $\text{FRR}(\tau) = \frac{\text{False Rejects}}{18}$ under the rejection condition $(s < \tau \land \lnot \text{hasFtsHit})$.
- Let $J^* = \min_{\tau} J(\tau)$.
- The optimal plateau is defined as:
  $$\mathcal{P}_{\epsilon} = \{ \tau \in [0.30, 0.80] \mid J(\tau) \le J^* + 0.04 \}$$
- The locked threshold is selected as the **center of the optimal plateau**:
  $$\tau^* = \frac{\min(\mathcal{P}_{\epsilon}) + \max(\mathcal{P}_{\epsilon})}{2}$$
  (rounded to the nearest discrete step of $0.025$).

---

## 5. Statistical Inference Protocol: Dual Bootstrap

For all pairwise arm comparisons ($B = 10,000$ resamples, deterministic seed `42`), the protocol reports two complementary bootstrap confidence intervals:

1. **Question-Level Paired Bootstrap:**
   - Resamples questions with replacement across the cohort.
   - Evaluates empirical 95% percentile confidence interval: $[\Delta_{2.5\%}, \Delta_{97.5\%}]$.
2. **Document-Cluster Paired Bootstrap:**
   - Questions targeting the same document share underlying text quality and font corruption artifacts, violating independence.
   - Clustered by `source_file`. Resamples *document clusters* with replacement.
   - **Cluster Size Guard:** Cluster bootstrap is methodologically valid only when cluster count $G \ge 3$ (`overall` $G=14$, `heldout` $G=3$, Primary CMap $G=6$). For cohorts with $G \le 2$ (specifically `heldout-CMap`, $G=2$ across `tarh-dakheli.pdf` and `forsat-motaleati.pdf`), the protocol reports `"N/A (G=2 clusters)"` alongside question-level CI and per-document deltas.

---

## 6. Implementation Architecture & Security Hygiene

1. **Gated Diagnostics Endpoint (`POST /api/v1/dev/retrieve`):**
   - Strictly gated behind `Diagnostics:EnableEvalEndpoints` (default `false` in all environments, including Development).
   - Enforces `[Authorize]` and collection read ACLs via `IAccessService`.
   - Returns raw `bestVectorScore` (cosine in $[0, 1]$), `hasFtsHit`, and candidate chunk rankings.
2. **Strict Provenance Manifest:**
   - Every benchmark output records Git commit SHA, dataset LF hash, Ollama model digest, and hardware summary.
3. **Repository Cleanliness:**
   - `eval/results/` and `eval/cache/` are added to `.gitignore`. Raw document text and evaluation dumps are strictly excluded from version control.
