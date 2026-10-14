# Phase-0 Evaluation Experiments (T01)

This folder contains experimental benchmarking tools and validation scripts for:
1. Validating the question dataset against source PDFs.
2. Comparing PDF text extraction across PyMuPDF, pdfplumber, and PdfPig.
3. Benchmarking embedding and vector retrieval using Ollama `bge-m3`.
4. Calibrating the heuristic token counter against Ollama real token counts (`prompt_eval_count`).

---

## 1. Prerequisites

- Python 3.11 virtual environment installed at `.venv`:
  ```powershell
  & ".\.venv\Scripts\python.exe" -m pip install -r eval/experiments/requirements.txt
  ```
- Ollama running locally at `http://localhost:11434` with model `bge-m3:latest`:
  ```powershell
  ollama list
  ```

---

## 2. Running Experiments

### Step 1: Validate Questions
Validates `eval/questions.json` schema, page bounds, and keyword non-emptiness:
```powershell
& ".\.venv\Scripts\python.exe" eval/validate_questions.py
```

### Step 2: Compare PDF Extractors
Runs `PdfPigProbe` (C#), `PyMuPDF`, and `pdfplumber`, evaluates page quality metrics, and generates side-by-side comparison samples:
```powershell
& ".\.venv\Scripts\python.exe" eval/experiments/extract_compare.py
```
- Output CSV: `eval/experiments/out/extract_summary.csv`
- Output Samples: `eval/experiments/out/extract_samples.md`

### Step 3: Run Embedding & Retrieval Benchmark
Chunks extracted document text, embeds chunks and questions via Ollama `/api/embed`, computes Recall@K and MRR across splits (`dev`, `test`, `all`), and performs threshold separation analysis:
```powershell
& ".\.venv\Scripts\python.exe" eval/experiments/embed_benchmark.py
```
- Output CSV: `eval/experiments/out/embed_results.csv`
- Output Report: `eval/experiments/out/embed_results.md`

### Step 4: Run Token Counter Calibration
Samples ≥ 50 individual chunks, queries Ollama for exact token counts, and calibrates heuristic multipliers for Persian and Latin text:
```powershell
& ".\.venv\Scripts\python.exe" eval/experiments/token_calibration.py
```
- Output CSV: `eval/experiments/out/token_calibration.csv`
- Output Report: `eval/experiments/out/token_calibration.md`
