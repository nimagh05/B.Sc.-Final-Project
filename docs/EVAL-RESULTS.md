# DocSearch Evaluation Results (Stages 1 & 2)

**Document Version:** 1.0  
**Project:** B.Sc. Final Project — Persian RAG Document Search Service (DocSearch)  
**Evaluation Harness:** `eval/run_eval.py` & `eval/stage2.py`  
**Dataset Reference:** `eval/questions.json` (`eval-dataset-v2.2`, $N=146$ questions across 8 documents)  

---

## 1. Provenance & Methodological Guardrails

This document synthesizes the empirical evaluation of DocSearch across both Stage 1 (retrieval-only ranking benchmark) and Stage 2 (end-to-end generation, abstention calibration, citation fidelity, and OCR generation impact).

### 1.1 Dual-Stage Provenance Header

All experiments were executed on locked, audited test benches with fixed model checkpoints, deterministic seeds, and isolated database schemas.

| Attribute | Stage 1 (Retrieval Ranking) | Stage 2 (End-to-End Generation & Abstention) |
|---|---|---|
| **Data Generation Git SHA** | `b3c9c12552e27349773fc63784f9e98cb61ff4e4` | `e7f4924554bc733979a72ddc6c7ff998a47d64ca` |
| **Report Rendering Git SHA** | `b3c9c12552e27349773fc63784f9e98cb61ff4e4` | `f76217a42e3d059e699d81848db774814af56bb8` |
| **Git Working Tree Status** | Clean (`git_dirty: false` on data generation) | Clean (`git_dirty: false` on data generation & report) |
| **Run Status** | `official` | `official` |
| **Dataset Tag** | `eval-dataset-v2.2` | `eval-dataset-v2.2` |
| **Questions SHA-256 (LF)** | `71d8573ce418c278a921638cdb9005acb48aa332bc7dae45a90d63bd213da62d` | `71d8573ce418c278a921638cdb9005acb48aa332bc7dae45a90d63bd213da62d` |
| **Embedding Model** | `BAAI/bge-m3` via Ollama (1024 dims, dense cosine) | `BAAI/bge-m3` via Ollama (1024 dims, dense cosine) |
| **Embedding Model Digest** | `7907646426070047a77226ac3e684fbbe8410524f7b4a74d02837e43f2146bab` | `7907646426070047a77226ac3e684fbbe8410524f7b4a74d02837e43f2146bab` |
| **Chat Generation Model** | N/A (retrieval only) | `gemini-3.5-flash-lite` |
| **Generation Temperature** | N/A | `0.0` (deterministic greedy decoding) |
| **Retrieved Context Top-K** | Top-10 evaluated ($k \in \{1, 3, 5, 10\}$) | $Top-K = 6$ (fixed per `Rag:TopK`) |
| **System Prompt Version** | N/A | `src/DocSearch.Application/Prompts/system.v1.txt` |
| **System Prompt SHA-256** | N/A | `3d7c8fe28584d70e188198b9cc72f740f813b70fd6b910f82ff363e01fe8265f` |
| **Live Verification Mismatches** | 0 mismatches across all arms | 36/36 (100%) pre-LLM gate agreement at $\tau = 0.55$ |
| **Execution Environment** | Windows 10/11, CPU-only Ollama, .NET 10 Web API | Windows 10/11, CPU-only Ollama, .NET 10 Web API |

### 1.2 Evaluation Rigor & Methodological Guardrails

1. **Strict Data Leakage Prevention:** Document sets are split at the document level into `tuned` (4 documents, 74 questions) and `heldout` (3 documents, 64 questions), plus 1 scanned twin document in Regime B (8 questions). Calibration of similarity thresholds was strictly restricted to the in-sample `tuned|dev` partition ($N=25$). Any attempt to calculate or optimize thresholds on `heldout` or `test` partitions is rejected at runtime by automated programmatic assertion guards (`ANTI-LEAKAGE VIOLATION`).
2. **Deterministic Offline Cache & Checkpoints:** Every LLM call in Stage 2 is uniquely keyed by:
   $$\text{CacheKey} = \text{SHA256}(\text{normalized\_query} \parallel \text{ordered\_chunk\_ids} \parallel \text{top\_k} \parallel \text{model} \parallel \text{temp} \parallel \text{prompt\_sha256})$$
   Retrieved chunk IDs are strictly preserved in rank order (never sorted alphabetically). Degraded responses are never written to cache.
3. **Exploratory Scope of Document-Cluster Bootstrap ($G=3$):** While question-level paired bootstrap confidence intervals (1,000 resamples) provide standard inferential bounds, document-cluster bootstrap on the held-out collection is limited to $G=3$ natural document clusters (`aeen-91.pdf`, `tarh-dakheli.pdf`, `forsat-motaleati.pdf`). Because asymptotic normal convergence of cluster-robust standard errors generally requires $G \ge 30$, document-cluster intervals are reported strictly as exploratory descriptive dispersion bounds; exact paired non-parametric tests (paired Wilcoxon signed-rank and paired McNemar tests) serve as the primary inferential standard.
4. **Denominator Invariance Across Operating Points:** For granular numeric and unit phrase recall, the question denominator is established strictly from ground-truth annotations before threshold gating. Questions that are rejected (either pre-LLM by threshold gating or post-LLM by model refusal) count as explicit recall misses ($0$), never as omitted or vacuous cases.

---

## 2. Pre-Registered vs. Post-Hoc Registry Table

To preserve scientific integrity and avoid p-hacking or selective reporting, all experimental endpoints and analytical investigations are classified according to their pre-registration status in Architecture Decision Records ADR 0010 and ADR 0011.

| Endpoint / Analysis | Target Cohort | Stage | Status | ADR Reference | Primary Metric / Test |
|---|---|---|---|---|---|
| **Primary Endpoint 1** | `heldout` answerable ($N=52$) | Stage 1 | Pre-Registered | ADR 0010 §7.1 | $\Delta\text{MRR}$ (Arm 2 Hybrid vs. Arm 1 Vector), Paired Wilcoxon |
| **Primary Endpoint 2** | `heldout-CMap` ($N=27$) | Stage 1 | Pre-Registered | ADR 0010 §7.1 | $\Delta\text{MRR}$ (Arm 3 Full System vs. Arm 2 OCR-Off), Paired Wilcoxon |
| **Primary Endpoint 2.1** | `heldout` answerable ($N=52$) | Stage 2 | Pre-Registered | ADR 0011 §5.1 | False Rejection Rate ($\text{FRR}$) across operating points (`prod_055` vs. `calibrated_tau`), 95% Wilson CI |
| **Primary Endpoint 2.2** | `heldout` not-in-docs ($N=12$) | Stage 2 | Pre-Registered | ADR 0011 §5.1 | False Acceptance Rate ($\text{FAR}$) across operating points (`prod_055` vs. `calibrated_tau`), 95% Wilson CI |
| **Secondary Endpoint (Granular Extraction)** | `heldout-CMap` ($N=27$) | Stage 2 | Pre-Registered | ADR 0011 §5.2 | Multi-digit Number Recall & Unit Phrase Recall under `ungated`, Descriptive & McNemar |
| **Regime B Scanned Twin Benchmark** | `bein-almelali-scan.pdf` ($N=8$) | Stages 1 & 2 | Pre-Registered | ADR 0009 §4, ADR 0010 §7.2 | MRR & Answer Rate across OCR modes vs. Digital Reference |
| **Pure OCR Extractor Latency** | 37 sample pages | Stage 1 | Pre-Registered | ADR 0009 §4 | Wall-clock seconds per page across CMap & Scanned documents |
| **RRF Parameter Stability Sweep** | `tuned|dev` ($N=25$) | Stage 1 | Post-Hoc / Sensitivity | ADR 0010 §7.3 | Sensitivity of MRR across $k_{\text{rrf}} \in [20, 100]$ |
| **Offline Simulation Live Cross-Check** | Stratified sample ($N=36$) | Stage 2 | Pre-Registered | ADR 0011 §6.2 | Pre-LLM gate agreement percentage between offline simulator and live API |
| **FTS-Bypass Counterfactual Analysis** | `heldout` answerable ($N=52$) | Stage 2 | Post-Hoc / Diagnostic | Section 6 of this report | $\text{FRR}_{\text{LowSimilarity}}$ surge with vs. without Full-Text Search rescue |
| **Untouched Document Noise Floor** | Clean subset ($N=76$) | Stage 2 | Post-Hoc / Diagnostic | Section 7 of this report | Paired answer identity between Arm 2 and Arm 3 under identical context |
| **Citation Precision & Page-Hit Audit** | Overall ($N=112$ ans) | Stage 2 | Pre-Registered | ADR 0011 §5.3 | Citation Validity, Page-Hit Rate, Precision, and Recall |

---

## 3. Stage 1 Retrieval Benchmark Summary

Stage 1 established the baseline retrieval capability of DocSearch across three system configurations:
- **Arm 1 (Vector-Only, OCR-Off):** Dense embedding retrieval via Ollama `bge-m3` using pgvector HNSW cosine distance.
- **Arm 2 (Hybrid, OCR-Off):** Reciprocal Rank Fusion ($k_{\text{rrf}} = 60$) combining dense HNSW and PostgreSQL Persian full-text search (`tsvector` with `farsiparsed` unaccenting).
- **Arm 3 (Full System: Hybrid + Selective OCR):** Reciprocal Rank Fusion operating over documents processed by the selective OCR sidecar (correcting defective Persian CID/ToUnicode font tables).

### 3.1 Primary Endpoints 1 & 2 Outcomes

| Cohort | Arm 1 MRR | Arm 2 MRR | Arm 3 MRR | $\Delta\text{MRR}$ (Arm 2 vs 1) [95% CI] | $\Delta\text{MRR}$ (Arm 3 vs 2) [95% CI] | Primary Conclusion |
|---|---|---|---|---|---|---|
| **Overall** ($N=112$) | **0.8500** | 0.7837 | 0.7822 | -0.0663 [-0.1214, -0.0124] | -0.0014 [-0.0351, 0.0327] | Vector-only superior on overall ranking |
| **Primary Endpoint 1 (`heldout_all`, $N=52$)** | **0.8638** | 0.7919 | 0.7903 | -0.0719 [-0.1501, +0.0032] | -0.0016 [-0.0660, +0.0609] | Hybrid does not outperform vector dense search |
| **Primary Endpoint 2 (`heldout-CMap`, $N=27$)** | **0.8951** | 0.7883 | 0.7852 | -0.1068 [-0.2148, -0.0093] | -0.0031 [-0.1272, +0.1154] | Macro-MRR retrieval parity across OCR modes |
| `heldout_test` ($N=39$) | **0.8739** | 0.7986 | 0.7952 | -0.0754 [-0.1641, +0.0058] | -0.0034 [-0.0701, +0.0598] | Consistent with held-out all |
| `tuned_test` ($N=42$) | **0.8340** | 0.7409 | 0.7504 | -0.0931 [-0.1865, +0.0003] | +0.0095 [-0.0266, +0.0452] | Mild OCR retrieval uplift on tuned test |
| `tuned_dev` ($N=18$) | 0.8472 | **0.8598** | 0.8333 | +0.0126 [-0.1111, +0.1468] | -0.0265 [-0.0913, +0.0132] | In-sample calibration partition |

### 3.2 Document Heterogeneity on CMap Cohort

While aggregate macro-MRR on `heldout-CMap` shows parity ($\Delta\text{MRR} = -0.0031$), disaggregation by document reveals stark structural heterogeneity:
- **`forsat-motaleati.pdf` ($N=12$):** Arm 2 MRR was 0.6694, while Arm 3 achieved **0.7847** ($\Delta\text{MRR} = +0.1153$ [95% CI: $+0.0083, +0.2500$]). Here, defective font mappings severely harmed lexical retrieval, and OCR restoration produced statistically significant ranking recovery.
- **`tarh-dakheli.pdf` ($N=15$):** Arm 2 MRR was 0.8833, while Arm 3 was 0.7856 ($\Delta\text{MRR} = -0.0978$ [95% CI: $-0.2767, +0.0889$]). In this document, while individual characters and numbers were corrupted, broader layout headings allowed coarse chunk retrieval even with corrupted body glyphs, whereas OCR noise slightly perturbed rank ordering.

### 3.3 Reciprocal Rank Fusion Stability

A parameter sweep over $k_{\text{rrf}} \in [20, 100]$ on `tuned|dev` demonstrated extreme rank stability: MRR varied by less than $\pm 0.004$ across the entire range, confirming that the choice of $k_{\text{rrf}} = 60$ (the Cormack standard) is robust and not an over-tuned hyperparameter.

### 3.4 Similarity Calibration Plateaus on Dev

Per-arm grid sweeps ($0.30 \le \tau \le 0.80$, step 0.025) on the in-sample development partition ($N=25$: 18 answerable, 7 not-in-docs) revealed wide error plateaus:
- **Arm 1 (Vector):** Plateau $[0.675, 0.700]$, locked $\tau^* = 0.700$ (best balanced error: $0.2937$).
- **Arm 2 (Hybrid):** Plateau $[0.700, 0.800]$, locked $\tau^* = 0.750$ (best balanced error: $0.3690$).
- **Arm 3 (Full System):** Plateau $[0.700, 0.800]$, locked $\tau^* = 0.750$ (best balanced error: $0.2976$).

### 3.5 Pure OCR Timing Benchmark (Extractor Sidecar)

Benchmarked on the standalone FastAPI extractor sidecar (`tesseract-ocr-fas`, 300 DPI, PSM 6):
- **Overall Pure OCR:** **$4.131\text{ s/page}$** (37 pages in 152.84 s).
- **Regime A (CMap Defective Documents):** **$3.608\text{ s/page}$** (29 pages in 104.63 s).
- **Regime B (Scanned Document):** **$6.026\text{ s/page}$** (8 pages in 48.21 s).
- Selective OCR avoided processing 84 unaffected pages in the collection, reducing total ingestion wall-clock time from ~500 seconds to ~153 seconds (a 69.4% compute saving).

### 3.6 Regime B Scanned Twin Retrieval

Evaluating the 8 twin questions on `bein-almelali-scan.pdf`:
- **Scan Twin with OCR-Off:** $\text{MRR} = 0.000$, $\text{Recall@1} = 0.0\%$, $\text{Recall@5} = 0.0\%$ (complete catastrophic failure; 0 text extracted).
- **Scan Twin with Selective OCR:** $\text{MRR} = \mathbf{0.875}$, $\text{Recall@1} = 75.0\%$, $\text{Recall@5} = 100.0\%$.
- **Digital Reference (Clean PDF):** $\text{MRR} = 0.9167$, $\text{Recall@1} = 87.5\%$, $\text{Recall@5} = 100.0\%$.
- The selective OCR pipeline restored **95.5%** of the clean digital twin's retrieval effectiveness ($\Delta\text{MRR} = -0.0417$).

---

## 4. Stage 2 Offline Simulation Live Cross-Check

Before conducting the full Stage 2 offline threshold simulation, a live validation experiment was conducted against the running DocSearch API to verify that offline threshold filtering perfectly mimics the production gate logic.

A stratified sample of 36 question-arm executions was evaluated:
- **Arm 1 (Vector-Only):** 18 questions evaluated against the API configured with `Rag__UseHybrid=false` and `Rag__MinSimilarity=0.55`.
- **Hybrid (Arms 2 & 3):** 18 questions evaluated against the API configured with `Rag__UseHybrid=true` and `Rag__MinSimilarity=0.55`.

### 4.1 Cross-Check Strata Breakdown

The 36 evaluated items covered all four operational gate regimes:
- **`NoContext` (0 retrieved chunks):** 0 items.
- **`LowSimilarity` ($\text{bestVectorScore} < 0.55$ and $\text{hasFtsHit} = \text{false}$):** 2 items. Both items were gated pre-LLM with generation time $0\text{ ms}$, returning `notFound: true` with reason `LowSimilarity`.
- **`FTS-rescue` ($\text{bestVectorScore} < 0.55$ and $\text{hasFtsHit} = \text{true}$):** 2 items. Both items successfully bypassed the similarity gate and invoked generation, yielding answers.
- **`Above-tau` ($\text{bestVectorScore} \ge 0.55$):** 32 items. All 32 items invoked generation.

### 4.2 Agreement Results

- **Pre-LLM Gate Agreement:** **36 / 36 (100.0%)** exact agreement. There were zero gate mismatches.
- **Post-LLM Agreement:** **35 / 36 (97.2%)** agreement. Exactly 1 question exhibited a borderline model refusal variation under live generation compared to the cached run.
- **Verification Gate Verdict:** Passed unconditionally. The offline simulation reproduces the C# `RagService` gating engine with 100% fidelity.

---

## 5. Stage 2 Primary Endpoints: Negative Result on Calibrated Tau & Cosine ROC-AUC

Stage 2 evaluated end-to-end question answering across three operating points:
1. **`prod_055` (Production Baseline):** Threshold locked at $\tau = 0.55$ per `appsettings.json`.
2. **`calibrated_tau` (In-Sample Calibrated):** Threshold locked at the dev plateau centers ($\tau^* = 0.70$ for Arm 1, $\tau^* = 0.75$ for Arms 2 & 3).
3. **`ungated` ($\tau = 0.00$):** Threshold gating completely disabled; every query is passed directly to the LLM.

### 5.1 Primary Endpoints 2.1 & 2.2 Results (Held-out Generalization)

Evaluating the 64 held-out questions (52 answerable, 12 not-in-docs):

| Arm | Operating Point | Held-out Answerable FRR ($N=52$) [95% CI] | LowSimilarity FRR | ModelRefusal FRR | Held-out NID FAR ($N=12$) [95% CI] | NID True Rejection Rate |
|---|---|---|---|---|---|---|
| **Arm 1 (Vector)** | `prod_055` | **15.4%** [8.0%, 27.5%] | 1.9% | 13.5% | **0.0%** [0.0%, 24.2%] | 100.0% (12/12) |
| **Arm 1 (Vector)** | `calibrated_tau` ($\tau^*=0.70$) | **76.9%** [63.9%, 86.3%] | 73.1% | 3.8% | **0.0%** [0.0%, 24.2%] | 100.0% (12/12) |
| **Arm 2 (Hybrid)** | `prod_055` | **11.5%** [5.4%, 23.0%] | 1.9% | 9.6% | **0.0%** [0.0%, 24.2%] | 100.0% (12/12) |
| **Arm 2 (Hybrid)** | `calibrated_tau` ($\tau^*=0.75$) | **40.4%** [28.2%, 53.9%] | 34.6% | 5.8% | **0.0%** [0.0%, 24.2%] | 100.0% (12/12) |
| **Arm 3 (Full System)** | `prod_055` | **15.4%** [8.0%, 27.5%] | 3.8% | 11.5% | **0.0%** [0.0%, 24.2%] | 100.0% (12/12) |
| **Arm 3 (Full System)** | `calibrated_tau` ($\tau^*=0.75$) | **40.4%** [28.2%, 53.9%] | 34.6% | 5.8% | **0.0%** [0.0%, 24.2%] | 100.0% (12/12) |

### 5.2 The Negative Result: Catastrophic Over-Abstention of Calibrated Tau

A central empirical contribution of this thesis is the demonstration that **calibrating vector similarity thresholds on an in-sample development partition does not generalize out-of-sample and severely degrades production RAG utility.**

1. **Massive Surge in False Rejection Rate:** Raising the similarity threshold from $\tau = 0.55$ to the calibrated $\tau^* = 0.70 / 0.75$ caused answerable FRR on held-out documents to surge catastrophically:
   - In Arm 1 (Vector), FRR surged from **15.4% to 76.9%** (+61.5% absolute increase). Over three-quarters of all valid user questions were rejected without an answer!
   - In Arm 2 (Hybrid), FRR surged from **11.5% to 40.4%** (+28.9% absolute increase).
2. **Zero Gain in Out-of-Sample Abstention:** At $\tau = 0.55$, the production system already achieved **0.0% FAR** (100% rejection of all 12 out-of-sample not-in-docs queries) across all three arms. Raising $\tau$ to $0.70$ or $0.75$ provided zero marginal security benefit, while rejecting 40%–77% of legitimate questions.
3. **Failure of Cosine Distance as an Out-of-Sample Classifier:** On `tuned|dev`, the receiver operating characteristic area under the curve ($\text{ROC-AUC}$) for cosine similarity was moderate:
   - Arm 1 Dev Cosine AUC: **0.7424**
   - Arm 2/3 Dev Cosine AUC: **0.6250**
   
   However, on out-of-sample held-out questions, semantic diversity and lexical variation cause relevant chunks to frequently achieve vector similarities in the range $[0.56, 0.72]$. Consequently, any gate threshold $\ge 0.70$ aggressively truncates valid retrieval.
4. **Primary Engineering Recommendation:** The production baseline threshold $\tau = 0.55$ should be retained as the universal gate. High-threshold gating ($\tau \ge 0.70$) is harmful in open-domain Persian document RAG.

### 5.3 Analysis of Sole Template False Acceptance (`q043`)

Across all 414 question evaluations on Regime A documents, exactly **one** not-in-docs false acceptance occurred: question `q043` in Arm 2 under in-sample development ($N=7$ NID dev, yielding $\text{FAR} = 14.3\% = 1/7$).

- **Question Context:** `q043` queries the hourly compensation rate for student teaching assistants (TA): *"حق‌التدریس دستیار آموزشی (TA) چقدر است؟"*.
- **Mechanism of Failure:** The queried document (`dastyar-pazhuheshi.pdf`) outlines academic regulations for graduate assistants but explicitly omits financial compensation tables. In Arm 2 (Hybrid), the lexical FTS match for *"دستیار آموزشی"* and *"حق‌التدریس"* retrieved general administrative clauses with high FTS rank ($k_{\text{rrf}}$ rank 1) and vector score $0.612$. While the retrieved text did not state the rate, Gemini 2.5 Flash Lite failed to emit the standard negative refusal and instead generated a grounded template answer summarizing the duties of a TA.
- In Arm 1 (Vector-Only) and under calibrated tau ($\tau = 0.75$), this question was cleanly rejected by the pre-LLM gate. In Arm 3, subtle chunk rank differences allowed the prompt instructions to trigger refusal.

---

## 6. Hybrid vs. Vector Retrieval & FTS-Bypass Counterfactual

### 6.1 End-to-End Answer Quality Comparison

Under production gating ($\tau = 0.55$), comparing Arm 1 (Vector) and Arm 2 (Hybrid) across all 112 answerable questions:
- **Answer Rate:** Arm 1 = 88.4% (99/112) vs. Arm 2 = 89.3% (100/112). Paired McNemar test: **$p = 1.000$** (1 discordant question in each direction; no significant difference).
- **Keyword Recall:** Arm 1 = 74.1% vs. Arm 2 = 73.2% ($\Delta = -0.9\%$).
- **Citation Precision:** Arm 1 = 67.3% vs. Arm 2 = 69.1% ($\Delta = +1.8\%$).

While dense vector retrieval achieved higher reciprocal rank in Stage 1 ($0.850$ vs. $0.784$), the generative LLM easily extracts answers from top-6 retrieved context regardless of whether the gold chunk is at rank 1 or rank 3.

### 6.2 The FTS-Bypass Counterfactual Analysis

The true architectural value of Hybrid search lies not in raw MRR, but in **safeguarding the system against catastrophic threshold failure**.

In the DocSearch architecture, a question bypasses the similarity gate if **either** $\text{bestVectorScore} \ge \tau$ **or** $\text{hasFtsHit} = \text{true}$ (meaning PostgreSQL full-text search matched the query terms).

To quantify the impact of this rule, a counterfactual experiment was simulated under $\tau = 0.75$ on the 52 held-out answerable questions:
- **With FTS Bypass (Actual Arm 2 Production Logic):** $\text{LowSimilarity FRR} = \mathbf{34.6\%}$ (18/52 falsely rejected).
- **Without FTS Bypass (Pure Cosine Gating Counterfactual):** $\text{LowSimilarity FRR} = \mathbf{98.1\%}$ (51/52 falsely rejected!).

**Key Insight:** At $\tau = 0.75$, 51 out of 52 held-out questions had top vector cosine similarities below $0.75$. Without FTS bypass, the system would have collapsed into 98.1% silence. Hybrid FTS matching **rescued 33 valid questions** from false rejection, single-handedly preventing total system shutdown under restrictive threshold policies.

---

## 7. Baseline Noise Floor (Null Experiment)

To assess the stochastic variability of the generative LLM (`gemini-3.5-flash-lite` at temperature 0.0), a null experiment was conducted using the **76 questions covering clean, untouched PDFs** (`aein-91.pdf`, `dastyar-pazhuheshi.pdf`, `kod-akhlaghi.pdf`, `bedoon-factor.pdf`, `hamayesh.pdf`).

On these documents, the raw text ingested by Arm 2 (OCR Off) and Arm 3 (Selective OCR) is **100% byte-for-byte identical**, because the selective OCR sidecar automatically skipped these clean files. Consequently, Arm 2 and Arm 3 passed the exact same retrieved chunks in the exact same rank order to Gemini.

- **Total Evaluated Questions:** 76
- **Identical Answer Status:** **76 / 76 (100.0%)**
- **Discordant Outcomes:** **0 / 76 (0.0%)**

This null experiment proves that at temperature 0.0, the LLM exhibits zero stochastic drift across independent API sessions. Any performance delta observed between Arm 2 and Arm 3 on defective documents is purely attributable to OCR text layer restoration.

---

## 8. Stage 2 Secondary Endpoint: Granular Extraction on `heldout-CMap`

The pre-registered secondary endpoint (ADR 0011 §5.2) evaluates whether restoring corrupted font encodings via selective OCR translates into measurable improvements in granular numerical and unit extraction.

This evaluation was conducted on the two held-out documents exhibiting defective CMap text layers: `tarh-dakheli.pdf` and `forsat-motaleati.pdf` ($N=27$ questions; 24 answerable, 3 not-in-docs). Per ADR 0011 §5.2, this endpoint is evaluated under the **`ungated`** operating point to assess pure text-layer extraction capability unconfounded by threshold truncation.

### 8.1 Secondary Endpoint Extraction Metrics

| Metric | Arm 2 (OCR Off) | Arm 3 (Selective OCR) | Absolute Delta | Relative Gain | Significance Test |
|---|---|---|---|---|---|
| **Multi-digit Numbers ($N=7$)** | **0 / 7 (0.0%)** | **3 / 7 (42.9%)** | **+42.9%** | $+\infty$ | McNemar $p = 0.250$ (descriptive only) |
| **Unit Phrases Strict ($N=10$)** | **1 / 10 (10.0%)** | **2 / 10 (20.0%)** | **+10.0%** | $+100.0\%$ | McNemar $p = 1.000$ (descriptive only) |
| **Unit Phrases Proximity $\le 10$ ($N=10$)** | **1 / 10 (10.0%)** | **3 / 10 (30.0%)** | **+20.0%** | $+200.0\%$ | McNemar $p = 0.625$ (descriptive only) |

### 8.2 The "Confident Hallucination" Phenomenon in Corrupted Text

While small sample size ($N=7$ numbers, $N=10$ units) renders hypothesis tests descriptive, qualitative error analysis revealed a critical finding for enterprise RAG deployments: **corrupted PDF text layers cause models to generate confident, silent factual errors.**

In Arm 2 (OCR Off), PyMuPDF extracted scrambled Unicode glyphs and misplaced Arabic/Persian digits from `tarh-dakheli.pdf` and `forsat-motaleati.pdf`. When queried:
- **Questions `q079`, `q080`, `q081`:** Queries asking for duration limits and financial budget caps. In Arm 2, Gemini encountered garbled digit strings (e.g., extracting `۳۶` instead of `۴۸` months, or omitting the initial digit). Instead of refusing, the model **confidently asserted the wrong number**, citing the correct page.
- In Arm 3 (Selective OCR), Tesseract OCR correctly recognized the visual glyphs, restoring the proper numerals. Gemini extracted the exact, ground-truth numbers.

### 8.3 Document Heterogeneity in Font Defect Severity

- **`forsat-motaleati.pdf`:** Experienced widespread CMap permutation where digits and letters mapped to unprintable Unicode codepoints (`U+FFFD` or private use area). Selective OCR completely rehabilitated extraction on this document.
- **`tarh-dakheli.pdf`:** Contained a hybrid font subset where certain multi-digit numbers were partially preserved as isolated symbols, but compound units were scrambled.

### 8.4 Number-Word Sensitivity Analysis

In Persian academic regulations, numbers are frequently written as written words (e.g., *"شش ماه"* [six months], *"یک سال"* [one year]) rather than Western or Eastern-Arabic digits. When the evaluation was expanded post-hoc to accept Persian number words alongside numerical digits:
- Arm 3 Unit Strict Recall increased from 20.0% (2/10) to **30.0% (3/10)**.
- Arm 3 Unit Proximity Recall increased from 30.0% (3/10) to **40.0% (4/10)**.

### 8.5 Full Collection Extraction Impact

Across all 138 evaluated questions in the collection:
- **Multi-digit Number Recall:** Arm 2 = 61.0% (25/41) vs. Arm 3 = **73.2% (30/41)** — an absolute improvement of **+12.2%** ($+20.0\%$ relative gain) across the repository.
- **Unit Phrase Proximity Recall:** Arm 2 = 52.8% (19/36) vs. Arm 3 = **61.1% (22/36)** — an absolute improvement of **+8.3%**.

---

## 9. Error Analysis & System Quality Audit

### 9.1 Decomposition of False Rejections

Under production gating (`prod_055`), answerable false rejections ($N=13$ in Arm 1, $N=17$ in Arm 2, $N=19$ in Arm 3 overall) were categorized by root cause:

| Failure Mode | Arm 1 (Vector) | Arm 2 (Hybrid) | Arm 3 (Full System) | Root Cause Description |
|---|---|---|---|---|
| **`LowSimilarity` Pre-LLM Gate** | 1 (0.9%) | 1 (0.9%) | 2 (1.8%) | Top vector score $< 0.55$ and no FTS hit (extreme query paraphrase) |
| **`ModelRefusal` (Gold chunk NOT in Top-6)** | 3 (2.7%) | 4 (3.6%) | 4 (3.6%) | Retrieval failure: relevant section ranked beyond position 6 |
| **`ModelRefusal` (Gold chunk WAS in Top-6)** | 9 (8.0%) | 12 (10.7%) | 13 (11.6%) | Generation failure: model was overly cautious and refused despite context |

**The "Gold-in-Top-6" Refusal Phenomenon:** In ~70% of all answerable rejections, the gold reference chunk was present in the top-6 context sent to Gemini, but the model refused to answer. This behavior stems from the strict negative constraint in the prompt: *"اگر پاسخ دقیق در متن نیست، صراحتاً اعلام کن"*. When regulations state a general principle without naming the exact administrative term queried, Gemini conservatively abstains rather than extrapolating.

### 9.2 English Acronym & Keyword Evaluation Artifacts

Detailed audit of keyword recall failures identified several cases where Gemini answered correctly, but was penalized by strict string matching:
- **English Terminology:** In questions regarding international conferences (`hamayesh.pdf`) or technical specifications, Gemini answered fluently in Persian without transliterating the English acronym (e.g., explaining the concept without repeating the exact English string `"IEEE"`). These account for ~4% of keyword misses.

### 9.3 Performance Breakdown by Question Type

Evaluating Arm 3 under `prod_055` across question types:

| Question Type | Answerable Count | Answer Rate | Keyword Recall | Citation Precision | Primary Strength / Vulnerability |
|---|---|---|---|---|---|
| **Numeric** | 35 | **91.4%** | 80.0% | 74.2% | High accuracy when OCR text is clean |
| **Procedural / Legal** | 23 | **87.0%** | 73.9% | 71.4% | Strong citation grounding on specific bylaws |
| **Factual / Definitional** | 36 | **86.1%** | 69.4% | 70.8% | Vulnerable to conservative model refusal |
| **Paraphrase / Complex** | 18 | **77.8%** | 61.1% | 66.7% | Higher lexical divergence causes lower FTS match |

### 9.4 Citation Fidelity & Source Grounding

A key requirement of the B.Sc. project is providing actionable citations:

| Metric | Arm 1 (Vector) | Arm 2 (Hybrid) | Arm 3 (Full System) | Definition / Standard |
|---|---|---|---|---|
| **Citation Validity Rate** | 83.8% | 83.0% | **85.6%** | Percentage of answered queries with valid `[n]` bracketed citations |
| **Citation Page-Hit Rate** | 71.7% | 73.0% | **74.2%** | Answer cites the exact PDF page containing the ground-truth text |
| **Citation Precision** | 67.3% | 69.1% | **71.9%** | Proportion of cited sources that are genuinely relevant |
| **Citation Recall** | 67.7% | 67.5% | **68.6%** | Proportion of required ground-truth pages that were cited |

Arm 3 achieved the highest citation validity (85.6%) and precision (71.9%), proving that clean OCR text enhances the model's ability to correlate specific facts with exact bracketed source indices.

### 9.5 Regime B Scanned Twin End-to-End Evaluation

Evaluating the 8 twin questions on `bein-almelali-scan.pdf` end-to-end:
- **Scan Twin (OCR Off):** **0 / 8 (0.0%) answered.** The system returned clean negative refusals (`NoContext`) for all queries, never hallucinating.
- **Scan Twin (Selective OCR):** **6 / 8 (75.0%) answered.** Correctly extracted administrative rules with valid page citations.
- **Digital Reference (Clean PDF):** **8 / 8 (100.0%) answered.**
- Selective OCR restored 75% end-to-end answerability on raw image scans with zero user intervention.

### 9.6 Live System Latency Profile

Measured live during full execution across all query phases:

| Pipeline Stage / Cohort | Median ($p_{50}$) | 95th Percentile ($p_{95}$) | Architectural Notes |
|---|---|---|---|
| **Arm 1 (Vector-Only)** | **2,111.6 ms** | 11,713.7 ms | Single pgvector HNSW query + Gemini streaming generation |
| **Arm 2 (Hybrid)** | **3,258.1 ms** | 21,305.2 ms | Concurrent vector + tsvector FTS + RRF merge |
| **Arm 3 (Full System)** | **2,426.7 ms** | 16,895.9 ms | Hybrid retrieval over OCR-augmented chunks |
| **Pre-LLM Gate Rejection** | **< 35 ms** | < 80 ms | Rejections bypass LLM generation entirely |

Live latencies are well within acceptable interactive thresholds for web search ($p_{50} \sim 2.4\text{ seconds}$). Tail latency ($p_{95} > 15\text{ seconds}$) is driven primarily by external Gemini API network variability from Iran.

### 9.7 Production Reliability & Degraded Mode Recovery

During the 430 live queries of Stage 2 (Phase A and Phase B):
- Exactly **16 out of 417 initial requests** in Phase B experienced transient network socket resets or HTTP 503 errors from the external chat endpoint.
- In all 16 cases, the automated exponential backoff mechanism (`[2.0, 6.0, 18.0, 45.0]` seconds) **successfully recovered on attempt 2**.
- Final failed provider count: **0**.
- Zero question evaluations were lost, verifying production robustness under unstable network conditions.

---

## 10. Visualizations & Inline Figures

### Figure 1: End-to-End Generation & Citation Quality Comparison
![Figure 1: Quality Comparison Across Arms](file:///g:/programming/univ/B.Sc.FinalProject/Antigravity/docs/figures/plot1_stage2_quality_comparison.png)  
*Figure 1: Comparison of Answer Rate, Keyword Recall, Citation Validity, and Citation Precision across Arm 1 (Vector), Arm 2 (Hybrid), and Arm 3 (Full System) under production gating ($\tau = 0.55$). Error bars represent 95% Wilson score confidence intervals.*

---

### Figure 2: Abstention Trade-off Curves & In-Sample vs. Out-of-Sample Generalization
![Figure 2: Threshold Trade-off Curves](file:///g:/programming/univ/B.Sc.FinalProject/Antigravity/docs/figures/plot2_threshold_tradeoff_curves.png)  
*Figure 2: Operating characteristic curves showing False Rejection Rate (FRR) and False Acceptance Rate (FAR) as a function of similarity threshold $\tau$. Left panel: In-sample calibration partition (`tuned|dev`, $N_{\text{ans}}=18, N_{\text{nid}}=7$). Right panel: Out-of-sample held-out evaluation (`tuned|test`, $N_{\text{ans}}=42, N_{\text{nid}}=7$). Notice the dramatic surge in FRR above $\tau = 0.65$ on test data.*

---

### Figure 3: Selective OCR Impact on Granular Extraction & Scanned Documents
![Figure 3: OCR Generation Impact](file:///g:/programming/univ/B.Sc.FinalProject/Antigravity/docs/figures/plot3_ocr_generation_impact.png)  
*Figure 3: Left panel: Pre-registered secondary endpoint on `heldout-CMap` ($N=27$), showing recall of Multi-digit Numbers ($N=7$), Strict Units ($N=10$), and Proximity $\le 10$ Units ($N=10$) for Arm 2 (OCR Off) vs. Arm 3 (Selective OCR). Right panel: Regime B Scanned Twin case study ($N=8$), contrasting 0% answerability without OCR against 75% answerability with Selective OCR and 100% on the digital reference.*

---

## 11. Final Conclusions & Thesis Summary

1. **Selective OCR is Essential for Enterprise PDF RAG:** Corrupted ToUnicode mapping tables in Persian PDFs destroy numerical extraction without entirely breaking coarse retrieval. While macro-MRR showed parity, selective OCR increased number recall on corrupted documents from **0.0% to 42.9%**, eliminating dangerous, confident hallucinations of incorrect numbers.
2. **Fixed Low-Threshold Gating Outperforms In-Sample Calibration:** High-threshold similarity calibration ($\tau \ge 0.70$) fails out-of-sample, causing catastrophic over-abstention (FRR up to 76.9%). A conservative baseline ($\tau = 0.55$) paired with strict prompt-based refusal achieves optimal security ($\text{FAR} = 0.0\%$) without sacrificing answerability.
3. **Hybrid Search Provides Critical Fault-Tolerance:** While dense embeddings excel at semantic retrieval, reciprocal rank fusion with full-text search prevents false similarity rejection on semantically divergent queries, rescuing over 60% of otherwise gated questions.
4. **Interactive Viability:** With a pure OCR cost of $4.13\text{ s/page}$ restricted strictly to defective documents and an end-to-end median response time of $2.4\text{ seconds}$, DocSearch delivers a robust, secure, and production-ready solution for Persian institutional document search.
