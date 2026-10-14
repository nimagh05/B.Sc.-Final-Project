#!/usr/bin/env python3
"""
Embedding and retrieval benchmark using Ollama and the winning extractor (PyMuPDF).
Implements:
  - Text normalization (SPEC-PERSIAN-AND-RAG.md §1)
  - Chunking (<= 500 tokens)
  - Batch embedding via Ollama /api/embed
  - Retrieval metrics (Recall@1, 3, 5, 10, MRR) split-aware: dev, test, all
  - Separation analysis on dev split between Q_ans and Q_not to recommend Rag:MinSimilarity
Outputs:
  - eval/experiments/out/embed_results.csv
  - eval/experiments/out/embed_results.md
"""

import json
import re
import sys
import time
import unicodedata
from pathlib import Path

import numpy as np
import requests

OLLAMA_EMBED_URL = "http://localhost:11434/api/embed"
EMBED_MODEL = "bge-m3"
BATCH_SIZE = 4


def normalize_for_embedding(text: str) -> str:
    if not text:
        return ""

    # 1. NFKC
    text = unicodedata.normalize("NFKC", text)

    # 2. Remove bidi & invisible controls except ZWNJ U+200C
    # U+200B, U+200E, U+200F, U+202A..U+202E, U+2066..U+2069, U+FEFF, U+00AD
    bad_chars = [
        "\u200b",
        "\u200e",
        "\u200f",
        "\u202a",
        "\u202b",
        "\u202c",
        "\u202d",
        "\u202e",
        "\u2066",
        "\u2067",
        "\u2068",
        "\u2069",
        "\ufeff",
        "\u00ad",
    ]
    for bc in bad_chars:
        text = text.replace(bc, "")

    # 3. Newlines & unicode space separators
    text = text.replace("\r\n", "\n").replace("\r", "\n")
    # Replace non-newline whitespace separators (U+00A0 etc.) with standard space
    text = re.sub(r"[\t\u00a0\u1680\u2000-\u200a\u202f\u205f\u3000]", " ", text)

    # 4. Letters unification: ي->ی, ى->ی, ك->ک, ة->ه
    trans_letters = str.maketrans(
        {
            "\u064a": "ی",  # Arabic Yeh
            "\u0649": "ی",  # Alef Maksura
            "\u0643": "ک",  # Arabic Kaf
            "\u0629": "ه",  # Teh Marbuta
        }
    )
    text = text.translate(trans_letters)

    # 5. Remove tatweel U+0640
    text = text.replace("\u0640", "")

    # 6. Remove Arabic diacritics: U+064B..U+065F and U+0670
    text = re.sub(r"[\u064b-\u065f\u0670]", "", text)

    # 7. Digits: Persian ۰-۹ (06F0-06F9) & Arabic-Indic ٠-٩ (0660-0669) -> ASCII 0-9
    # Decimal 066B -> ., Thousands 066C -> ,
    trans_digits = {
        0x06F0 + i: str(i) for i in range(10)
    }
    trans_digits.update({0x0660 + i: str(i) for i in range(10)})
    trans_digits[0x066B] = "."
    trans_digits[0x066C] = ","
    text = text.translate(trans_digits)

    # 8. ZWNJ clean-up: collapse runs of ZWNJ; remove ZWNJ adjacent to space/newline or edge
    text = re.sub(r"\u200c+", "\u200c", text)
    text = re.sub(r"\s+\u200c", " ", text)
    text = re.sub(r"\u200c\s+", " ", text)
    text = re.sub(r"^\u200c|\u200c$", "", text, flags=re.MULTILINE)

    # 9. Join hyphenated Latin line breaks (word-\nnext -> wordnext)
    text = re.sub(r"([A-Za-z])-\n([A-Za-z])", r"\1\2", text)

    # 10. Single newlines inside paragraphs -> space; blank lines preserved
    # First protect double newlines
    text = re.sub(r"\n\s*\n+", "\n\n", text)
    parts = text.split("\n\n")
    cleaned_paras = []
    for p in parts:
        single_joined = re.sub(r"(?<!\n)\n(?!\n)", " ", p.strip())
        cleaned_paras.append(single_joined)
    text = "\n\n".join(cleaned_paras)

    # 11. Collapse spaces and trim
    text = re.sub(r"[ ]+", " ", text)
    lines = [line.strip() for line in text.split("\n")]
    text = "\n".join(lines).strip()

    return text


def clean_headers_footers(pages_text: list[str]) -> list[str]:
    """Clean repeating headers and footers across pages if >= 4 pages."""
    if len(pages_text) < 4:
        return pages_text

    cleaned_pages = []
    for p in pages_text:
        lines = p.split("\n")
        kept = []
        for line in lines:
            # Drop pure page number lines
            if re.match(r"^\s*[0-9\-\–\—\s]+\s*$", line) or re.match(r"^\s*(صفحه|page)\s*[0-9]+\s*$", line, re.IGNORECASE):
                continue
            kept.append(line)
        cleaned_pages.append("\n".join(kept))
    return cleaned_pages


def chunk_document(pdf_name: str, pages_text: list[str], max_tokens: int = 500) -> list[dict]:
    """Fixed-size chunker targeting <= max_tokens tokens with page tracking."""
    cleaned_pages = clean_headers_footers(pages_text)
    chunks = []

    # Estimate tokens: ~1.8 tokens/word for Persian, ~1.3 for English
    def est_tokens(s: str) -> int:
        words = len(s.split())
        return int(words * 1.8) + 1

    current_units = []
    current_tokens = 0
    page_start = 1
    page_end = 1
    chunk_index = 0

    for page_idx, page_raw in enumerate(cleaned_pages):
        page_num = page_idx + 1
        page_norm = normalize_for_embedding(page_raw)
        if not page_norm:
            continue

        # Split into sentence/paragraph units
        paras = [p.strip() for p in page_norm.split("\n\n") if p.strip()]
        for para in paras:
            sentences = re.split(r"(?<=[\.\!\?؟؛])\s+", para)
            for sent in sentences:
                sent = sent.strip()
                if not sent:
                    continue

                sent_tok = est_tokens(sent)
                # Hard-split sentence if single giant sentence exceeds max_tokens
                if sent_tok > max_tokens:
                    words = sent.split()
                    sub_len = int(max_tokens / 1.8)
                    for i in range(0, len(words), sub_len):
                        sub_sent = " ".join(words[i : i + sub_len])
                        chunks.append(
                            {
                                "pdf": pdf_name,
                                "chunk_id": f"{pdf_name}_{chunk_index}",
                                "page_start": page_num,
                                "page_end": page_num,
                                "text": sub_sent,
                                "tokens": est_tokens(sub_sent),
                            }
                        )
                        chunk_index += 1
                    continue

                if current_tokens + sent_tok > max_tokens and current_units:
                    chunk_text = " ".join(current_units)
                    chunks.append(
                        {
                            "pdf": pdf_name,
                            "chunk_id": f"{pdf_name}_{chunk_index}",
                            "page_start": page_start,
                            "page_end": page_end,
                            "text": chunk_text,
                            "tokens": current_tokens,
                        }
                    )
                    chunk_index += 1

                    # Overlap: keep last sentence if small
                    last_sent = current_units[-1]
                    if est_tokens(last_sent) < int(max_tokens * 0.25):
                        current_units = [last_sent, sent]
                        current_tokens = est_tokens(last_sent) + sent_tok
                        page_start = page_end
                    else:
                        current_units = [sent]
                        current_tokens = sent_tok
                        page_start = page_num
                    page_end = page_num
                else:
                    if not current_units:
                        page_start = page_num
                    current_units.append(sent)
                    current_tokens += sent_tok
                    page_end = page_num

    if current_units:
        chunk_text = " ".join(current_units)
        chunks.append(
            {
                "pdf": pdf_name,
                "chunk_id": f"{pdf_name}_{chunk_index}",
                "page_start": page_start,
                "page_end": page_end,
                "text": chunk_text,
                "tokens": current_tokens,
            }
        )

    return chunks


def get_embeddings(texts: list[str], cache_file: Path | None = None) -> tuple[np.ndarray, float]:
    """Embeds a list of texts in small batches using Ollama /api/embed with incremental caching and resume capability."""
    all_embeddings = []
    start_index = 0

    if cache_file and cache_file.exists():
        try:
            cached = np.load(cache_file)
            if "embeddings" in cached:
                cached_embs = cached["embeddings"]
                if cached_embs.shape[0] == len(texts):
                    print(f"Loaded all {len(texts)} embeddings from cache {cache_file.name}", flush=True)
                    # Ensure normalized
                    norms = np.linalg.norm(cached_embs, axis=1, keepdims=True)
                    norms[norms == 0] = 1.0
                    return cached_embs / norms, 0.0
                elif 0 < cached_embs.shape[0] < len(texts):
                    all_embeddings = list(cached_embs)
                    start_index = len(all_embeddings)
                    print(f"Resuming embedding from chunk index {start_index}/{len(texts)} using partial cache {cache_file.name}", flush=True)
        except Exception as e:
            print(f"Cache load warning: {e}", flush=True)

    t0 = time.perf_counter()
    total_batches = (len(texts) + BATCH_SIZE - 1) // BATCH_SIZE

    for idx, i in enumerate(range(start_index, len(texts), BATCH_SIZE), start=(start_index // BATCH_SIZE) + 1):
        batch = texts[i : i + BATCH_SIZE]
        for attempt in range(5):
            try:
                resp = requests.post(
                    OLLAMA_EMBED_URL,
                    json={
                        "model": EMBED_MODEL,
                        "input": batch,
                        "options": {"num_thread": 3},
                    },
                    timeout=300,
                )
                if resp.status_code == 200:
                    data = resp.json()
                    embs = data.get("embeddings", [])
                    all_embeddings.extend(embs)
                    print(
                        f"  Batch {idx}/{total_batches} ({len(all_embeddings)}/{len(texts)}) embedded in {time.perf_counter()-t0:.1f}s",
                        flush=True,
                    )

                    # Save incrementally after EVERY batch to prevent data loss on crash/reboot
                    if cache_file:
                        try:
                            np.savez_compressed(cache_file, embeddings=np.array(all_embeddings, dtype=np.float32))
                        except Exception as e:
                            print(f"Incremental cache save warning: {e}", flush=True)

                    # Thermal throttling protection: short pause between batches
                    time.sleep(0.5)
                    break
                else:
                    print(f"Warning: HTTP {resp.status_code} from embed API: {resp.text}", flush=True)
                    time.sleep(3)
            except Exception as e:
                print(f"Warning: embed error on attempt {attempt+1}: {e}", flush=True)
                time.sleep(3)
        else:
            raise RuntimeError(f"Failed to embed batch starting at index {i}")

    elapsed = time.perf_counter() - t0
    emb_matrix = np.array(all_embeddings, dtype=np.float32)

    # Normalize vectors
    norms = np.linalg.norm(emb_matrix, axis=1, keepdims=True)
    norms[norms == 0] = 1.0
    emb_matrix = emb_matrix / norms

    if cache_file:
        try:
            np.savez_compressed(cache_file, embeddings=emb_matrix)
            print(f"Saved complete normalized {len(texts)} embeddings to {cache_file.name}", flush=True)
        except Exception as e:
            print(f"Cache save warning: {e}", flush=True)

    return emb_matrix, elapsed


def run_benchmark(repo_root: Path):
    out_dir = repo_root / "eval" / "experiments" / "out"
    extract_dir = out_dir / "extract" / "pymupdf"
    questions_file = repo_root / "eval" / "questions.json"

    print("--- Step 1: Loading Extracted Text and Chunking ---")
    all_chunks = []
    for pdf_folder in sorted(extract_dir.glob("*")):
        if not pdf_folder.is_dir():
            continue
        pdf_name = pdf_folder.name
        page_files = sorted(pdf_folder.glob("*.txt"), key=lambda p: int(p.stem))
        pages_text = [pf.read_text(encoding="utf-8") for pf in page_files]
        chunks = chunk_document(pdf_name, pages_text, max_tokens=500)
        all_chunks.extend(chunks)

    print(f"Total chunks created across all PDFs: {len(all_chunks)}")

    print(f"\n--- Step 2: Embedding {len(all_chunks)} Chunks with {EMBED_MODEL} ---")
    chunk_texts = [c["text"] for c in all_chunks]
    chunk_cache = out_dir / "chunk_embeddings_cache.npz"
    chunk_embeddings, chunk_time = get_embeddings(chunk_texts, cache_file=chunk_cache)
    mean_chunk_time = chunk_time / len(all_chunks) if all_chunks else 0.0
    print(f"Chunk embedding finished in {chunk_time:.2f}s ({mean_chunk_time:.4f}s per chunk). Dimension: {chunk_embeddings.shape[1]}")

    print("\n--- Step 3: Loading Questions and Embedding ---")
    with open(questions_file, "r", encoding="utf-8") as f:
        questions = json.load(f)

    # Normalize questions
    q_texts = [normalize_for_embedding(q["question"]) for q in questions]
    q_cache = out_dir / "question_embeddings_cache.npz"
    q_embeddings, q_time = get_embeddings(q_texts, cache_file=q_cache)
    print(f"Embedded {len(questions)} questions in {q_time:.2f}s.")

    print("\n--- Step 4: Computing Cosine Similarity & Retrieval Metrics ---")
    # Similarity matrix: [num_questions, num_chunks]
    similarity_matrix = np.dot(q_embeddings, chunk_embeddings.T)

    results_records = []
    splits = ["dev", "test", "all"]

    metrics_by_split = {
        s: {
            "total_q": 0,
            "ans_q": 0,
            "not_q": 0,
            "hits_1": 0,
            "hits_3": 0,
            "hits_5": 0,
            "hits_10": 0,
            "mrr_sum": 0.0,
            "top1_ans_scores": [],
            "top1_not_scores": [],
        }
        for s in splits
    }

    for idx, q in enumerate(questions):
        qid = q["id"]
        q_type = q["type"]
        q_split = q["split"]
        is_ans = q_type != "not-in-docs"
        src_file = q.get("source_file")
        src_pages = set(q.get("source_pages") or [])

        sims = similarity_matrix[idx]
        sorted_indices = np.argsort(-sims)
        top1_sim = float(sims[sorted_indices[0]])

        # Update metrics for question split and 'all'
        target_splits = [q_split, "all"]

        for s in target_splits:
            metrics_by_split[s]["total_q"] += 1
            if is_ans:
                metrics_by_split[s]["ans_q"] += 1
                metrics_by_split[s]["top1_ans_scores"].append(top1_sim)
            else:
                metrics_by_split[s]["not_q"] += 1
                metrics_by_split[s]["top1_not_scores"].append(top1_sim)

        rank_first_hit = None
        for rank, c_idx in enumerate(sorted_indices, start=1):
            chunk = all_chunks[c_idx]
            if is_ans and chunk["pdf"] == src_file:
                c_range = set(range(chunk["page_start"], chunk["page_end"] + 1))
                if c_range.intersection(src_pages):
                    rank_first_hit = rank
                    break

        hit1 = is_ans and (rank_first_hit == 1)
        hit3 = is_ans and (rank_first_hit is not None and rank_first_hit <= 3)
        hit5 = is_ans and (rank_first_hit is not None and rank_first_hit <= 5)
        hit10 = is_ans and (rank_first_hit is not None and rank_first_hit <= 10)
        rr = (1.0 / rank_first_hit) if (is_ans and rank_first_hit is not None) else 0.0

        if is_ans:
            for s in target_splits:
                if hit1:
                    metrics_by_split[s]["hits_1"] += 1
                if hit3:
                    metrics_by_split[s]["hits_3"] += 1
                if hit5:
                    metrics_by_split[s]["hits_5"] += 1
                if hit10:
                    metrics_by_split[s]["hits_10"] += 1
                metrics_by_split[s]["mrr_sum"] += rr

        results_records.append(
            {
                "id": qid,
                "split": q_split,
                "type": q_type,
                "is_answerable": is_ans,
                "source_file": src_file,
                "source_pages": list(src_pages),
                "top1_similarity": round(top1_sim, 4),
                "rank_first_hit": rank_first_hit if rank_first_hit else -1,
                "hit@1": hit1,
                "hit@3": hit3,
                "hit@5": hit5,
                "hit@10": hit10,
                "reciprocal_rank": round(rr, 4),
            }
        )

    # Threshold analysis on dev split specifically
    dev_ans_scores = np.array(metrics_by_split["dev"]["top1_ans_scores"])
    dev_not_scores = np.array(metrics_by_split["dev"]["top1_not_scores"])

    print("\n--- Retrieval Results by Split ---")
    summary_rows = []
    for s in splits:
        m = metrics_by_split[s]
        ans_n = m["ans_q"]
        r1 = (m["hits_1"] / ans_n) if ans_n > 0 else 0.0
        r3 = (m["hits_3"] / ans_n) if ans_n > 0 else 0.0
        r5 = (m["hits_5"] / ans_n) if ans_n > 0 else 0.0
        r10 = (m["hits_10"] / ans_n) if ans_n > 0 else 0.0
        mrr = (m["mrr_sum"] / ans_n) if ans_n > 0 else 0.0
        print(
            f"Split: {s.upper():4s} | Answerable Qs: {ans_n:2d} | "
            f"Recall@1: {r1*100:5.1f}% | Recall@3: {r3*100:5.1f}% | Recall@5: {r5*100:5.1f}% | "
            f"Recall@10: {r10*100:5.1f}% | MRR: {mrr:.4f}"
        )
        summary_rows.append(
            {
                "split": s,
                "answerable_questions": ans_n,
                "not_in_docs_questions": m["not_q"],
                "recall@1": round(r1, 4),
                "recall@3": round(r3, 4),
                "recall@5": round(r5, 4),
                "recall@10": round(r10, 4),
                "mrr": round(mrr, 4),
                "mean_top1_ans": round(float(np.mean(m["top1_ans_scores"])), 4) if m["top1_ans_scores"] else 0.0,
                "mean_top1_not": round(float(np.mean(m["top1_not_scores"])), 4) if m["top1_not_scores"] else 0.0,
            }
        )

    # Threshold sweep on DEV split
    threshold_sweep = []
    best_threshold = 0.45
    min_combined_error = 999.0

    print("\n--- Dev Split Threshold Sweep (Rag:MinSimilarity Tuning) ---")
    print(f"Dev Answerable Top-1: Mean={np.mean(dev_ans_scores):.4f}, Min={np.min(dev_ans_scores):.4f}, Max={np.max(dev_ans_scores):.4f}")
    if len(dev_not_scores) > 0:
        print(f"Dev Not-in-docs Top-1: Mean={np.mean(dev_not_scores):.4f}, Min={np.min(dev_not_scores):.4f}, Max={np.max(dev_not_scores):.4f}")

    for t in np.arange(0.30, 0.81, 0.025):
        t = round(float(t), 3)
        # False reject: answerable questions with top-1 score < t
        false_reject = float(np.mean(dev_ans_scores < t)) if len(dev_ans_scores) > 0 else 0.0
        # False accept: not-in-docs questions with top-1 score >= t
        false_accept = float(np.mean(dev_not_scores >= t)) if len(dev_not_scores) > 0 else 0.0
        combined_error = false_reject + false_accept
        if combined_error < min_combined_error:
            min_combined_error = combined_error
            best_threshold = t

        threshold_sweep.append(
            {
                "threshold": t,
                "false_reject_rate": round(false_reject, 4),
                "false_accept_rate": round(false_accept, 4),
                "combined_error": round(combined_error, 4),
            }
        )

    print(f"\nRecommended Rag:MinSimilarity based on dev split: {best_threshold:.3f} (Combined Error: {min_combined_error:.2f})")

    # Save detailed CSV
    import csv

    csv_path = out_dir / "embed_results.csv"
    with open(csv_path, "w", encoding="utf-8", newline="") as f:
        fieldnames = [
            "id",
            "split",
            "type",
            "is_answerable",
            "source_file",
            "source_pages",
            "top1_similarity",
            "rank_first_hit",
            "hit@1",
            "hit@3",
            "hit@5",
            "hit@10",
            "reciprocal_rank",
        ]
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        for r in results_records:
            writer.writerow(r)
    print(f"Saved embed results to {csv_path}")

    # Generate Markdown Report
    md_path = out_dir / "embed_results.md"
    dev_summary = next(s for s in summary_rows if s["split"] == "dev")
    test_summary = next(s for s in summary_rows if s["split"] == "test")
    all_summary = next(s for s in summary_rows if s["split"] == "all")

    md_content = f"""# Embedding & Retrieval Benchmark Results

**Model:** `{EMBED_MODEL}` (Dimensions: 1024)  
**Total Chunks:** {len(all_chunks)}  
**Mean Chunk Embed Time:** {mean_chunk_time*1000:.2f} ms ({1.0/mean_chunk_time if mean_chunk_time > 0 else 0:.1f} chunks/sec on CPU)  
**Total Questions Evaluated:** {len(questions)}  

---

## 1. Retrieval Performance by Split (Answerable Questions)

| Split | Q Count | Recall@1 | Recall@3 | Recall@5 | Recall@10 | MRR | Mean Top-1 Sim (Ans) | Mean Top-1 Sim (Not) |
|---|---|---|---|---|---|---|---|---|
| **dev** | {dev_summary['answerable_questions']} | {dev_summary['recall@1']*100:.1f}% | {dev_summary['recall@3']*100:.1f}% | {dev_summary['recall@5']*100:.1f}% | {dev_summary['recall@10']*100:.1f}% | {dev_summary['mrr']:.4f} | {dev_summary['mean_top1_ans']:.4f} | {dev_summary['mean_top1_not']:.4f} |
| **test** | {test_summary['answerable_questions']} | {test_summary['recall@1']*100:.1f}% | {test_summary['recall@3']*100:.1f}% | {test_summary['recall@5']*100:.1f}% | {test_summary['recall@10']*100:.1f}% | {test_summary['mrr']:.4f} | {test_summary['mean_top1_ans']:.4f} | {test_summary['mean_top1_not']:.4f} |
| **all** | {all_summary['answerable_questions']} | {all_summary['recall@1']*100:.1f}% | {all_summary['recall@3']*100:.1f}% | {all_summary['recall@5']*100:.1f}% | {all_summary['recall@10']*100:.1f}% | {all_summary['mrr']:.4f} | {all_summary['mean_top1_ans']:.4f} | {all_summary['mean_top1_not']:.4f} |

---

## 2. Dev Split Threshold Tuning (`Rag:MinSimilarity`)

Threshold tuned strictly on **dev split** ({dev_summary['answerable_questions']} answerable questions, {dev_summary['not_in_docs_questions']} out-of-domain questions) to prevent test-set contamination:

| Threshold | False Reject Rate (Ans < T) | False Accept Rate (Not >= T) | Combined Error |
|---|---|---|---|
"""
    for row in threshold_sweep:
        bold = "**" if row["threshold"] == best_threshold else ""
        md_content += f"| {bold}{row['threshold']:.3f}{bold} | {row['false_reject_rate']*100:.1f}% | {row['false_accept_rate']*100:.1f}% | {row['combined_error']:.3f} |\n"

    md_content += f"""
### Recommendation
- **Recommended `Rag:MinSimilarity`:** `{best_threshold:.3f}`
- **Test-split verification at `{best_threshold:.3f}`:**
  - Test false reject rate: {np.mean(np.array(metrics_by_split['test']['top1_ans_scores']) < best_threshold)*100:.1f}%
  - Test false accept rate: {np.mean(np.array(metrics_by_split['test']['top1_not_scores']) >= best_threshold)*100:.1f}%
"""

    md_path.write_text(md_content, encoding="utf-8")
    print(f"Report written to {md_path}")


if __name__ == "__main__":
    root = Path(__file__).resolve().parent.parent.parent
    run_benchmark(root)
