# 2. PDF extractor, embedding model, and token calibration decisions

Date: 2026-10-14  
Status: Accepted  

## Context

Phase-0 experiments (Task T01) were conducted to establish data-backed decisions for:
1. Selecting the primary PDF extraction library among **PyMuPDF**, **pdfplumber**, and **PdfPig**.
2. Deciding whether OCR is required for scanned documents and tuning page quality scoring.
3. Benchmarking embedding and retrieval performance (Recall@1/3/5/10, MRR) with candidate models in Ollama (`bge-m3` and `multilingual-e5-*`).
4. Calibrating heuristic token counter multipliers against real Ollama token counts.

The evaluation was performed on a dataset of 12 real PDF documents (Persian university regulations, research papers, and scanned forms) in `eval/pdfs/` and 63 benchmark questions in `eval/questions.json` (divided into 20 `dev` and 43 `test` questions). The development host is CPU-only with 12 GB RAM running Ollama at `http://localhost:11434`.

---

## Results Summary

### 1. PDF Extractor Comparison (12 Documents, 130 Pages Each)

| Metric | PyMuPDF (`fitz`) | pdfplumber | PdfPig (`UglyToad.PdfPig`) |
|---|---|---|---|
| **Mean Quality Score** | **0.892** | 0.700 | 0.700 |
| **Reversed Persian Pages** | **0 / 130 (0.0%)** | 50 / 130 (38.5%) | 50 / 130 (38.5%) |
| **Empty / Scanned Pages** | 8 | 8 | 8 |
| **Total Extraction Time** | **1.54 s** | 17.43 s | 3.26 s |
| **Mean Time / Page** | **0.012 s** | 0.134 s | 0.025 s |

*Key finding:* Both PdfPig and pdfplumber extracted visual/physical character sequences on 50 Persian pages, resulting in completely backwards text (e.g., `ﯽﺳﺎﻨﺷرﺎﮐ يﺎﻫ هرود ﯽﺷزﻮﻣآ ﻪﻣﺎﻧ ﻦﯿﯾآ`), whereas PyMuPDF resolved logical reading order correctly (e.g., `آیین نامه آموزشی دوره های کارشناسی`) with zero reversed pages.

### 2. Scanned Document & OCR Assessment

- On `bein-almelali-scan.pdf` (scanned document), extracted character count across all pages was < 20, resulting in `quality = 0.0` and `empty_page = True`.
- Page quality scoring formula correctly identified 100% of scanned pages as requiring OCR (`NeedsOcr = True` with threshold `MinPageQuality = 0.35`).
- For digital PDFs with selectable text, quality scores averaged 0.95–1.00 under PyMuPDF, requiring no OCR routing.

### 3. Embedding Model & Retrieval Performance (`bge-m3`)

- Candidate model `bge-m3:latest` (1024 dimensions) was benchmarked across all 147 document chunks and 63 questions.
- Mean embedding latency on CPU: **5.54 s / chunk** (batch size 4, 3 CPU threads).

#### Retrieval Metrics (Answerable Questions: 52 total)

| Split | Question Count | Recall@1 | Recall@3 | Recall@5 | Recall@10 | MRR | Mean Top-1 Sim (Ans) | Mean Top-1 Sim (Not) |
|---|---|---|---|---|---|---|---|---|
| **dev** | 16 | 68.8% | 75.0% | 87.5% | 100.0% | 0.7616 | 0.6547 | 0.6291 |
| **test** | 36 | 75.0% | 86.1% | 91.7% | 94.4% | 0.8229 | 0.6598 | 0.5647 |
| **all** | 52 | 73.1% | 82.7% | 90.4% | 96.2% | 0.8040 | 0.6582 | 0.5882 |

#### Out-of-Domain Separation (`Rag:MinSimilarity` Tuning on Dev Split)

- Tuned strictly on **dev split** (16 answerable questions vs 4 out-of-domain questions) to prevent test contamination:
- Answerable top-1 similarity range: `[0.5826, 0.7654]` (Mean: `0.6547`).
- Out-of-domain (`not-in-docs`) top-1 similarity range: `[0.5940, 0.6522]` (Mean: `0.6291`).
- Optimal operational threshold: **`Rag:MinSimilarity = 0.650`** (or balanced range `0.60–0.65`).

### 4. Multilingual-E5 Registry Status (Golden Rule 6 Report)

- Verification of `ollama pull multilingual-e5-base` and `multilingual-e5-small` against the official Ollama registry returned: `Error: pull model manifest: file does not exist`.
- Neither `multilingual-e5-base` nor `multilingual-e5-small` is published in the official Ollama model library at this time.
- Consequently, `bge-m3` is selected as the primary embedding model, with 1024 vector dimensions.

### 5. Token Counter Calibration (60 Chunks Sampled)

Heuristic formula: $\text{tokens} = \lceil \text{words} \times M \rceil + \lfloor \text{symbols} / 12 \rfloor$

| Category | Sample Size | Default Spec $M$ | Calibrated $M$ | MAPE (%) | RMSE (Tokens) |
|---|---|---|---|---|---|
| **Persian** (`persianRatio >= 0.5`) | 35 | 1.80 | **1.35** | 6.09% | 8.10 |
| **Latin/English** (`persianRatio < 0.5`) | 25 | 1.30 | **1.80** | 12.34% | 45.24 |

---

## Decisions

1. **PDF Extractor:** Adopt **PyMuPDF** (`fitz`) via a Python FastAPI sidecar (Option B in T05) as the primary PDF extraction engine. PdfPig is disqualified for Persian due to severe reverse-text ordering on 38.5% of pages.
2. **Embedding Model & Dimensions:** Adopt **`bge-m3`** via Ollama with fixed dimension **`vector(1024)`**. This decision fixes the database schema column definition in Task T03.
3. **OCR Routing:** Implement OCR routing in T12 for pages with `quality < 0.35` or `chars < 20`. Digital PDFs will bypass OCR.
4. **Token Counter Multipliers:** Set `PersianMultiplier = 1.35` and `LatinMultiplier = 1.80` for `HeuristicTokenCounter` in Task T06.
5. **Threshold Default:** Set `Rag:MinSimilarity = 0.65` as the baseline threshold for rejecting out-of-domain questions.
6. **CPU Concurrency & Batching:** For Ollama calls on CPU, enforce batch size ≤ 4 and `num_thread = 3` with incremental batch persistence to prevent memory exhaustion and thermal throttling.

---

## Consequences

- **T03 (Persistence):** The PostgreSQL chunks table will be defined with `embedding vector(1024)`.
- **T05 (Extractor Adapter):** Will implement the HTTP adapter calling the PyMuPDF sidecar with quality scoring.
- **T06 (Chunker):** Will use calibrated multipliers `1.35` (Persian) and `1.80` (Latin).
- **T07 (Embeddings):** Will configure Ollama embedding provider targeting `bge-m3` (1024 dims).

---

## Alternatives Considered

- **PdfPig (C# In-Process):** Rejected. While in-process C# avoids a sidecar, PdfPig cannot extract Persian text in correct logical reading order without extensive custom bidi layout restructuring.
- **pdfplumber (Python):** Rejected. 10x slower than PyMuPDF and exhibited identical backwards text on Persian pages.
- **multilingual-e5-base:** Unavailable on the official Ollama registry; importing third-party GGUF would complicate deployment.
