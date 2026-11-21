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

---

## 7. Amendment — Post-Stage 1 Observation (2026-11-21)

Following the empirical completion of Stage 1 retrieval and calibration evaluation across all benchmark arms, this section records four formal methodological amendments prior to launching Stage 2 (`/ask` end-to-end evaluation).

### 7.1 Pre-Registration of Stage 2 Secondary Endpoint (Granular Extraction Under Font Corruption)
While Primary Endpoint 2 ($H_{0,2}$) pre-registers Retrieval MRR on `heldout-CMap` ($N=27$), retrieval ranking alone cannot evaluate whether character glyphs correctly propagate numbers and units into final generated answers.
- **Pre-Registered Stage 2 Secondary Endpoint:** **Multi-digit Number Recall and Unit Recall on `heldout-CMap`** (`tarh-dakheli.pdf` and `forsat-motaleati.pdf`), comparing **`Arm 3 (Hybrid + Selective OCR)`** vs **`Arm 2 (Hybrid + OCR Off)`**.
- **Non-Vacuous Denominators:** Per the T12 text-layer audit on the 27 `heldout-CMap` questions, the evaluation establishes two explicit target subsets:
  * **Multi-digit Numbers Subcohort:** $N_{\text{num}} = 7$ questions whose reference answers contain multi-digit numeric quantities ($\ge 2$ digits, such as percentage thresholds, budget limits, or article numbers).
  * **Unit-Phrase Subcohort:** $N_{\text{unit}} = 10$ questions whose reference answers contain coupled number-unit phrases (e.g., percentages, monetary amounts, months/years).
- **Exact Matching Rules:**
  * **Multi-digit Numbers ($\ge 2$ digits):** Evaluated via normalized substring contains-matching (mapping Arabic/Persian digits to ASCII and normalizing decimal/thousands separators).
  * **Unit-Phrase Matching:** Evaluated under two pre-registered definitions:
    1. *Strict Match:* Requires the exact consecutive `<number> <unit>` phrase in the generated answer text.
    2. *Proximity Match:* Allows a loose character window where `<number>` and `<unit>` co-occur within $\le 10$ characters of each other, providing sensitivity against minor surface-form phrasing differences.
- **Descriptive-Only Statistical Status:** With small sample sizes ($N_{\text{num}} = 7$ and $N_{\text{unit}} = 10$), bootstrap confidence intervals will necessarily be wide and cannot establish formal hypothesis-testing statistical significance ($p < 0.05$). Consequently, this secondary endpoint is pre-registered for **descriptive and diagnostic evaluation only**: results will be reported via exact counts, observed percentages, and descriptive confidence intervals, rather than claims of formal statistical significance.

### 7.2 Methodological Clarification: Document-Cluster Bootstrap Guard ($G < 5$)
In Stage 1 results, document-cluster paired bootstrap intervals were computed for all cohorts with $G \ge 3$. However, econometric and statistical literature demonstrates that cluster bootstrap resamples provide degenerate or unreliable variance estimates when the number of clusters $G$ is small ($G < 5$):
- For `heldout_all` ($G=3$) and `heldout_test` ($G=3$), the number of distinct cluster multisets is $\binom{3 + 3 - 1}{3} = \binom{5}{3} = 10$.
- With only 10 unique cluster configurations, the empirical bootstrap distribution is heavily discretized, yielding excessively wide intervals (e.g., `[-0.0978, 0.1153]`).
- **Formal Protocol Amendment:** Document-cluster confidence intervals with cluster count $G < 5$ are designated as **descriptive only**. They must **not** be cited as statistical significance evidence or hypothesis test verdicts in the B.Sc. thesis. For small-$G$ cohorts ($G < 5$), statistical inference relies primarily on question-level paired bootstrap CIs alongside explicit per-document metric breakdowns.

### 7.3 Calibration Plateau Boundary Limitation & Operational Error Analysis
Empirical threshold calibration on `tuned` dev questions ($N=25$, 18 answerable + 7 Not-In-Docs) revealed a fundamental interaction between PostgreSQL Full-Text Search (FTS) and vector similarity gating:
- **Plateau Grid Boundary Contact:** In `RagService.cs`, the rejection condition is:
  $$\text{Reject} \iff (s < \tau \land \lnot \text{hasFtsHit})$$
  When in-domain Not-In-Docs (NID) queries share vocabulary with the document corpus, `hasFtsHit` fires on unrelated lexical matches. Consequently, increasing $\tau$ cannot reject these queries because the FTS disjunction overrides the cosine gate. As a result, the optimal balanced-error plateau $\mathcal{P}_\epsilon$ extends to the upper sweep boundary ($0.80$).
- **Ill-Defined Center Rule at Boundary:** Because the plateau touches the arbitrary grid upper bound ($0.80$), the arithmetic midpoint of the plateau becomes an artifact of the grid truncation rather than an interior optimum.
- **Operational Unacceptability of Balanced-Error Minimization on Small $N$:** The unweighted balanced error objective $J(\tau) = (\text{FAR}(\tau) + \text{FRR}(\tau)) / 2$ severely penalized false acceptances on the 7 NID queries, forcing $\tau^*$ to high thresholds ($0.70$ for Arm 1, $0.75$ for Arms 2 & 3). At $\tau = 0.75$, the False Rejection Rate (FRR) on legitimate answerable queries climbed to operationally unacceptable levels ($22\%–61\%$), causing excessive abstentions on valid user requests.
- **Pure Cosine Audit:** For transparency, the harness logs the pure cosine midpoint sensitivity ($0.70$ for Arm 1, $0.70$ for Arm 2, $0.675$ for Arm 3).

### 7.4 Stage 2 Ungated Execution Protocol (Offline Multi-Threshold Evaluation)
To prevent wasteful duplicate LLM calls while rigorously evaluating threshold gating in Stage 2:
- **Ungated Execution (`Rag__MinSimilarity=0`):** Source code inspection of `src/DocSearch.Infrastructure/Rag/RagService.cs` verifies that `MinSimilarity` only controls pre-LLM refusal gating (`Answers.NoRelevantDocumentsFound`); it does **not** filter or prune the top-$k$ context chunks passed to the LLM prompt.
- **Evaluation Procedure:** The evaluation API instance runs with `Rag__MinSimilarity=0`. For every query, the harness logs the LLM response, along with `bestVectorScore`, `hasFtsHit`, and whether the query would have been refused.
- **Offline Thresholding:** Three distinct gating regimes are evaluated offline from a single LLM execution pass:
  1. **Production Default Gate:** $\tau = 0.55$ (the baseline system setting).
  2. **Calibrated Gate:** $\tau = \tau^*$ (the per-arm calibrated threshold).
  3. **Ungated Baseline:** $\tau = 0$ (`none`, evaluating raw LLM generation and grounding without refusal).
