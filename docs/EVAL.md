# EVAL — evaluation harness and conformance suite

Two different tools:

| Tool | Purpose | Needs real AI? | Used for |
|---|---|---|---|
| **Conformance suite** (`conformance/`) | Does an implementation obey the API contract and safety rules? | No (Fake providers) | CI, comparing agent implementations fairly |
| **Evaluation harness** (`eval/run_eval.py`) | How good are the answers/retrieval? | Yes (Ollama, optionally Gemini) | The thesis (scientific part), tuning thresholds |

---

## 1. Evaluation dataset

### 1.1 Files
- `eval/pdfs/` — real documents (git-ignored; may be confidential/copyrighted).
- `eval/questions.json` — committed; contains questions, **never** document text beyond short gold answers.

### 1.2 Schema
```json
[
  {
    "id": "q001",
    "question": "حداکثر تعداد واحد مجاز در ترم چند واحد است؟",
    "expected_answer": "۲۰ واحد",
    "answer_keywords": ["20", "واحد"],
    "source_file": "aeen-name-amozeshi.pdf",
    "source_pages": [12],
    "type": "factual",
    "language": "fa",
    "notes": ""
  }
]
```
- `type` ∈ `factual`, `numeric`, `legal-reference` (mentions ماده/تبصره/بند), `multi-chunk` (needs ≥ 2 places), `paraphrase` (question words differ from the text), `not-in-docs`, `english`.
- `answer_keywords`: normalized (NormalizeForEmbedding-style: ASCII digits, unified letters) strings that must appear in a correct answer (all of them). For `not-in-docs` items `source_file` is `null`.
- Minimum for the thesis: **≥ 50 questions**: ≥ 12 factual/numeric, ≥ 10 legal-reference, ≥ 8 paraphrase, ≥ 6 multi-chunk, ≥ 8 not-in-docs, ≥ 4 english. Write questions **before** looking at system output (avoid tuning to the test).
- Split: ~30% **dev** (used for tuning thresholds/chunk size) and ~70% **test** (reported). Add `"split": "dev"|"test"` to each item. Never tune on `test`.

### 1.3 Quality checks (script `eval/validate_questions.py`)
Unique ids; every `source_file` exists in `eval/pdfs/`; `source_pages` within page count; keywords non-empty for answerable questions; reports counts per `type` and `split`.

---

## 2. Metrics (definitions — implement exactly)

Let `Q_ans` be answerable questions (type ≠ `not-in-docs`), `Q_not` the `not-in-docs` ones.

**Retrieval (on `Q_ans`, using the sources returned by retrieval, before generation):**
- A retrieved chunk is a **hit** for a question if `chunk.fileName == source_file` AND `[page_start, page_end]` intersects `source_pages`.
- `Recall@K` = fraction of questions with ≥ 1 hit in the top K chunks (K = 1, 3, 5, 10).
- `MRR` = mean of `1 / rank_of_first_hit` (0 if none).
- Report also: mean/median similarity score of top-1 for answerable vs not-in-docs (to choose `MinSimilarity`).

**Generation (on `Q_ans` where the system did not return notFound/degraded):**
- `Correctness (auto)`: all `answer_keywords` are found in the normalized answer → 1 else 0.
- `Correctness (judge, optional)`: LLM-as-judge score 0/1 (rubric below).
- `Faithfulness (judge, optional)`: 1 if every factual claim is supported by the cited excerpts.
- `Citation precision`: fraction of cited sources that are hits for the question.
- `Answer rate` = share of answerable questions that got a real answer (not notFound, not degraded).

**Not-found behavior:**
- On `Q_not`: `false_accept_rate` = share where the system produced an answer instead of notFound (hallucination risk). `true_reject_rate` = 1 − that.
- On `Q_ans`: `false_reject_rate` = share wrongly answered with notFound.
- Sweep `MinSimilarity` in 0.30…0.80 step 0.025 on **dev**; plot false-accept vs false-reject; choose the threshold; report on **test**.

**Performance:** p50/p95 of `/ask` latency (retrieval stage and generation stage separately if the API exposes timings via a debug header `X-Timing-Retrieval-Ms`, `X-Timing-Generation-Ms` — add them in T19 behind `Docs:Enabled`/Development), ingestion seconds per page.

**Statistics:** with ~50 questions use **bootstrap 95% confidence intervals** (1000 resamples, fixed seed) on every reported metric. Do not claim a difference between variants unless the intervals/paired bootstrap support it; say "not significant" otherwise.

### LLM-as-judge rubric (optional; temperature 0; cache results by hash)
Judge input: question, gold answer, system answer, cited excerpts. Judge output JSON only:
`{"correct": 0|1, "faithful": 0|1, "reason": "<≤ 30 words>"}`
Rules: correct = same factual content as gold (wording may differ; numbers must match); faithful = all claims supported by the excerpts. Judge model must differ from or be at least as strong as the answering model; record judge model name in the report. Manually audit ≥ 15 judged items and report agreement.

---

## 3. Harness behavior (`eval/run_eval.py`)

CLI: `python eval/run_eval.py --base-url http://localhost:5000 --email … --password … --collection <id> --questions eval/questions.json --split test --out eval/results/<run-id> [--judge] [--k 1 3 5 10]`.
Steps: login → (optionally) wait until all documents `Ready` → for each question call `POST /ask` (with `topK=max(K)`) → record answer, notFound, degraded, sources, score(s), latency → compute metrics → write `results.json` (raw per-question), `summary.csv` (metrics with CI), `report.md` (tables), PNG plots (matplotlib): recall@K bars, threshold sweep curves, latency histogram.
Resilience: per-question timeout, retry once on network error, `--resume` using cached raw results, LLM calls throttled (`--sleep`) for free-tier quotas. Deterministic ordering and seeds. Never print document text beyond snippets in reports.

### 3.1 Experiment matrix (`eval/experiments.yaml`)
```yaml
baseline: { chunk_target_tokens: 500, overlap: 0.15, hybrid: true, embedding_model: bge-m3, prompt_version: v1, top_k: 6 }
variants:
  - name: chunk-300  ; override: { chunk_target_tokens: 300 }
  - name: chunk-800  ; override: { chunk_target_tokens: 800 }
  - name: no-overlap ; override: { overlap: 0.0 }
  - name: vector-only; override: { hybrid: false }
  - name: e5-base    ; override: { embedding_model: multilingual-e5-base }
  - name: top-k-3 / top-k-10
```
Implementation rule: chunking/embedding variants change stored data → run each in its **own collection** (re-ingest the same PDFs); retrieval-only variants (hybrid, topK, threshold) reuse the baseline collection via per-request/config overrides. The runner starts the API with the right env overrides (`Chunking__TargetTokens=300`, `Rag__UseHybrid=false`, …) or calls the admin endpoints; document how in `eval/README.md`.

### 3.2 Reporting for the thesis
Each experiment: table (metric, baseline, variant, Δ, 95% CI), 2–3 sentences of interpretation, and a failure analysis of 5–10 concrete wrong cases (categorize: extraction error, chunking split, embedding miss, ranking, threshold, generation). Keep raw results so every number in the thesis is traceable.

---

## 4. Conformance suite (`conformance/`, pytest, black-box)

Runs against any implementation: `BASE_URL`, `ADMIN_EMAIL`, `ADMIN_PASSWORD` env vars. The implementation under test must be started with **Fake providers** (`Ai__Embedding__Provider=Fake`, `Ai__Chat__Provider=Fake`, `Rag__MinSimilarity=0.2`, `Auth__BootstrapAdmin__*`, `Auth__AllowSelfRegistration=true`) and a clean database. Markers: `contract` (always), `quality` (needs real Ollama; optional).

`conftest.py`: helper client with auth, fixtures to create users/collections, `wait_until_ready(document_id, timeout=120)` polling with backoff (no fixed sleeps), PDF fixtures from `conformance/data/` (Persian sample PDF supplied by the human with a known sentence; English PDF; empty file; text file renamed `.pdf`; corrupt PDF).

| # | Test | Expected |
|---|---|---|
| 1 | `GET /health/live`, `/health/ready` | 200, JSON with checks, no secrets |
| 2 | Register + login | 201 then 200 with token; wrong password 401 problem+json `auth.invalid_credentials` |
| 3 | Protected endpoint without token | 401 problem+json |
| 4 | Collection CRUD | 201/200/204 with correct bodies; duplicate name 409 `collection.name_taken` |
| 5 | Upload valid PDF | 202, status `Uploaded` or `Processing`, `Location` header |
| 6 | Document becomes `Ready` | `Ready` within timeout, `pageCount ≥ 1`, `chunkCount ≥ 1` |
| 7 | Upload text file named `.pdf` | 400 `document.invalid_file` |
| 8 | Upload empty file / corrupt PDF | 400 or document ends `Failed` with `errorMessage` (never stuck in `Processing`) |
| 9 | Upload > limit | 413 `document.too_large` |
| 10 | Upload same file twice | 409 `document.duplicate` |
| 11 | Ask a question whose words are in the sample PDF | 200, `answer` non-empty, `sources[0].fileName` equals uploaded name, `page ≥ 1`, every `[n]` in answer maps to a source |
| 12 | Ask nonsense (no shared words) | 200, `notFound=true`, no LLM answer text other than the refusal sentence |
| 13 | ACL: user B asks/reads/downloads/deletes in user A's collection | 404 (never 200/403 leaking existence) |
| 14 | ACL: viewer cannot upload/delete; editor can upload; owner manages members | 403/201/… per spec |
| 15 | Ask with `collectionIds` mixing readable + unreadable | only readable are used; sources never from unreadable |
| 16 | Delete document | 204; subsequent ask no longer returns its chunks; file download 404 |
| 17 | Degraded mode | start with `Ai__Chat__Fake__Fail=true` (separate run / marker `degraded`): `/ask` → 200, `degraded=true`, `answer=null`, `sources` non-empty |
| 18 | Validation | empty question 400 `validation.failed`; `topK=0` 400; 21 collection ids 400 |
| 19 | Rate limit | burst on `/auth/login` → eventually 429 with `Retry-After` |
| 20 | Persian round-trip | question with Persian digits/ZWNJ variants of a sentence in the PDF still retrieves it (normalization works) |
| 21 | Admin | non-admin on `/admin/*` → 403; admin reindex → 202 and job completes |
| 22 | Error hygiene | 500-class and 404 bodies contain no stack traces/paths |
| 23 | Idempotent reprocess | `POST /documents/{id}/reprocess` → chunk count stable, status `Ready` |
| 24 | Conversation | ask twice with returned `conversationId`; `GET /conversations/{id}/messages` returns 4 messages in order |

Output: `pytest -m contract -v --junitxml=conformance/results/<impl>.xml`. A tiny script `conformance/score.py` prints pass/fail counts per area for the bake-off scoreboard.

**Freeze rule:** after the human approves T01B, `conformance/**` and `eval/questions.json` are read-only for agents. Any needed change is requested from the human.
