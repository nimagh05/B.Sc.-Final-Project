#!/usr/bin/env python3
"""
Embedding and retrieval benchmark using Ollama bge-m3 and pre-computed chunks (T06).
Supports:
  - Loading pre-computed chunks (--chunks path/to/chunks.json) from C# chunker probe
  - Fallback PyMuPDF document chunking for baseline comparison
  - SHA-256 text-hash caching (--hash-cache) with incremental per-batch save (batch <= 4, num_thread 3)
  - Split-aware retrieval metrics (Recall@1, 3, 5, 10, MRR on dev, test, all)
  - Separate reporting for target questions: q016, q034, q050, q055, q056
  - Top-1 similarity distribution on dev split (answerable vs not-in-docs)
  - Chunk count and token size statistics per arm (min, max, mean, median, P90, P95, pages/chunk)
Outputs:
  - eval/experiments/out/embed_results_<tag>.csv
  - eval/experiments/out/embed_results_<tag>.md
"""

import argparse
import csv
import hashlib
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
DEFAULT_BATCH_SIZE = 4
TARGET_QUESTION_IDS = ["q016", "q034", "q050", "q055", "q056"]


def normalize_for_embedding(text: str) -> str:
    if not text:
        return ""

    # 1. NFKC
    text = unicodedata.normalize("NFKC", text)

    # 2. Remove bidi & invisible controls except ZWNJ U+200C
    bad_chars = [
        "\u200b", "\u200e", "\u200f", "\u202a", "\u202b", "\u202c", "\u202d", "\u202e",
        "\u2066", "\u2067", "\u2068", "\u2069", "\ufeff", "\u00ad",
    ]
    for bc in bad_chars:
        text = text.replace(bc, "")

    # 3. Newlines & unicode space separators
    text = text.replace("\r\n", "\n").replace("\r", "\n")
    text = re.sub(r"[\t\u00a0\u1680\u2000-\u200a\u202f\u205f\u3000]", " ", text)

    # 4. Letters unification: ي->ی, ى->ی, ك->ک, ة->ه
    trans_letters = str.maketrans({
        "\u064a": "ی",  # Arabic Yeh
        "\u0649": "ی",  # Alef Maksura
        "\u0643": "ک",  # Arabic Kaf
        "\u0629": "ه",  # Teh Marbuta
    })
    text = text.translate(trans_letters)

    # 5. Remove tatweel U+0640
    text = text.replace("\u0640", "")

    # 6. Remove Arabic diacritics: U+064B..U+065F and U+0670
    text = re.sub(r"[\u064b-\u065f\u0670]", "", text)

    # 7. Digits: Persian ۰-۹ (06F0-06F9) & Arabic-Indic ٠-٩ (0660-0669) -> ASCII 0-9
    trans_digits = {0x06F0 + i: str(i) for i in range(10)}
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


def clean_headers_footers_legacy(pages_text: list[str]) -> list[str]:
    """Clean repeating headers and footers across pages if >= 4 pages (legacy Python)."""
    if len(pages_text) < 4:
        return pages_text

    cleaned_pages = []
    for p in pages_text:
        lines = p.split("\n")
        kept = []
        for line in lines:
            if re.match(r"^\s*[0-9\-\–\—\s]+\s*$", line) or re.match(r"^\s*(صفحه|page)\s*[0-9]+\s*$", line, re.IGNORECASE):
                continue
            kept.append(line)
        cleaned_pages.append("\n".join(kept))
    return cleaned_pages


def chunk_document_legacy(pdf_name: str, pages_text: list[str], max_tokens: int = 500) -> list[dict]:
    """Legacy fixed-size chunker used in baseline (Arm a)."""
    cleaned_pages = clean_headers_footers_legacy(pages_text)
    chunks = []

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

        paras = [p.strip() for p in page_norm.split("\n\n") if p.strip()]
        for para in paras:
            sentences = re.split(r"(?<=[\.\!\?؟؛])\s+", para)
            for sent in sentences:
                sent = sent.strip()
                if not sent:
                    continue

                sent_tok = est_tokens(sent)
                if sent_tok > max_tokens:
                    words = sent.split()
                    sub_len = int(max_tokens / 1.8)
                    for i in range(0, len(words), sub_len):
                        sub_sent = " ".join(words[i : i + sub_len])
                        chunks.append({
                            "pdf": pdf_name,
                            "chunk_id": f"{pdf_name}_{chunk_index}",
                            "page_start": page_num,
                            "page_end": page_num,
                            "text": sub_sent,
                            "tokens": est_tokens(sub_sent),
                        })
                        chunk_index += 1
                    continue

                if current_tokens + sent_tok > max_tokens and current_units:
                    chunk_text = " ".join(current_units)
                    chunks.append({
                        "pdf": pdf_name,
                        "chunk_id": f"{pdf_name}_{chunk_index}",
                        "page_start": page_start,
                        "page_end": page_end,
                        "text": chunk_text,
                        "tokens": current_tokens,
                    })
                    chunk_index += 1

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
        chunks.append({
            "pdf": pdf_name,
            "chunk_id": f"{pdf_name}_{chunk_index}",
            "page_start": page_start,
            "page_end": page_end,
            "text": chunk_text,
            "tokens": current_tokens,
        })

    return chunks


def get_embeddings_hash_cached(
    texts: list[str],
    hash_cache_file: Path,
    batch_size: int = DEFAULT_BATCH_SIZE,
    model: str = EMBED_MODEL,
    ollama_url: str = OLLAMA_EMBED_URL,
) -> tuple[np.ndarray, float]:
    """Embeds a list of texts using SHA-256 text-hash caching across arms.
    Saves incrementally after every batch to prevent loss on interruption.
    """
    t0 = time.perf_counter()
    cache_dict: dict[str, np.ndarray] = {}

    if hash_cache_file.exists():
        try:
            cached_data = np.load(hash_cache_file)
            if "hashes" in cached_data and "embeddings" in cached_data:
                c_hashes = [h.decode("utf-8") if isinstance(h, bytes) else str(h) for h in cached_data["hashes"]]
                c_embs = cached_data["embeddings"]
                for h, emb in zip(c_hashes, c_embs):
                    cache_dict[h] = emb
                print(f"Loaded {len(cache_dict)} cached embeddings from {hash_cache_file.name}", flush=True)
        except Exception as e:
            print(f"Cache load warning: {e}", flush=True)

    text_hashes = [hashlib.sha256(t.encode("utf-8")).hexdigest() for t in texts]
    missing_indices = [i for i, h in enumerate(text_hashes) if h not in cache_dict]
    print(
        f"Total texts: {len(texts)} | Cache hits: {len(texts) - len(missing_indices)} | Needs Ollama embedding: {len(missing_indices)}",
        flush=True,
    )

    if missing_indices:
        missing_texts = [texts[i] for i in missing_indices]
        missing_hashes = [text_hashes[i] for i in missing_indices]
        total_batches = (len(missing_texts) + batch_size - 1) // batch_size

        for b_idx in range(0, len(missing_texts), batch_size):
            batch_texts = missing_texts[b_idx : b_idx + batch_size]
            batch_hashes = missing_hashes[b_idx : b_idx + batch_size]
            batch_num = (b_idx // batch_size) + 1

            for attempt in range(5):
                try:
                    resp = requests.post(
                        ollama_url,
                        json={
                            "model": model,
                            "input": batch_texts,
                            "options": {"num_thread": 3},
                        },
                        timeout=300,
                    )
                    if resp.status_code == 200:
                        data = resp.json()
                        embs = data.get("embeddings", [])
                        for h, emb in zip(batch_hashes, embs):
                            arr = np.array(emb, dtype=np.float32)
                            norm = np.linalg.norm(arr)
                            if norm > 0:
                                arr = arr / norm
                            cache_dict[h] = arr

                        print(
                            f"  Batch {batch_num}/{total_batches} ({min(b_idx + batch_size, len(missing_texts))}/{len(missing_texts)}) embedded in {time.perf_counter()-t0:.1f}s",
                            flush=True,
                        )

                        # Incremental save after EVERY batch
                        try:
                            sorted_hashes = list(cache_dict.keys())
                            sorted_embs = np.array([cache_dict[h] for h in sorted_hashes], dtype=np.float32)
                            np.savez_compressed(hash_cache_file, hashes=sorted_hashes, embeddings=sorted_embs)
                        except Exception as e:
                            print(f"Warning saving hash cache: {e}", flush=True)

                        time.sleep(0.5)
                        break
                    else:
                        print(f"Warning: HTTP {resp.status_code} from embed API: {resp.text}", flush=True)
                        time.sleep(3)
                except Exception as e:
                    print(f"Warning: embed error on attempt {attempt+1}: {e}", flush=True)
                    time.sleep(3)
            else:
                raise RuntimeError(f"Failed to embed batch starting at index {b_idx}")

    # Build final normalized embedding matrix in input order
    final_embs = np.array([cache_dict[h] for h in text_hashes], dtype=np.float32)
    elapsed = time.perf_counter() - t0
    return final_embs, elapsed


def run_benchmark(
    repo_root: Path,
    chunks_path: Path | None = None,
    hash_cache_path: Path | None = None,
    batch_size: int = DEFAULT_BATCH_SIZE,
    output_tag: str | None = None,
):
    out_dir = repo_root / "eval" / "experiments" / "out"
    extract_dir = out_dir / "extract" / "pymupdf"
    questions_file = repo_root / "eval" / "questions.json"

    if hash_cache_path is None:
        hash_cache_path = out_dir / "embed_cache_by_hash.npz"

    if output_tag is None:
        if chunks_path:
            output_tag = chunks_path.stem.replace("chunks_", "")
        else:
            output_tag = "baseline"

    print(f"=== Running Retrieval Benchmark (Tag: {output_tag}) ===")
    print("--- Step 1: Loading Chunks ---")

    if chunks_path and chunks_path.exists():
        print(f"Loading pre-computed chunks from: {chunks_path}")
        with open(chunks_path, "r", encoding="utf-8") as f:
            all_chunks = json.load(f)
    else:
        print("No --chunks specified or file missing. Using legacy baseline Python chunker on PyMuPDF extracts.")
        all_chunks = []
        for pdf_folder in sorted(extract_dir.glob("*")):
            if not pdf_folder.is_dir():
                continue
            pdf_name = pdf_folder.name
            page_files = sorted(pdf_folder.glob("*.txt"), key=lambda p: int(p.stem))
            pages_text = [pf.read_text(encoding="utf-8") for pf in page_files]
            chunks = chunk_document_legacy(pdf_name, pages_text, max_tokens=500)
            all_chunks.extend(chunks)

    print(f"Total chunks loaded: {len(all_chunks)}")

    # Chunk token & page stats
    tokens_list = [c.get("tokens", 0) for c in all_chunks]
    pages_list = [c["page_end"] - c["page_start"] + 1 for c in all_chunks]

    min_tok = int(np.min(tokens_list)) if tokens_list else 0
    max_tok = int(np.max(tokens_list)) if tokens_list else 0
    mean_tok = float(np.mean(tokens_list)) if tokens_list else 0.0
    median_tok = float(np.median(tokens_list)) if tokens_list else 0.0
    p90_tok = float(np.percentile(tokens_list, 90)) if tokens_list else 0.0
    p95_tok = float(np.percentile(tokens_list, 95)) if tokens_list else 0.0

    mean_pages = float(np.mean(pages_list)) if pages_list else 0.0
    median_pages = float(np.median(pages_list)) if pages_list else 0.0
    max_pages = int(np.max(pages_list)) if pages_list else 0
    chunks_ge_4_pages = sum(1 for p in pages_list if p >= 4)
    pct_ge_4_pages = (chunks_ge_4_pages * 100.0 / len(all_chunks)) if all_chunks else 0.0

    over_500 = sum(1 for t in tokens_list if t > 500)

    print("\n--- Chunker Statistics ---")
    print(f"  Total Chunks:       {len(all_chunks)}")
    print(f"  Tokens (estimated): Min={min_tok}, Max={max_tok}, Mean={mean_tok:.1f}, Median={median_tok:.1f}, P90={p90_tok:.1f}, P95={p95_tok:.1f}")
    print(f"  Pages per Chunk:    Mean={mean_pages:.2f}, Median={median_pages:.1f}, Max={max_pages}")
    print(f"  Chunks span >= 4p:  {chunks_ge_4_pages} ({pct_ge_4_pages:.2f}%)")
    print(f"  Chunks > 500:       {over_500} ({(over_500*100.0/len(all_chunks)):.2f}%)")

    print(f"\n--- Step 2: Embedding {len(all_chunks)} Chunks with {EMBED_MODEL} ---")
    chunk_texts = [c["text"] for c in all_chunks]
    chunk_embeddings, chunk_time = get_embeddings_hash_cached(
        chunk_texts,
        hash_cache_file=hash_cache_path,
        batch_size=batch_size,
    )
    mean_chunk_time = chunk_time / len(all_chunks) if all_chunks else 0.0
    print(f"Chunk embedding ready in {chunk_time:.2f}s ({mean_chunk_time:.4f}s per chunk). Dimension: {chunk_embeddings.shape[1]}")

    print("\n--- Step 3: Loading Questions and Embedding ---")
    with open(questions_file, "r", encoding="utf-8") as f:
        questions = json.load(f)

    q_texts = [normalize_for_embedding(q["question"]) for q in questions]
    q_embeddings, q_time = get_embeddings_hash_cached(
        q_texts,
        hash_cache_file=hash_cache_path,
        batch_size=batch_size,
    )
    print(f"Questions embedding ready in {q_time:.2f}s.")

    print("\n--- Step 4: Computing Cosine Similarity & Retrieval Metrics ---")
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
            "strict_hits_1": 0,
            "strict_hits_3": 0,
            "strict_hits_5": 0,
            "strict_hits_10": 0,
            "strict_mrr_sum": 0.0,
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
        first_hit_span = None
        rank_first_hit_strict = None

        for rank, c_idx in enumerate(sorted_indices, start=1):
            chunk = all_chunks[c_idx]
            if is_ans and chunk["pdf"] == src_file:
                c_range = set(range(chunk["page_start"], chunk["page_end"] + 1))
                if c_range.intersection(src_pages):
                    span = chunk["page_end"] - chunk["page_start"] + 1
                    if rank_first_hit is None:
                        rank_first_hit = rank
                        first_hit_span = span
                    if rank_first_hit_strict is None and span <= 3:
                        rank_first_hit_strict = rank
                    if rank_first_hit is not None and rank_first_hit_strict is not None:
                        break

        hit1 = is_ans and (rank_first_hit == 1)
        hit3 = is_ans and (rank_first_hit is not None and rank_first_hit <= 3)
        hit5 = is_ans and (rank_first_hit is not None and rank_first_hit <= 5)
        hit10 = is_ans and (rank_first_hit is not None and rank_first_hit <= 10)
        rr = (1.0 / rank_first_hit) if (is_ans and rank_first_hit is not None) else 0.0

        strict_hit1 = is_ans and (rank_first_hit_strict == 1)
        strict_hit3 = is_ans and (rank_first_hit_strict is not None and rank_first_hit_strict <= 3)
        strict_hit5 = is_ans and (rank_first_hit_strict is not None and rank_first_hit_strict <= 5)
        strict_hit10 = is_ans and (rank_first_hit_strict is not None and rank_first_hit_strict <= 10)
        strict_rr = (1.0 / rank_first_hit_strict) if (is_ans and rank_first_hit_strict is not None) else 0.0

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

                if strict_hit1:
                    metrics_by_split[s]["strict_hits_1"] += 1
                if strict_hit3:
                    metrics_by_split[s]["strict_hits_3"] += 1
                if strict_hit5:
                    metrics_by_split[s]["strict_hits_5"] += 1
                if strict_hit10:
                    metrics_by_split[s]["strict_hits_10"] += 1
                metrics_by_split[s]["strict_mrr_sum"] += strict_rr

        results_records.append({
            "id": qid,
            "split": q_split,
            "type": q_type,
            "is_answerable": is_ans,
            "source_file": src_file,
            "source_pages": list(src_pages),
            "top1_similarity": round(top1_sim, 4),
            "rank_first_hit": rank_first_hit if rank_first_hit else -1,
            "first_hit_span": first_hit_span if first_hit_span is not None else -1,
            "rank_first_hit_strict": rank_first_hit_strict if rank_first_hit_strict else -1,
            "hit@1": hit1,
            "hit@3": hit3,
            "hit@5": hit5,
            "hit@10": hit10,
            "reciprocal_rank": round(rr, 4),
        })

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

        sr1 = (m["strict_hits_1"] / ans_n) if ans_n > 0 else 0.0
        sr3 = (m["strict_hits_3"] / ans_n) if ans_n > 0 else 0.0
        sr5 = (m["strict_hits_5"] / ans_n) if ans_n > 0 else 0.0
        sr10 = (m["strict_hits_10"] / ans_n) if ans_n > 0 else 0.0
        smrr = (m["strict_mrr_sum"] / ans_n) if ans_n > 0 else 0.0

        print(
            f"Split: {s.upper():4s} | Answerable Qs: {ans_n:2d} | "
            f"Recall@1: {r1*100:5.1f}% | Recall@3: {r3*100:5.1f}% | Recall@5: {r5*100:5.1f}% | "
            f"Recall@10: {r10*100:5.1f}% | MRR: {mrr:.4f}"
        )
        print(
            f"       (Strict span<=3) | "
            f"Recall@1: {sr1*100:5.1f}% | Recall@3: {sr3*100:5.1f}% | Recall@5: {sr5*100:5.1f}% | "
            f"Recall@10: {sr10*100:5.1f}% | MRR: {smrr:.4f}"
        )
        summary_rows.append({
            "split": s,
            "answerable_questions": ans_n,
            "not_in_docs_questions": m["not_q"],
            "recall@1": round(r1, 4),
            "recall@3": round(r3, 4),
            "recall@5": round(r5, 4),
            "recall@10": round(r10, 4),
            "mrr": round(mrr, 4),
            "strict_recall@1": round(sr1, 4),
            "strict_recall@3": round(sr3, 4),
            "strict_recall@5": round(sr5, 4),
            "strict_recall@10": round(sr10, 4),
            "strict_mrr": round(smrr, 4),
            "mean_top1_ans": round(float(np.mean(m["top1_ans_scores"])), 4) if m["top1_ans_scores"] else 0.0,
            "mean_top1_not": round(float(np.mean(m["top1_not_scores"])), 4) if m["top1_not_scores"] else 0.0,
        })

    # Target Questions breakdown (q016, q034, q050, q055, q056)
    print("\n--- Target Questions Performance (q016, q034, q050, q055, q056) ---")
    target_results = [r for r in results_records if r["id"] in TARGET_QUESTION_IDS]
    for r in target_results:
        print(
            f"  {r['id']:5s} | Split: {r['split']:4s} | Rank: {r['rank_first_hit']:2d} | "
            f"Hit@1: {str(r['hit@1']):5s} | Hit@3: {str(r['hit@3']):5s} | Hit@5: {str(r['hit@5']):5s} | "
            f"RR: {r['reciprocal_rank']:.4f} | Top-1 Sim: {r['top1_similarity']:.4f}"
        )

    # Top-1 Similarity Distribution on DEV split
    dev_ans_scores = np.array(metrics_by_split["dev"]["top1_ans_scores"])
    dev_not_scores = np.array(metrics_by_split["dev"]["top1_not_scores"])
    print("\n--- Dev Split Top-1 Similarity Distribution ---")
    if len(dev_ans_scores) > 0:
        print(
            f"  Answerable (n={len(dev_ans_scores)}): Mean={np.mean(dev_ans_scores):.4f}, "
            f"Std={np.std(dev_ans_scores):.4f}, Min={np.min(dev_ans_scores):.4f}, "
            f"Max={np.max(dev_ans_scores):.4f}, Median={np.median(dev_ans_scores):.4f}"
        )
    if len(dev_not_scores) > 0:
        print(
            f"  Not-in-docs (n={len(dev_not_scores)}): Mean={np.mean(dev_not_scores):.4f}, "
            f"Std={np.std(dev_not_scores):.4f}, Min={np.min(dev_not_scores):.4f}, "
            f"Max={np.max(dev_not_scores):.4f}, Median={np.median(dev_not_scores):.4f}"
        )

    # Save detailed CSV
    csv_filename = f"embed_results_{output_tag}.csv" if output_tag != "baseline" else "embed_results.csv"
    csv_path = out_dir / csv_filename
    with open(csv_path, "w", encoding="utf-8", newline="") as f:
        fieldnames = [
            "id", "split", "type", "is_answerable", "source_file", "source_pages",
            "top1_similarity", "rank_first_hit", "first_hit_span", "rank_first_hit_strict",
            "hit@1", "hit@3", "hit@5", "hit@10", "reciprocal_rank",
        ]
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        for r in results_records:
            writer.writerow(r)
    print(f"\nSaved embed results to {csv_path}")

    # Generate Markdown Report
    md_filename = f"embed_results_{output_tag}.md" if output_tag != "baseline" else "embed_results.md"
    md_path = out_dir / md_filename
    dev_summary = next(s for s in summary_rows if s["split"] == "dev")
    test_summary = next(s for s in summary_rows if s["split"] == "test")
    all_summary = next(s for s in summary_rows if s["split"] == "all")

    md_content = f"""# Embedding & Retrieval Benchmark Results ({output_tag})

**Model:** `{EMBED_MODEL}` (Dimensions: 1024)  
**Total Chunks:** {len(all_chunks)}  
**Token Statistics (estimated):** Min={min_tok}, Max={max_tok}, Mean={mean_tok:.1f}, Median={median_tok:.1f}, P90={p90_tok:.1f}, P95={p95_tok:.1f}  
**Pages per Chunk:** Mean={mean_pages:.2f}, Median={median_pages:.1f}, Max={max_pages}  
**Chunks spanning >= 4 pages:** {chunks_ge_4_pages} ({pct_ge_4_pages:.2f}%)  
**Total Questions Evaluated:** {len(questions)}  

---

## 1. Retrieval Performance by Split (Answerable Questions)

| Split | Q Count | Recall@1 | Recall@3 | Recall@5 | Recall@10 | MRR | Mean Top-1 Sim (Ans) | Mean Top-1 Sim (Not) |
|---|---|---|---|---|---|---|---|---|
| **dev** | {dev_summary['answerable_questions']} | {dev_summary['recall@1']*100:.1f}% | {dev_summary['recall@3']*100:.1f}% | {dev_summary['recall@5']*100:.1f}% | {dev_summary['recall@10']*100:.1f}% | {dev_summary['mrr']:.4f} | {dev_summary['mean_top1_ans']:.4f} | {dev_summary['mean_top1_not']:.4f} |
| **test** | {test_summary['answerable_questions']} | {test_summary['recall@1']*100:.1f}% | {test_summary['recall@3']*100:.1f}% | {test_summary['recall@5']*100:.1f}% | {test_summary['recall@10']*100:.1f}% | {test_summary['mrr']:.4f} | {test_summary['mean_top1_ans']:.4f} | {test_summary['mean_top1_not']:.4f} |
| **all** | {all_summary['answerable_questions']} | {all_summary['recall@1']*100:.1f}% | {all_summary['recall@3']*100:.1f}% | {all_summary['recall@5']*100:.1f}% | {all_summary['recall@10']*100:.1f}% | {all_summary['mrr']:.4f} | {all_summary['mean_top1_ans']:.4f} | {all_summary['mean_top1_not']:.4f} |

### Strict Hit Rule (Page overlap AND span <= 3 pages)

| Split | Q Count | Recall@1 | Recall@3 | Recall@5 | Recall@10 | MRR |
|---|---|---|---|---|---|---|
| **dev** | {dev_summary['answerable_questions']} | {dev_summary['strict_recall@1']*100:.1f}% | {dev_summary['strict_recall@3']*100:.1f}% | {dev_summary['strict_recall@5']*100:.1f}% | {dev_summary['strict_recall@10']*100:.1f}% | {dev_summary['strict_mrr']:.4f} |
| **test** | {test_summary['answerable_questions']} | {test_summary['strict_recall@1']*100:.1f}% | {test_summary['strict_recall@3']*100:.1f}% | {test_summary['strict_recall@5']*100:.1f}% | {test_summary['strict_recall@10']*100:.1f}% | {test_summary['strict_mrr']:.4f} |
| **all** | {all_summary['answerable_questions']} | {all_summary['strict_recall@1']*100:.1f}% | {all_summary['strict_recall@3']*100:.1f}% | {all_summary['strict_recall@5']*100:.1f}% | {all_summary['strict_recall@10']*100:.1f}% | {all_summary['strict_mrr']:.4f} |

---

## 2. Target Questions Breakdown (q016, q034, q050, q055, q056)

| ID | Split | Rank | Hit@1 | Hit@3 | Hit@5 | Reciprocal Rank | Top-1 Similarity |
|---|---|---|---|---|---|---|---|
"""
    for r in target_results:
        md_content += f"| {r['id']} | {r['split']} | {r['rank_first_hit']} | {r['hit@1']} | {r['hit@3']} | {r['hit@5']} | {r['reciprocal_rank']:.4f} | {r['top1_similarity']:.4f} |\n"

    md_content += f"""
---

## 3. Dev Split Top-1 Similarity Distribution

- **Answerable Questions (n={len(dev_ans_scores)}):**
  - Mean: `{np.mean(dev_ans_scores):.4f}`
  - Std:  `{np.std(dev_ans_scores):.4f}`
  - Min:  `{np.min(dev_ans_scores):.4f}`
  - Max:  `{np.max(dev_ans_scores):.4f}`
  - Median: `{np.median(dev_ans_scores):.4f}`
- **Not-in-docs Questions (n={len(dev_not_scores)}):**
  - Mean: `{np.mean(dev_not_scores):.4f}`
  - Std:  `{np.std(dev_not_scores):.4f}`
  - Min:  `{np.min(dev_not_scores):.4f}`
  - Max:  `{np.max(dev_not_scores):.4f}`
  - Median: `{np.median(dev_not_scores):.4f}`
"""

    md_path.write_text(md_content, encoding="utf-8")
    print(f"Report written to {md_path}")


def main():
    parser = argparse.ArgumentParser(description="Embedding & retrieval benchmark for DocSearch")
    parser.add_argument("--chunks", type=Path, default=None, help="Path to pre-computed JSON chunks file")
    parser.add_argument("--hash-cache", type=Path, default=None, help="Path to .npz file storing hash-based embedding cache")
    parser.add_argument("--batch-size", type=int, default=DEFAULT_BATCH_SIZE, help="Batch size for embedding (<= 4)")
    parser.add_argument("--tag", type=str, default=None, help="Output tag for naming output files")
    args = parser.parse_args()

    root = Path(__file__).resolve().parent.parent.parent
    run_benchmark(
        repo_root=root,
        chunks_path=args.chunks,
        hash_cache_path=args.hash_cache,
        batch_size=args.batch_size,
        output_tag=args.tag,
    )


if __name__ == "__main__":
    main()
