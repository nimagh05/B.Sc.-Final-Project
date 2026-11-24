#!/usr/bin/env python3
"""Stage 2 End-to-End Evaluation, Offline Threshold Simulation, and Reporting Module (T19b).

Evaluates the complete DocSearch RAG pipeline (POST /api/v1/ask) across multi-arm benchmarks:
- Pre-ask retrieval diagnostics via POST /api/v1/dev/retrieve.
- Exact-order deterministic content-hash caching with system prompt hash.
- Strict non-degraded failure policy (4 retries, failed_provider tracking, halt on 3 consecutive).
- Offline 3-threshold simulation (prod_055, calibrated_tau, ungated).
- Live validation cross-check (--validate-offline-sim).
- Normalized Keyword Recall, non-vacuous Multi-Digit Number Recall & Unit Recall (strict & proximity).
- All 4 citation metrics (Validity, Page-Hit, Precision, Recall).
- Wilson score CIs for small proportions, McNemar exact test for paired binary arm comparisons,
  and G < 5 cluster bootstrap guards.
- Headless Matplotlib publication plots.
"""

import csv
import datetime
import hashlib
import json
import math
import re
import subprocess
import sys
import time
import unicodedata
from collections import defaultdict
from pathlib import Path
from typing import Any, Dict, List, Optional, Set, Tuple

import numpy as np
import requests

# Set headless Matplotlib backend before importing pyplot
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

REPO_ROOT = Path(__file__).resolve().parent.parent


def get_git_sha() -> str:
    try:
        return subprocess.check_output(["git", "rev-parse", "HEAD"], stderr=subprocess.DEVNULL, text=True).strip()
    except Exception:
        return "unknown"

# --- Normalization Maps & Regexes matching C# and T12/ADR 0008 ---

DIGIT_MAP = str.maketrans("۰۱۲۳۴۵۶۷۸۹٠١٢٣٤٥٦٧٨٩", "01234567890123456789")
CHAR_MAP = str.maketrans({
    "ي": "ی", "ى": "ی", "ئ": "ی",
    "ك": "ک",
    "ۀ": "ه", "ة": "ه",
    "أ": "ا", "إ": "ا", "آ": "ا",
    "ؤ": "و",
    "\u0640": "",
})
DIACRITICS_RE = re.compile(r"[\u064B-\u065F\u0670]")
THOUSANDS_RE = re.compile(r"(?<!\d)(\d{1,3}(?:[,٬/]\d{3})+)(?!\d)")
UNIT_PHRASE_RE = re.compile(
    r"(?<!\d)(\d+(?:[./]\d+)?)\s*(سال|ماه|هفته|روز|ساعت|درصد|%|واحد|نفر|صفحه|کلمه|مقاله|میلیون|ریال|دلار|نیمسال|نمره)"
)
MULTI_DIGIT_RE = re.compile(r"(?<!\d)(\d{2,})(?!\d)")
PERSIAN_CHAR_RE = re.compile(r"[\u0600-\u06FF\u0750-\u077F\uFB50-\uFDFF\uFE70-\uFEFF]")
CITATION_BRACKET_RE = re.compile(r"\[(\d+)\]")

PERSIAN_REFUSAL_SENTENCE = "«در اسناد موجود پاسخی برای این پرسش پیدا نشد.»"
PERSIAN_REFUSAL_CORE = "در اسناد موجود پاسخی برای این پرسش پیدا نشد"

PRIMARY_CMAP_FILES: Set[str] = {
    "bedoon-factor.pdf",
    "dastyar-pazhuheshi.pdf",
    "hamayesh.pdf",
    "kod-akhlaghi.pdf",
    "tarh-dakheli.pdf",
    "forsat-motaleati.pdf",
}
HELDOUT_CMAP_FILES: Set[str] = {"tarh-dakheli.pdf", "forsat-motaleati.pdf"}
UNIT_ONLY_QS: Set[str] = {"q022", "q097", "q100", "q102", "q103"}


def normalize_text(s: str) -> str:
    """Matches C# PersianTextNormalizer and eval/audit_text_layer.py."""
    if not s:
        return ""
    s = unicodedata.normalize("NFKC", str(s)).lower()
    s = s.translate(DIGIT_MAP).translate(CHAR_MAP)
    s = DIACRITICS_RE.sub("", s)
    s = s.replace("\u200c", " ")
    s = THOUSANDS_RE.sub(lambda m: re.sub(r"[,٬/]", "", m.group(1)), s)
    s = re.sub(r"\s+", " ", s).strip()
    return s


def get_system_prompt_sha256(repo_root: Optional[Path] = None, version: str = "v1") -> str:
    root = repo_root or REPO_ROOT
    prompt_file = root / "src" / "DocSearch.Application" / "Prompts" / f"system.{version}.txt"
    if prompt_file.exists():
        return hashlib.sha256(prompt_file.read_bytes()).hexdigest()
    # Fallback sha if file cannot be read directly
    return "prompt_file_not_found"


# --- Deterministic Content-Hash Cache ---

def compute_cache_key(
    normalized_question: str,
    chunk_ids: List[str],
    top_k: int,
    chat_model: str,
    temperature: float,
    prompt_sha256: str,
) -> str:
    """Exact-order chunk ID formula per user specification."""
    ordered_chunks_str = ",".join(chunk_ids)
    key_str = (
        f"{normalized_question}|{ordered_chunks_str}|"
        f"top_k={top_k}|chat_model={chat_model}|"
        f"temperature={temperature:.4f}|prompt_sha256={prompt_sha256}"
    )
    return hashlib.sha256(key_str.encode("utf-8")).hexdigest()


def get_from_cache(cache_dir: Path, cache_key: str) -> Optional[Dict[str, Any]]:
    cache_file = cache_dir / f"{cache_key}.json"
    if cache_file.exists():
        try:
            with open(cache_file, "r", encoding="utf-8") as f:
                data = json.load(f)
                data["is_cached"] = True
                return data
        except Exception:
            return None
    return None


def save_to_cache(cache_dir: Path, cache_key: str, data: Dict[str, Any]) -> None:
    # Strict invariant: never cache degraded responses or empty answers
    if data.get("degraded", False):
        return
    cache_dir.mkdir(parents=True, exist_ok=True)
    cache_file = cache_dir / f"{cache_key}.json"
    try:
        with open(cache_file, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
    except Exception as e:
        print(f"[WARN] Failed to write cache entry {cache_key}: {e}")


# --- JSONL Checkpoint Manager ---

def load_checkpoint(checkpoint_path: Path) -> Dict[str, Dict[str, Any]]:
    records = {}
    if checkpoint_path.exists():
        try:
            with open(checkpoint_path, "r", encoding="utf-8") as f:
                for line in f:
                    line = line.strip()
                    if line:
                        rec = json.loads(line)
                        if "id" in rec and not rec.get("degraded", False):
                            records[rec["id"]] = rec
        except Exception as e:
            print(f"[WARN] Failed to load checkpoint {checkpoint_path}: {e}")
    return records


def append_checkpoint(checkpoint_path: Path, record: Dict[str, Any]) -> None:
    # Strict invariant: never checkpoint degraded responses
    if record.get("degraded", False):
        return
    checkpoint_path.parent.mkdir(parents=True, exist_ok=True)
    with open(checkpoint_path, "a", encoding="utf-8") as f:
        f.write(json.dumps(record, ensure_ascii=False) + "\n")
        f.flush()


# --- Offline 3-Threshold Simulation ---

def simulate_threshold(
    item: Dict[str, Any],
    tau: float,
    refusal_sentence: str = PERSIAN_REFUSAL_SENTENCE,
) -> Dict[str, Any]:
    """Simulates pre-LLM gating offline without duplicate LLM calls."""
    retrieved_count = item.get("retrieved_chunk_count", 0)
    best_score = item.get("bestVectorScore", 0.0)
    has_fts = item.get("hasFtsHit", False)
    raw_not_found = item.get("notFound", False)
    raw_answer = item.get("answer")
    raw_sources = item.get("sources", [])

    if retrieved_count == 0:
        not_found = True
        reason = "NoContext"
        gated = True
    elif best_score < tau and not has_fts:
        not_found = True
        reason = "LowSimilarity"
        gated = True
    else:
        not_found = raw_not_found
        reason = "ModelRefusal" if raw_not_found else "None"
        gated = False

    ans = raw_answer if not not_found else refusal_sentence
    sources = raw_sources if not not_found else []

    # Preserve non-vacuous denominators: if refused, convert hit to False if was not None
    if not_found:
        kw_hit = False
        num_hit = False if item.get("num_hit") is not None else None
        unit_hit_strict = False if item.get("unit_hit_strict") is not None else None
        unit_hit_prox = False if item.get("unit_hit_proximity_10") is not None else None
        cit_validity = False
        cit_page_hit = False
        cit_prec = 0.0
        cit_rec = 0.0
    else:
        kw_hit = bool(item.get("kw_hit", False))
        num_hit = item.get("num_hit")
        unit_hit_strict = item.get("unit_hit_strict")
        unit_hit_prox = item.get("unit_hit_proximity_10")
        cit_validity = bool(item.get("citation_validity", False))
        cit_page_hit = bool(item.get("citation_page_hit", False))
        cit_prec = float(item.get("citation_precision", 0.0))
        cit_rec = float(item.get("citation_recall", 0.0))

    res = dict(item)
    res.update({
        "notFound": not_found,
        "answer": ans,
        "sources": sources,
        "no_answer_reason": reason,
        "gated": gated,
        "kw_hit": kw_hit,
        "num_hit": num_hit,
        "unit_hit_strict": unit_hit_strict,
        "unit_hit_proximity_10": unit_hit_prox,
        "citation_validity": cit_validity,
        "citation_page_hit": cit_page_hit,
        "citation_precision": cit_prec,
        "citation_recall": cit_rec,
    })
    return res


# --- Generation & Citation Scoring ---

def is_chunk_hit(
    chunk_file: str,
    page_start: int,
    page_end: int,
    gold_file: Optional[str],
    gold_pages: List[int],
    twin_mapping: Optional[Dict[str, str]] = None,
) -> bool:
    if not gold_file or not gold_pages:
        return False
    effective_file = chunk_file
    if twin_mapping and chunk_file in twin_mapping:
        effective_file = twin_mapping[chunk_file]
    if effective_file != gold_file:
        return False
    gold_min, gold_max = min(gold_pages), max(gold_pages)
    return max(page_start, gold_min) <= min(page_end, gold_max)


def evaluate_generation_metrics(
    answer: Optional[str],
    not_found: bool,
    expected_answer: Optional[str],
    answer_keywords: List[str],
    sources: List[Dict[str, Any]],
    gold_file: Optional[str],
    gold_pages: List[int],
    twin_mapping: Optional[Dict[str, str]] = None,
) -> Dict[str, Any]:
    """Computes Normalized Keyword Recall, non-vacuous Number/Unit Recall, and all 4 Citation metrics."""
    norm_expected = normalize_text(expected_answer or "")
    expected_nums = sorted(set(MULTI_DIGIT_RE.findall(norm_expected)))
    unit_phrases = [f"{m[0]} {m[1]}" for m in UNIT_PHRASE_RE.findall(norm_expected)]

    if not_found or not answer:
        return {
            "kw_hit": False,
            "num_hit": False if expected_nums else None,
            "unit_hit_strict": False if unit_phrases else None,
            "unit_hit_proximity_10": False if unit_phrases else None,
            "citation_validity": False,
            "citation_page_hit": False,
            "citation_precision": 0.0,
            "citation_recall": 0.0,
            "cited_source_count": 0,
            "valid_gold_hits_count": 0,
        }

    norm_ans = normalize_text(answer)

    # 1. Normalized Keyword Recall (all keywords present)
    if answer_keywords:
        kw_hits = sum(1 for kw in answer_keywords if normalize_text(kw) in norm_ans)
        kw_hit = (kw_hits == len(answer_keywords))
    else:
        kw_hit = True

    # 2. Multi-Digit Number Recall (>= 2 digits) with non-vacuous denominator
    if expected_nums:
        num_hits = sum(1 for num in expected_nums if re.search(rf"(?<!\d){re.escape(num)}(?!\d)", norm_ans))
        num_hit: Optional[bool] = (num_hits == len(expected_nums))
    else:
        num_hit = None

    # 3. Unit-Phrase Recall (strict and proximity <= 10) with non-vacuous denominator
    if unit_phrases:
        strict_hits = 0
        prox_hits = 0
        for ph in unit_phrases:
            num_part, unit_part = ph.split(" ", 1)
            # Strict match: consecutive number and unit
            pattern_strict = rf"(?<!\d){re.escape(num_part)}\s*{re.escape(unit_part)}"
            if unit_part == "درصد":
                alt_pattern = rf"(?<!\d){re.escape(num_part)}\s*%"
            elif unit_part == "%":
                alt_pattern = rf"(?<!\d){re.escape(num_part)}\s*درصد"
            else:
                alt_pattern = None

            if re.search(pattern_strict, norm_ans) or (alt_pattern and re.search(alt_pattern, norm_ans)):
                strict_hits += 1

            # Proximity match: number and unit co-occur within <= 10 chars
            prox_pattern = rf"(?<!\d){re.escape(num_part)}.{{0,10}}{re.escape(unit_part)}"
            rev_prox_pattern = rf"{re.escape(unit_part)}.{{0,10}}(?<!\d){re.escape(num_part)}"
            if (re.search(prox_pattern, norm_ans) or re.search(rev_prox_pattern, norm_ans) or
                (alt_pattern and (re.search(rf"(?<!\d){re.escape(num_part)}.{{0,10}}%", norm_ans) or
                                  re.search(rf"%.{{0,10}}(?<!\d){re.escape(num_part)}", norm_ans)))):
                prox_hits += 1

        unit_hit_strict: Optional[bool] = (strict_hits == len(unit_phrases))
        unit_hit_prox: Optional[bool] = (prox_hits == len(unit_phrases))
    else:
        unit_hit_strict = None
        unit_hit_prox = None

    # 4. Citations Metrics (Bracket Syntax [n])
    cited_indices = [int(m) for m in CITATION_BRACKET_RE.findall(answer)]
    source_count = len(sources)
    valid_citations = [idx for idx in cited_indices if 1 <= idx <= source_count]

    # Citation Validity: Answer contains >= 1 valid bracket citation pointing to an available source
    citation_validity = len(valid_citations) > 0

    # Map 1-based citation index to source item
    cited_sources = [sources[idx - 1] for idx in valid_citations]
    gold_hit_sources = []
    cited_pages_hit: Set[int] = set()

    for s in cited_sources:
        fn = s.get("fileName", "")
        p = s.get("page", 1)
        if is_chunk_hit(fn, p, p, gold_file, gold_pages, twin_mapping=twin_mapping):
            gold_hit_sources.append(s)
            cited_pages_hit.add(p)

    # Citation Page-Hit Rate (answer-level): >= 1 cited source hits gold file & page
    citation_page_hit = len(gold_hit_sources) > 0

    # Citation Precision (source-level): hits / total cited sources
    citation_precision = (len(gold_hit_sources) / len(cited_sources)) if cited_sources else 0.0

    # Citation Recall (coverage-level): unique gold pages cited / total gold pages
    citation_recall = (len(cited_pages_hit) / len(gold_pages)) if gold_pages else 0.0

    return {
        "kw_hit": kw_hit,
        "num_hit": num_hit,
        "unit_hit_strict": unit_hit_strict,
        "unit_hit_proximity_10": unit_hit_prox,
        "citation_validity": citation_validity,
        "citation_page_hit": citation_page_hit,
        "citation_precision": round(citation_precision, 4),
        "citation_recall": round(citation_recall, 4),
        "cited_source_count": len(cited_sources),
        "valid_gold_hits_count": len(gold_hit_sources),
    }


# --- Statistical Inference Routines ---

def wilson_score_interval(successes: int, total: int, confidence: float = 0.95) -> Tuple[float, float]:
    """Computes Wilson score 95% confidence interval for proportions on small N."""
    if total == 0:
        return (0.0, 0.0)
    z = 1.95996
    p = successes / total
    denom = 1.0 + (z**2) / total
    center = (p + (z**2) / (2.0 * total)) / denom
    half_width = (z * math.sqrt((p * (1.0 - p) + (z**2) / (4.0 * total)) / total)) / denom
    return (max(0.0, round(center - half_width, 4)), min(1.0, round(center + half_width, 4)))


def mcnemar_exact(b: int, c: int) -> float:
    """Exact two-sided binomial p-value for McNemar's paired test on discordant pairs."""
    n = b + c
    if n == 0:
        return 1.0
    k = min(b, c)
    p_one_sided = sum(math.comb(n, i) for i in range(k + 1)) * (0.5 ** n)
    return min(1.0, round(2.0 * p_one_sided, 4))


def paired_bootstrap_ci(
    values_a: List[float],
    values_b: List[float],
    n_resamples: int = 10000,
    seed: int = 42,
) -> Dict[str, Any]:
    """Question-level paired bootstrap 95% confidence interval."""
    assert len(values_a) == len(values_b), "Sample sizes must match"
    n = len(values_a)
    if n == 0:
        return {"mean_delta": 0.0, "ci_low": 0.0, "ci_high": 0.0, "p_value_two_sided": 1.0}

    diffs = np.array(values_b, dtype=float) - np.array(values_a, dtype=float)
    observed_mean = float(np.mean(diffs))

    rng = np.random.default_rng(seed)
    boot_means = np.empty(n_resamples, dtype=float)
    for i in range(n_resamples):
        idx = rng.integers(0, n, size=n)
        boot_means[i] = np.mean(diffs[idx])

    ci_low = float(np.percentile(boot_means, 2.5))
    ci_high = float(np.percentile(boot_means, 97.5))

    p_val = float(np.mean(boot_means <= 0.0)) if observed_mean > 0 else float(np.mean(boot_means >= 0.0))
    p_val_two_sided = min(1.0, 2.0 * p_val)

    return {
        "mean_delta": round(observed_mean, 4),
        "ci_low": round(ci_low, 4),
        "ci_high": round(ci_high, 4),
        "p_value_two_sided": round(p_val_two_sided, 4),
        "significant_at_05": (ci_low > 0.0) or (ci_high < 0.0),
    }


def document_cluster_paired_bootstrap_ci(
    items_a: List[Dict[str, Any]],
    items_b: List[Dict[str, Any]],
    metric_key: str,
    n_resamples: int = 10000,
    seed: int = 42,
) -> Dict[str, Any]:
    """Harmonized cluster bootstrap with ADR 0010 §7.2 / ADR 0011 G < 5 cluster size guard."""
    assert len(items_a) == len(items_b), "Sample sizes must match"

    clusters_a: Dict[str, List[float]] = defaultdict(list)
    clusters_b: Dict[str, List[float]] = defaultdict(list)
    for it_a, it_b in zip(items_a, items_b):
        doc = it_a.get("source_file") or "unknown"
        val_a = float(it_a.get(metric_key, 0.0) or 0.0)
        val_b = float(it_b.get(metric_key, 0.0) or 0.0)
        clusters_a[doc].append(val_a)
        clusters_b[doc].append(val_b)

    cluster_keys = sorted(clusters_a.keys())
    g = len(cluster_keys)

    per_doc_deltas = {}
    for doc in cluster_keys:
        mean_a = float(np.mean(clusters_a[doc]))
        mean_b = float(np.mean(clusters_b[doc]))
        per_doc_deltas[doc] = round(mean_b - mean_a, 4)

    # Cluster Size Guard: G < 5
    if g < 5:
        status_label = "Descriptive only (G < 5 clusters)" if g == 3 else "N/A (G < 5 clusters)"
        return {
            "status": status_label,
            "cluster_count": g,
            "mean_delta": round(float(np.mean(list(per_doc_deltas.values()))), 4) if per_doc_deltas else 0.0,
            "ci_low": None,
            "ci_high": None,
            "per_doc_deltas": per_doc_deltas,
        }

    # Standard cluster bootstrap when G >= 5
    rng = np.random.default_rng(seed)
    boot_means = np.empty(n_resamples, dtype=float)
    cluster_indices = np.arange(g)

    for i in range(n_resamples):
        sampled_indices = rng.choice(cluster_indices, size=g, replace=True)
        sample_a: List[float] = []
        sample_b: List[float] = []
        for idx in sampled_indices:
            doc = cluster_keys[idx]
            sample_a.extend(clusters_a[doc])
            sample_b.extend(clusters_b[doc])
        boot_means[i] = np.mean(sample_b) - np.mean(sample_a)

    observed_delta = float(
        np.mean([float(b.get(metric_key, 0.0) or 0.0) for b in items_b]) -
        np.mean([float(a.get(metric_key, 0.0) or 0.0) for a in items_a])
    )

    return {
        "status": "valid",
        "cluster_count": g,
        "mean_delta": round(observed_delta, 4),
        "ci_low": round(float(np.percentile(boot_means, 2.5)), 4),
        "ci_high": round(float(np.percentile(boot_means, 97.5)), 4),
        "per_doc_deltas": per_doc_deltas,
    }


# --- Stage 2 Execution Harness ---

def run_stage2_eval(args: Any, config: Dict[str, Any], token: str) -> None:
    """Executes Stage 2 ungated evaluation across specified arms."""
    from run_eval import (
        DocSearchClient,
        get_git_sha,
        build_reproducibility_manifest,
        load_collection_manifest,
    )

    # 1. Official Run Guard: enforce clean git working tree
    git_dirty = False
    try:
        import subprocess
        status_out = subprocess.check_output(["git", "status", "--porcelain"], stderr=subprocess.DEVNULL, text=True).strip()
        git_dirty = len(status_out) > 0
    except Exception:
        pass

    if git_dirty and not getattr(args, "allow_dirty", False):
        print("\n[ERROR] Working tree is dirty! Official runs require git_dirty == False.")
        print("        Commit your changes first or pass --allow-dirty for an unofficial run.")
        sys.exit(1)

    # Preflight Check: verify API is live via /health/live (not /health/ready which checks extractor)
    try:
        live_resp = requests.get(f"{args.base_url.rstrip('/')}/health/live", timeout=5.0)
        if live_resp.status_code != 200:
            raise RuntimeError(f"API /health/live returned HTTP {live_resp.status_code}")
    except Exception as ex:
        print(f"\n[FATAL] DocSearch API is not reachable at {args.base_url}: {ex}")
        print("        Ensure API is started in another terminal before running eval.")
        sys.exit(1)

    client = DocSearchClient(args.base_url, token)
    cache_dir = args.cache_dir / "llm_cache"
    cache_dir.mkdir(parents=True, exist_ok=True)
    prompt_sha256 = get_system_prompt_sha256()

    chat_model = args.chat_model
    if not chat_model:
        raise ValueError("Must provide explicit --chat-model matching API's configured Ai__Chat__Model (e.g. gemini-2.5-flash).")

    # Load questions
    with open(args.questions, "r", encoding="utf-8") as f:
        all_questions = json.load(f)

    if args.smoke:
        all_questions = all_questions[:5]
        print(f"[SMOKE TEST] Evaluating {len(all_questions)} questions.")

    # Collection mapping
    manifest_path = args.cache_dir / "collection_manifest.json"
    manifest = load_collection_manifest(manifest_path)
    existing_cols = {c["name"]: c["id"] for c in client.get_collections()}

    col_map = {}
    for col_key, col_meta in config["collections"].items():
        name = col_meta["id_name"]
        if name in manifest and manifest[name].get("id"):
            col_map[col_key] = manifest[name]["id"]
        elif name in existing_cols:
            col_map[col_key] = existing_cols[name]
        else:
            raise RuntimeError(f"Collection '{name}' ({col_key}) not found. Ingest collections first.")

    # Target arms
    selected_arms = [a.strip() for a in args.arms.split(",")] if args.arms else list(config["arms"].keys())
    out_dir = args.out or Path("eval/results") / f"stage2_{datetime.datetime.now().strftime('%Y%m%d_%H%M%S')}"
    out_dir.mkdir(parents=True, exist_ok=True)
    checkpoints_dir = out_dir / "checkpoints"
    checkpoints_dir.mkdir(parents=True, exist_ok=True)

    print("\n========================================================")
    print("  DocSearch Stage 2 Evaluation (Ungated End-to-End)")
    print(f"  Chat Model:       {chat_model}")
    print(f"  Temperature:      {args.temperature}")
    print(f"  Prompt SHA:       {prompt_sha256[:12]}...")
    print(f"  Target Arms:      {selected_arms}")
    print(f"  Output Directory: {out_dir}")
    print("========================================================")

    arms_data: Dict[str, List[Dict[str, Any]]] = {}
    total_cache_hits = 0
    total_queries_run = 0
    failed_provider_ids: List[str] = []
    consecutive_provider_failures = 0

    for arm_key in selected_arms:
        if arm_key not in config["arms"]:
            print(f"[WARN] Unknown arm key: {arm_key}. Skipping.")
            continue

        arm_meta = config["arms"][arm_key]
        arm_name = arm_meta["name"]
        col_id = col_map[arm_meta["collection_ref"]]
        use_hybrid = arm_meta.get("use_hybrid", True)
        twin_mapping = arm_meta.get("twin_mapping")

        print(f"\n--- Running Stage 2 Arm: {arm_name} ---")
        checkpoint_path = checkpoints_dir / f"{arm_key}.jsonl"
        checkpoint_records = load_checkpoint(checkpoint_path)
        arm_results: List[Dict[str, Any]] = []

        if "regime_b" in arm_key:
            target_qs = [q for q in all_questions if q.get("source_file") == "bein-almelali.pdf" and q.get("type") != "not-in-docs"]
        else:
            target_qs = all_questions

        for idx, q in enumerate(target_qs, 1):
            q_id = q.get("id", f"q{idx:03d}")
            total_queries_run += 1

            # Resume support
            if q_id in checkpoint_records:
                arm_results.append(checkpoint_records[q_id])
                continue

            q_text = q["question"]
            norm_q = normalize_text(q_text)
            gold_file = q.get("source_file")
            gold_pages = q.get("source_pages") or []
            expected_ans = q.get("expected_answer")
            keywords = q.get("answer_keywords") or []

            # 1. Pre-Ask Diagnostics Call via dev/retrieve with explicit topK matching Rag:TopK
            ret_retries = 4
            backoffs = [2.0, 6.0, 18.0, 45.0]
            ret_success = False
            best_score = 0.0
            has_fts = False
            ret_elapsed_ms = 0.0
            chunks = []

            for ret_attempt in range(ret_retries):
                try:
                    ret_resp = client.dev_retrieve(q_text, [col_id], top_k=args.top_k, use_hybrid=use_hybrid)
                    best_score = float(ret_resp.get("bestVectorScore", 0.0))
                    has_fts = bool(ret_resp.get("hasFtsHit", False))
                    ret_elapsed_ms = float(ret_resp.get("elapsedMs", 0.0))
                    chunks = ret_resp.get("chunks", [])
                    ret_success = True
                    break
                except Exception as ex:
                    wait_sec = backoffs[ret_attempt] if ret_attempt < len(backoffs) else 5.0
                    print(f"  [WARN] dev_retrieve attempt {ret_attempt + 1}/{ret_retries} failed on {q_id}: {ex}. Retrying in {wait_sec}s...")
                    time.sleep(wait_sec)

            if not ret_success:
                print(f"  [FATAL] dev_retrieve on {q_id} exhausted all {ret_retries} retries. Marking failed_provider.")
                failed_provider_ids.append(q_id)
                consecutive_provider_failures += 1
                if consecutive_provider_failures >= 3:
                    print("\n[CRITICAL] 3 consecutive provider failures detected! Halting run to protect quota.")
                    sys.exit(1)
                continue

            chunk_ids = [c["chunkId"] for c in chunks]
            retrieved_chunk_count = len(chunks)

            # 2. Check deterministic content-hash cache
            cache_key = compute_cache_key(
                normalized_question=norm_q,
                chunk_ids=chunk_ids,
                top_k=args.top_k,
                chat_model=chat_model,
                temperature=args.temperature,
                prompt_sha256=prompt_sha256,
            )

            cached_entry = None if getattr(args, "no_cache", False) else get_from_cache(cache_dir, cache_key)
            is_cached = False

            if cached_entry:
                is_cached = True
                total_cache_hits += 1
                answer = cached_entry.get("answer")
                not_found = cached_entry.get("notFound", False)
                degraded = cached_entry.get("degraded", False)
                sources = cached_entry.get("sources", [])
                retrieval_ms = ret_elapsed_ms
                generation_ms = float(cached_entry.get("generation_ms", 0.0))
                total_ms = retrieval_ms + generation_ms
                consecutive_provider_failures = 0
            elif retrieved_chunk_count == 0:
                # Pre-LLM short-circuit: 0 chunks retrieved
                answer = PERSIAN_REFUSAL_SENTENCE
                not_found = True
                degraded = False
                sources = []
                retrieval_ms = ret_elapsed_ms
                generation_ms = 0.0
                total_ms = retrieval_ms
                consecutive_provider_failures = 0
            else:
                # Live /ask execution with retry logic for transient provider failures
                ask_url = f"{args.base_url.rstrip('/')}/api/v1/ask"
                ask_body = {
                    "question": q_text,
                    "collectionIds": [col_id],
                    "conversationId": None,
                    "topK": args.top_k,
                }
                headers = {"Authorization": f"Bearer {token}", "Content-Type": "application/json"}

                success = False
                retries = 4
                backoffs = [2.0, 6.0, 18.0, 45.0]

                for attempt in range(retries):
                    t0 = time.perf_counter()
                    try:
                        resp = requests.post(ask_url, json=ask_body, headers=headers, timeout=90.0)
                        wall_ms = (time.perf_counter() - t0) * 1000.0

                        if resp.status_code == 429 or resp.status_code >= 500:
                            raise requests.RequestException(f"HTTP {resp.status_code}: {resp.text}")

                        resp.raise_for_status()
                        resp_data = resp.json()
                        degraded = bool(resp_data.get("degraded", False))

                        if degraded:
                            raise requests.RequestException("API returned degraded=true (chat LLM failure)")

                        answer = resp_data.get("answer")
                        not_found = bool(resp_data.get("notFound", False))
                        sources = resp_data.get("sources", [])

                        retrieval_ms = float(resp.headers.get("X-Timing-Retrieval-Ms", ret_elapsed_ms))
                        generation_ms = float(resp.headers.get("X-Timing-Generation-Ms", wall_ms))
                        total_ms = wall_ms

                        success = True
                        consecutive_provider_failures = 0
                        break
                    except Exception as ex:
                        wait_sec = backoffs[attempt] if attempt < len(backoffs) else 5.0
                        print(f"  [WARN] Attempt {attempt + 1}/{retries} failed on {q_id}: {ex}. Retrying in {wait_sec}s...")
                        time.sleep(wait_sec)

                if not success:
                    print(f"  [FATAL] Question {q_id} exhausted all {retries} retries. Marking failed_provider.")
                    failed_provider_ids.append(q_id)
                    consecutive_provider_failures += 1
                    if consecutive_provider_failures >= 3:
                        print("\n[CRITICAL] 3 consecutive provider failures detected! Halting run to protect quota.")
                        sys.exit(1)
                    continue

                # Save successful response to cache
                compact_sources = [{"fileName": s.get("fileName"), "page": s.get("page")} for s in sources]
                save_to_cache(cache_dir, cache_key, {
                    "answer": answer,
                    "notFound": not_found,
                    "degraded": False,
                    "sources": compact_sources,
                    "retrieval_ms": retrieval_ms,
                    "generation_ms": generation_ms,
                })

            # Evaluate Generation & Citation metrics
            gen_metrics = evaluate_generation_metrics(
                answer=answer,
                not_found=not_found,
                expected_answer=expected_ans,
                answer_keywords=keywords,
                sources=sources,
                gold_file=gold_file,
                gold_pages=gold_pages,
                twin_mapping=twin_mapping,
            )

            # Record
            compact_sources = [{"fileName": s.get("fileName"), "page": s.get("page")} for s in sources]
            record = {
                "id": q_id,
                "arm": arm_key,
                "question": q_text,
                "docset": q.get("docset"),
                "split": q.get("split"),
                "type": q.get("type"),
                "source_file": gold_file,
                "source_pages": gold_pages,
                "text_layer_defect": q.get("text_layer_defect", False),
                "defect_mechanism": q.get("defect_mechanism", "none"),
                "answer": answer,
                "notFound": not_found,
                "degraded": degraded,
                "sources": compact_sources,
                "bestVectorScore": best_score,
                "hasFtsHit": has_fts,
                "retrieved_chunk_count": retrieved_chunk_count,
                "chunk_ids": chunk_ids,
                "retrieval_ms": round(retrieval_ms, 2),
                "generation_ms": round(generation_ms, 2),
                "total_ms": round(total_ms, 2),
                "is_cached": is_cached,
                "chat_model": chat_model,
                "temperature": args.temperature,
                "prompt_version": "v1",
                "timestamp_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                "git_sha": get_git_sha(),
                "git_dirty": git_dirty,
                **gen_metrics,
            }

            arm_results.append(record)
            append_checkpoint(checkpoint_path, record)

            if args.sleep > 0 and not is_cached:
                time.sleep(args.sleep)

        arms_data[arm_key] = arm_results
        print(f"  Arm {arm_key} completed: {len(arm_results)} questions processed.")

    # Manifest
    manifest = build_reproducibility_manifest(args.questions, args.base_url)
    manifest["stage"] = "stage2_execution"
    manifest["git_dirty"] = git_dirty
    manifest["run_status"] = "unofficial" if git_dirty else "official"
    manifest["chat_model"] = chat_model
    manifest["temperature"] = args.temperature
    manifest["top_k"] = args.top_k
    manifest["system_prompt_sha256"] = prompt_sha256
    manifest["failed_provider_count"] = len(failed_provider_ids)
    manifest["failed_provider_ids"] = failed_provider_ids
    manifest["total_queries"] = total_queries_run
    manifest["total_cache_hits"] = total_cache_hits
    manifest["cache_hit_rate"] = round(total_cache_hits / total_queries_run, 4) if total_queries_run > 0 else 0.0

    phase_results_file = out_dir / "phase_results.json"
    results_payload = {"manifest": manifest, "arms": arms_data}
    with open(phase_results_file, "w", encoding="utf-8") as f:
        json.dump(results_payload, f, ensure_ascii=False, indent=2)

    print("\n[OK] Stage 2 Phase Execution Complete!")
    print(f"     Results saved to: {phase_results_file}")
    if failed_provider_ids:
        print(f"     [WARN] Provider failures ({len(failed_provider_ids)}): {failed_provider_ids}")

    return results_payload


# --- Live Validation Cross-Check (--validate-offline-sim) ---

def run_validate_offline_sim(args: Any, config: Dict[str, Any], token: str) -> None:
    """Live validation of offline prod_055 simulation against API with Rag__MinSimilarity=0.55."""
    from run_eval import DocSearchClient, load_collection_manifest

    client = DocSearchClient(args.base_url, token)
    manifest_path = args.cache_dir / "collection_manifest.json"
    manifest = load_collection_manifest(manifest_path)
    existing_cols = {c["name"]: c["id"] for c in client.get_collections()}

    # Determine phase directories
    phase_a_dir = args.phase_a_dir or args.stage2_phase_a
    phase_b_dir = args.phase_b_dir or args.stage2_phase_b

    phase_records: Dict[str, Dict[str, Any]] = {}
    for p_dir in [phase_a_dir, phase_b_dir]:
        if p_dir and Path(p_dir).exists():
            res_file = Path(p_dir) / "phase_results.json"
            if res_file.exists():
                with open(res_file, "r", encoding="utf-8") as f:
                    p_data = json.load(f)
                    for arm_k, arm_items in p_data.get("arms", {}).items():
                        for it in arm_items:
                            phase_records[(arm_k, it["id"])] = it

    if not phase_records:
        raise RuntimeError("No Stage 2 phase results found to validate against! Run Phase A/B first.")

    with open(args.questions, "r", encoding="utf-8") as f:
        all_qs = {q["id"]: q for q in json.load(f)}

    selected_arms = [a.strip() for a in args.arms.split(",")] if args.arms else ["arm2_hybrid_ocr_off", "arm1_vector_ocr_off"]

    print("\n========================================================")
    print("  Live Threshold Validation Cross-Check (--validate-offline-sim)")
    print(f"  Arms: {selected_arms}")
    print("========================================================")

    # Stratified validation sample across targeted arms
    validation_pairs = []
    for (arm_k, q_id), item in phase_records.items():
        if arm_k in selected_arms:
            validation_pairs.append((arm_k, q_id, item))

    questions_tested = len(validation_pairs)
    if questions_tested == 0:
        print("\n[FAIL] No questions validated. Check that selected arms match records in phase results.")
        sys.exit(2)

    # Pick up to 18 stratified items
    validation_pairs = validation_pairs[:18]
    questions_tested = len(validation_pairs)
    pre_llm_mismatches = 0
    post_llm_mismatches = 0

    col_map = {}
    for col_key, col_meta in config["collections"].items():
        name = col_meta["id_name"]
        col_map[col_key] = manifest[name]["id"] if name in manifest else existing_cols[name]

    stratum_counts = {
        "NoContext": 0,
        "LowSimilarity": 0,
        "FTS-rescue": 0,
        "Above-tau": 0,
    }

    for arm_k, q_id, ungated_item in validation_pairs:
        ret_count = ungated_item.get("retrieved_chunk_count", 0)
        b_score = ungated_item.get("bestVectorScore", 0.0)
        h_fts = ungated_item.get("hasFtsHit", False)
        if ret_count == 0:
            stratum_counts["NoContext"] += 1
        elif b_score < 0.55 and not h_fts:
            stratum_counts["LowSimilarity"] += 1
        elif b_score < 0.55 and h_fts:
            stratum_counts["FTS-rescue"] += 1
        else:
            stratum_counts["Above-tau"] += 1

        arm_meta = config["arms"][arm_k]
        col_id = col_map[arm_meta["collection_ref"]]
        use_hybrid = arm_meta.get("use_hybrid", True)

        q_item = all_qs[q_id]
        q_text = q_item["question"]

        # 1. Offline simulated outcome under tau = 0.55
        sim = simulate_threshold(ungated_item, tau=0.55)

        # 2. Live call against API with MinSimilarity = 0.55 (must send same topK=args.top_k)
        client.dev_retrieve(q_text, [col_id], top_k=args.top_k, use_hybrid=use_hybrid)

        ask_url = f"{args.base_url.rstrip('/')}/api/v1/ask"
        resp = requests.post(
            ask_url,
            json={"question": q_text, "collectionIds": [col_id], "conversationId": None, "topK": args.top_k},
            headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"},
            timeout=45.0,
        )
        resp.raise_for_status()
        live_data = resp.json()

        live_not_found = bool(live_data.get("notFound", False))
        live_gen_ms = float(resp.headers.get("X-Timing-Generation-Ms", 0.0))

        # Check pre-LLM gate outcome
        is_live_pre_gated = (live_gen_ms == 0.0 and live_not_found)
        is_sim_pre_gated = sim["gated"]

        if is_live_pre_gated != is_sim_pre_gated:
            pre_llm_mismatches += 1
            print(f"  [GATE MISMATCH] {arm_k} {q_id}: Live gated={is_live_pre_gated} vs Sim gated={is_sim_pre_gated}")

        # Check post-LLM outcome when not pre-gated
        if not is_sim_pre_gated:
            if live_not_found != sim["notFound"]:
                post_llm_mismatches += 1
                print(f"  [POST-LLM VARIATION] {arm_k} {q_id}: Live notFound={live_not_found} vs Sim notFound={sim['notFound']}")

    print("\n--- Validation Summary ---")
    print(f"  Questions Tested:       {questions_tested}")
    print("  Strata Breakdown:")
    print(f"    - NoContext:          {stratum_counts['NoContext']}")
    print(f"    - LowSimilarity:      {stratum_counts['LowSimilarity']}")
    print(f"    - FTS-rescue:         {stratum_counts['FTS-rescue']}")
    print(f"    - Above-tau:          {stratum_counts['Above-tau']}")
    print(f"  Pre-LLM Gate Agreement: {questions_tested - pre_llm_mismatches}/{questions_tested} (100% required)")
    print(f"  Post-LLM Agreement:     {questions_tested - post_llm_mismatches}/{questions_tested}")

    assert pre_llm_mismatches == 0, f"Pre-LLM gate mismatches detected ({pre_llm_mismatches})! Gate simulation failed."
    print("[OK] Live threshold cross-check passed with 100% pre-LLM gate agreement!")


# --- Stage 2 Report Merge & Plot Generation ---

def generate_stage2_plots(results: Dict[str, Any], out_dir: Path) -> List[Path]:
    """Generates 3 headless Matplotlib PNG plots for the thesis."""
    out_dir.mkdir(parents=True, exist_ok=True)
    generated_plots = []

    arms_data = results.get("arms", {})
    if not arms_data:
        return []

    # Plot 1: Stage 2 Quality Comparison (Answer Rate, KW Recall, Citation Validity, Precision) under prod_055
    fig, ax = plt.subplots(figsize=(10, 5.5))
    target_arms = ["arm1_vector_ocr_off", "arm2_hybrid_ocr_off", "arm3_hybrid_ocr_defective"]
    labels = ["Arm 1 (Vector)", "Arm 2 (Hybrid)", "Arm 3 (Full System)"]
    metrics_list = ["answer_rate", "kw_recall", "citation_validity", "citation_precision"]
    metric_display_labels = [
        "Answer Rate (N=112)",
        "KW Recall (N=112)",
        "Citation Validity (N=ans)",
        "Citation Precision (mean ratio, no Wilson bar)",
    ]

    data_matrix = []
    yerr_lower_matrix = []
    yerr_upper_matrix = []

    for m in metrics_list:
        row = []
        err_low_row = []
        err_up_row = []
        for arm_k in target_arms:
            items = arms_data.get(arm_k, [])
            ans_items = [it for it in items if it.get("type") != "not-in-docs"]
            if not ans_items:
                row.append(0.0)
                err_low_row.append(0.0)
                err_up_row.append(0.0)
                continue
            # Apply prod_055 simulation
            sim_items = [simulate_threshold(it, 0.55) for it in ans_items]
            answered = [s for s in sim_items if not s["notFound"]]

            if m == "answer_rate":
                k = len(answered)
                n = len(ans_items)  # 112
                val = k / n
                ci_l, ci_u = wilson_score_interval(k, n)
                err_low_row.append(max(0.0, val - ci_l))
                err_up_row.append(max(0.0, ci_u - val))
            elif m == "kw_recall":
                k = sum(1 for s in sim_items if s.get("kw_hit"))
                n = len(ans_items)  # 112
                val = k / n
                ci_l, ci_u = wilson_score_interval(k, n)
                err_low_row.append(max(0.0, val - ci_l))
                err_up_row.append(max(0.0, ci_u - val))
            elif m == "citation_validity":
                k = sum(1 for s in answered if s.get("citation_validity"))
                n = len(answered)  # actual answered count per arm (99, 100, 97)
                val = k / n if n else 0.0
                ci_l, ci_u = wilson_score_interval(k, n) if n else (0.0, 0.0)
                err_low_row.append(max(0.0, val - ci_l))
                err_up_row.append(max(0.0, ci_u - val))
            elif m == "citation_precision":
                # Citation Precision is a mean ratio across answered queries (not a Bernoulli trial)
                prec_list = [s.get("citation_precision", 0.0) for s in answered]
                val = float(np.mean(prec_list)) if prec_list else 0.0
                err_low_row.append(0.0)
                err_up_row.append(0.0)
            row.append(val)
        data_matrix.append(row)
        yerr_lower_matrix.append(err_low_row)
        yerr_upper_matrix.append(err_up_row)

    x = np.arange(len(target_arms))
    width = 0.2
    for idx, (disp_label, row) in enumerate(zip(metric_display_labels, data_matrix)):
        if metrics_list[idx] == "citation_precision":
            yerr = None
        else:
            yerr = [yerr_lower_matrix[idx], yerr_upper_matrix[idx]]
        ax.bar(
            x + idx * width,
            row,
            width,
            yerr=yerr,
            capsize=3,
            error_kw={"elinewidth": 1.2, "ecolor": "black", "capthick": 1.2},
            label=disp_label,
        )

    ax.set_ylabel("Score / Proportion")
    ax.set_title("DocSearch Stage 2 Generation Quality by Arm (prod_055, tau=0.55)", fontsize=11, pad=35)
    ax.set_xticks(x + width * 1.5)
    ax.set_xticklabels(labels)
    ax.set_ylim(0.0, 1.15)
    ax.legend(
        bbox_to_anchor=(0.5, 1.02),
        loc="lower center",
        ncol=4,
        frameon=True,
        fontsize=8.0,
    )
    ax.grid(axis="y", linestyle="--", alpha=0.7)

    plot1_path = out_dir / "plot1_stage2_quality_comparison.png"
    fig.tight_layout()
    fig.savefig(plot1_path, dpi=300, bbox_inches="tight")
    plt.close(fig)
    generated_plots.append(plot1_path)

    # Plot 2: Threshold Sweep Trade-Off Curves across 3 panels: Tuned Dev, Tuned Test, Held-out
    fig, (ax_dev, ax_test, ax_held) = plt.subplots(1, 3, figsize=(16, 5.2), sharey=True)
    sweep_range = np.arange(0.30, 0.825, 0.025)

    arm_plot_styles = {
        "arm1_vector_ocr_off": {
            "label_base": "Arm 1 (Vector)",
            "frr_color": "#1f77b4",
            "frr_style": ":",
            "frr_marker": "^",
            "frr_lw": 1.8,
            "far_color": "#d62728",
            "far_style": ":",
            "far_marker": "v",
            "far_lw": 1.8,
            "markevery": 2,
        },
        "arm2_hybrid_ocr_off": {
            "label_base": "Arm 2 (Hybrid)",
            "frr_color": "teal",
            "frr_style": "--",
            "frr_marker": "s",
            "frr_lw": 2.2,
            "far_color": "darkorange",
            "far_style": "--",
            "far_marker": "d",
            "far_lw": 2.2,
            "markevery": (1, 2),
        },
        "arm3_hybrid_ocr_defective": {
            "label_base": "Arm 3 (Full System)",
            "frr_color": "navy",
            "frr_style": "-",
            "frr_marker": "o",
            "frr_lw": 1.4,
            "far_color": "crimson",
            "far_style": "-",
            "far_marker": "x",
            "far_lw": 1.4,
            "markevery": 2,
        },
    }

    panel_configs = [
        (ax_dev, "tuned_dev", "Tuned Dev (In-Sample Calibration)"),
        (ax_test, "tuned_test", "Tuned Test (Out-of-Sample)"),
        (ax_held, "heldout", "Held-out (Out-of-Sample Generalization)"),
    ]

    for ax_panel, panel_key, title_prefix in panel_configs:
        n_ans_panel = 0
        n_nid_panel = 0

        for arm_k in ["arm1_vector_ocr_off", "arm2_hybrid_ocr_off", "arm3_hybrid_ocr_defective"]:
            arm_items = arms_data.get(arm_k, [])
            if panel_key == "tuned_dev":
                sub_qs = [it for it in arm_items if it.get("docset") == "tuned" and it.get("split") == "dev"]
            elif panel_key == "tuned_test":
                sub_qs = [it for it in arm_items if it.get("docset") == "tuned" and it.get("split") == "test"]
            else:
                sub_qs = [it for it in arm_items if it.get("docset") == "heldout"]

            ans_qs = [it for it in sub_qs if it.get("type") != "not-in-docs"]
            nid_qs = [it for it in sub_qs if it.get("type") == "not-in-docs"]

            if ans_qs:
                n_ans_panel = len(ans_qs)
            if nid_qs:
                n_nid_panel = len(nid_qs)

            if not sub_qs:
                continue

            style = arm_plot_styles.get(arm_k, {})
            far_curve = []
            frr_curve = []
            for tau in sweep_range:
                fa = sum(1 for it in nid_qs if not simulate_threshold(it, tau)["notFound"])
                fr = sum(1 for it in ans_qs if simulate_threshold(it, tau)["notFound"])
                far_curve.append(fa / len(nid_qs) if nid_qs else 0.0)
                frr_curve.append(fr / len(ans_qs) if ans_qs else 0.0)

            ax_panel.plot(
                sweep_range,
                frr_curve,
                color=style.get("frr_color", "blue"),
                linestyle=style.get("frr_style", "-"),
                linewidth=style.get("frr_lw", 1.5),
                marker=style.get("frr_marker", "o"),
                markersize=4,
                markevery=style.get("markevery", 1),
                alpha=0.9,
                label=f"{style.get('label_base', arm_k)} FRR",
            )
            ax_panel.plot(
                sweep_range,
                far_curve,
                color=style.get("far_color", "red"),
                linestyle=style.get("far_style", "--"),
                linewidth=style.get("far_lw", 1.5),
                marker=style.get("far_marker", "x"),
                markersize=4,
                markevery=style.get("markevery", 1),
                alpha=0.9,
                label=f"{style.get('label_base', arm_k)} FAR",
            )

        ax_panel.axvline(0.55, color="gray", linestyle=":", linewidth=1.5, label="prod_055 (0.55)")
        ax_panel.axvline(0.70, color="darkorange", linestyle="-.", linewidth=1.5, label="Arm 1 tau* = 0.70")
        ax_panel.axvline(0.75, color="purple", linestyle="--", linewidth=1.5, label="Arms 2-3 tau* = 0.75")
        ax_panel.set_xlabel("Similarity Threshold (tau)")
        ax_panel.set_title(f"{title_prefix}\n(N_ans={n_ans_panel}, N_nid={n_nid_panel})", fontsize=10)
        ax_panel.grid(True, linestyle="--", alpha=0.5)

    ax_dev.set_ylabel("Error Rate")
    fig.suptitle("DocSearch Threshold Trade-Off Curves End-to-End Across Arms", fontsize=12, y=1.03)

    handles_dev, labels_dev = ax_dev.get_legend_handles_labels()
    dedup_handles = []
    dedup_labels = []
    seen = set()
    for h, l in zip(handles_dev, labels_dev):
        if l not in seen:
            seen.add(l)
            dedup_handles.append(h)
            dedup_labels.append(l)

    fig.legend(
        dedup_handles,
        dedup_labels,
        loc="upper center",
        bbox_to_anchor=(0.5, -0.05),
        ncol=5,
        fontsize=8.5,
        frameon=True,
    )

    plot2_path = out_dir / "plot2_threshold_tradeoff_curves.png"
    fig.tight_layout()
    fig.savefig(plot2_path, dpi=300, bbox_inches="tight")
    plt.close(fig)
    generated_plots.append(plot2_path)

    # Plot 3: OCR Impact (Number/Unit Recall on heldout-CMap & Regime B Twin)
    fig, (ax_l, ax_r) = plt.subplots(1, 2, figsize=(13, 5.0))

    # Left: heldout-CMap Arm 3 vs Arm 2 under ungated
    arm2_items = arms_data.get("arm2_hybrid_ocr_off", [])
    arm3_items = arms_data.get("arm3_hybrid_ocr_defective", [])

    heldout_cmap_a2 = [it for it in arm2_items if it.get("source_file") in HELDOUT_CMAP_FILES and it.get("type") != "not-in-docs"]
    heldout_cmap_a3 = [it for it in arm3_items if it.get("source_file") in HELDOUT_CMAP_FILES and it.get("type") != "not-in-docs"]

    num_hits_a2 = sum(1 for it in heldout_cmap_a2 if it.get("num_hit") is True)
    num_tot_a2 = sum(1 for it in heldout_cmap_a2 if it.get("num_hit") is not None)
    num_hits_a3 = sum(1 for it in heldout_cmap_a3 if it.get("num_hit") is True)
    num_tot_a3 = sum(1 for it in heldout_cmap_a3 if it.get("num_hit") is not None)

    unit_hits_a2 = sum(1 for it in heldout_cmap_a2 if it.get("unit_hit_strict") is True)
    unit_tot_a2 = sum(1 for it in heldout_cmap_a2 if it.get("unit_hit_strict") is not None)
    unit_hits_a3 = sum(1 for it in heldout_cmap_a3 if it.get("unit_hit_strict") is True)
    unit_tot_a3 = sum(1 for it in heldout_cmap_a3 if it.get("unit_hit_strict") is not None)

    unit_prox_hits_a2 = sum(1 for it in heldout_cmap_a2 if it.get("unit_hit_proximity_10") is True)
    unit_prox_tot_a2 = sum(1 for it in heldout_cmap_a2 if it.get("unit_hit_proximity_10") is not None)
    unit_prox_hits_a3 = sum(1 for it in heldout_cmap_a3 if it.get("unit_hit_proximity_10") is True)
    unit_prox_tot_a3 = sum(1 for it in heldout_cmap_a3 if it.get("unit_hit_proximity_10") is not None)

    x3 = np.arange(3)
    w3 = 0.35
    a2_vals = [
        num_hits_a2 / num_tot_a2 if num_tot_a2 else 0.0,
        unit_hits_a2 / unit_tot_a2 if unit_tot_a2 else 0.0,
        unit_prox_hits_a2 / unit_prox_tot_a2 if unit_prox_tot_a2 else 0.0,
    ]
    a3_vals = [
        num_hits_a3 / num_tot_a3 if num_tot_a3 else 0.0,
        unit_hits_a3 / unit_tot_a3 if unit_tot_a3 else 0.0,
        unit_prox_hits_a3 / unit_prox_tot_a3 if unit_prox_tot_a3 else 0.0,
    ]

    # Compute 95% Wilson score error bars for all 6 bars
    a2_counts = [(num_hits_a2, num_tot_a2), (unit_hits_a2, unit_tot_a2), (unit_prox_hits_a2, unit_prox_tot_a2)]
    a3_counts = [(num_hits_a3, num_tot_a3), (unit_hits_a3, unit_tot_a3), (unit_prox_hits_a3, unit_prox_tot_a3)]

    a2_err_low = []
    a2_err_up = []
    a2_ci_u_list = []
    for (k, n), val in zip(a2_counts, a2_vals):
        ci_l, ci_u = wilson_score_interval(k, n) if n else (0.0, 0.0)
        a2_err_low.append(max(0.0, val - ci_l))
        a2_err_up.append(max(0.0, ci_u - val))
        a2_ci_u_list.append(ci_u)

    a3_err_low = []
    a3_err_up = []
    a3_ci_u_list = []
    for (k, n), val in zip(a3_counts, a3_vals):
        ci_l, ci_u = wilson_score_interval(k, n) if n else (0.0, 0.0)
        a3_err_low.append(max(0.0, val - ci_l))
        a3_err_up.append(max(0.0, ci_u - val))
        a3_ci_u_list.append(ci_u)

    bars_a2 = ax_l.bar(
        x3 - w3/2, a2_vals, w3,
        yerr=[a2_err_low, a2_err_up],
        capsize=3,
        error_kw={"elinewidth": 1.2, "ecolor": "black", "capthick": 1.2},
        label="Arm 2 (OCR Off)",
        color="salmon",
    )
    bars_a3 = ax_l.bar(
        x3 + w3/2, a3_vals, w3,
        yerr=[a3_err_low, a3_err_up],
        capsize=3,
        error_kw={"elinewidth": 1.2, "ecolor": "black", "capthick": 1.2},
        label="Arm 3 (Selective OCR)",
        color="mediumseagreen",
    )

    a2_labels = [
        f"{num_hits_a2}/{num_tot_a2}",
        f"{unit_hits_a2}/{unit_tot_a2}",
        f"{unit_prox_hits_a2}/{unit_prox_tot_a2}",
    ]
    a3_labels = [
        f"{num_hits_a3}/{num_tot_a3}",
        f"{unit_hits_a3}/{unit_tot_a3}",
        f"{unit_prox_hits_a3}/{unit_prox_tot_a3}",
    ]

    for b, lbl, ci_u in zip(bars_a2, a2_labels, a2_ci_u_list):
        h = b.get_height()
        y_pos = max(ci_u + 0.03, h + 0.03, 0.05)
        ax_l.annotate(
            lbl,
            xy=(b.get_x() + b.get_width() / 2, y_pos),
            xytext=(0, 2),
            textcoords="offset points",
            ha="center",
            va="bottom",
            fontsize=8.5,
            fontweight="bold",
        )

    for b, lbl, ci_u in zip(bars_a3, a3_labels, a3_ci_u_list):
        h = b.get_height()
        y_pos = max(ci_u + 0.03, h + 0.03, 0.05)
        ax_l.annotate(
            lbl,
            xy=(b.get_x() + b.get_width() / 2, y_pos),
            xytext=(0, 2),
            textcoords="offset points",
            ha="center",
            va="bottom",
            fontsize=8.5,
            fontweight="bold",
        )

    ax_l.set_xticks(x3)
    ax_l.set_xticklabels([
        f"Numbers (N={num_tot_a3})",
        f"Units Strict (N={unit_tot_a3})",
        f"Units Prox<=10 (N={unit_prox_tot_a3})",
    ])
    ax_l.set_ylabel("Recall")
    ax_l.set_title("heldout-CMap Granular Recall (Ungated, 95% Wilson CI)")
    ax_l.set_ylim(0.0, 1.25)
    ax_l.legend(loc="upper left")
    ax_l.grid(axis="y", linestyle="--", alpha=0.7)

    # Right: Regime B Scan Twin Answer Rate with 3 bars: Off, Defective, Digital Ref
    scan_off_items = arms_data.get("regime_b_scan_off", [])
    scan_def_items = arms_data.get("regime_b_scan_defective", [])
    digital_ref_items = [it for it in arm2_items if it.get("source_file") == "bein-almelali.pdf" and it.get("type") != "not-in-docs"]

    # Evaluate under prod_055 simulation
    sim_off = [simulate_threshold(it, 0.55) for it in scan_off_items]
    sim_def = [simulate_threshold(it, 0.55) for it in scan_def_items]
    sim_dig = [simulate_threshold(it, 0.55) for it in digital_ref_items]

    k_off = sum(1 for it in sim_off if not it.get("notFound"))
    n_off = len(sim_off)
    k_def = sum(1 for it in sim_def if not it.get("notFound"))
    n_def = len(sim_def)
    k_dig = sum(1 for it in sim_dig if not it.get("notFound"))
    n_dig = len(sim_dig)

    ans_off = k_off / n_off if n_off else 0.0
    ans_def = k_def / n_def if n_def else 0.0
    ans_dig = k_dig / n_dig if n_dig else 0.0

    r_vals = [ans_off, ans_def, ans_dig]
    r_counts = [(k_off, n_off), (k_def, n_def), (k_dig, n_dig)]
    r_err_low = []
    r_err_up = []
    r_ci_u_list = []
    for (k, n), val in zip(r_counts, r_vals):
        ci_l, ci_u = wilson_score_interval(k, n) if n else (0.0, 0.0)
        r_err_low.append(max(0.0, val - ci_l))
        r_err_up.append(max(0.0, ci_u - val))
        r_ci_u_list.append(ci_u)

    r_names = ["Scan Twin\nOCR Off", "Scan Twin\nOCR Defective", "Digital Ref\n(Arm 2)"]
    bars_r = ax_r.bar(
        r_names,
        r_vals,
        yerr=[r_err_low, r_err_up],
        capsize=3,
        error_kw={"elinewidth": 1.2, "ecolor": "black", "capthick": 1.2},
        color=["gray", "teal", "royalblue"],
        width=0.45,
    )
    r_labels = [f"{k_off}/{n_off}", f"{k_def}/{n_def}", f"{k_dig}/{n_dig}"]
    for b, lbl, ci_u in zip(bars_r, r_labels, r_ci_u_list):
        h = b.get_height()
        y_pos = max(ci_u + 0.03, h + 0.03, 0.05)
        ax_r.annotate(
            lbl,
            xy=(b.get_x() + b.get_width() / 2, y_pos),
            xytext=(0, 2),
            textcoords="offset points",
            ha="center",
            va="bottom",
            fontsize=9,
            fontweight="bold",
        )

    ax_r.set_ylabel("Answer Rate")
    ax_r.set_title("Regime B Scanned Twin (Single-Doc Case Study, N=8, prod_055)")
    ax_r.set_ylim(0.0, 1.25)
    ax_r.grid(axis="y", linestyle="--", alpha=0.7)

    plot3_path = out_dir / "plot3_ocr_generation_impact.png"
    fig.tight_layout()
    fig.savefig(plot3_path, dpi=300, bbox_inches="tight")
    plt.close(fig)
    generated_plots.append(plot3_path)

    return generated_plots

    return generated_plots


def run_stage2_report(args: Any) -> Dict[str, Path]:
    """Merges Phase A and Phase B results, computes metrics across 3 threshold operating points, and renders report."""
    phase_a_dir = getattr(args, "phase_a_dir", None) or getattr(args, "stage2_phase_a", None)
    phase_b_dir = getattr(args, "phase_b_dir", None) or getattr(args, "stage2_phase_b", None)
    out_dir = getattr(args, "out", None) or Path("eval/results") / "t19b_stage2"
    out_dir = Path(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    merged_arms: Dict[str, List[Dict[str, Any]]] = {}
    manifest: Dict[str, Any] = {}

    phase_data_shas: List[str] = []
    phase_dirty_flags: List[bool] = []
    chat_models: List[str] = []
    prompt_shas: List[str] = []
    total_queries_sum = 0
    total_cache_hits_sum = 0
    all_failed_provider_ids: List[str] = []

    for p_dir in [phase_a_dir, phase_b_dir]:
        if not p_dir:
            continue
        p_path = Path(p_dir)
        p_res = p_path if p_path.is_file() else (p_path / "phase_results.json")
        if p_res.exists():
            with open(p_res, "r", encoding="utf-8") as f:
                data = json.load(f)
                p_manifest = data.get("manifest", {})
                for k, v in p_manifest.items():
                    if k not in manifest:
                        manifest[k] = v

                sha = p_manifest.get("data_git_sha") or p_manifest.get("git_sha")
                if sha:
                    phase_data_shas.append(sha)

                if "git_dirty" in p_manifest:
                    phase_dirty_flags.append(bool(p_manifest["git_dirty"]))

                if p_manifest.get("chat_model"):
                    chat_models.append(p_manifest["chat_model"])
                if p_manifest.get("system_prompt_sha256"):
                    prompt_shas.append(p_manifest["system_prompt_sha256"])

                total_queries_sum += int(p_manifest.get("total_queries", 0))
                total_cache_hits_sum += int(p_manifest.get("total_cache_hits", 0))
                for fid in p_manifest.get("failed_provider_ids", []):
                    if fid not in all_failed_provider_ids:
                        all_failed_provider_ids.append(fid)

                for arm_k, arm_items in data.get("arms", {}).items():
                    merged_arms[arm_k] = arm_items

    # Locked tau thresholds (from stage 1 or standard calibrations)
    calibrations = {
        "arm1_vector_ocr_off": 0.70,
        "arm2_hybrid_ocr_off": 0.75,
        "arm3_hybrid_ocr_defective": 0.75,
        "regime_b_scan_off": 0.55,
        "regime_b_scan_defective": 0.55,
    }

    stage1_path = getattr(args, "stage1_results", None)
    if stage1_path and Path(stage1_path).exists():
        try:
            with open(stage1_path, "r", encoding="utf-8") as f:
                s1_data = json.load(f)
                s1_cals = s1_data.get("calibrations", {})
                for k, v in s1_cals.items():
                    if k in calibrations and isinstance(v, (int, float)):
                        calibrations[k] = float(v)
        except Exception as ex:
            print(f"  [WARN] Failed to load calibrations from stage1-results: {ex}")

    data_git_sha = phase_data_shas[0] if phase_data_shas else manifest.get("git_sha", "unknown")
    report_git_sha = get_git_sha()
    is_dirty = any(phase_dirty_flags) if phase_dirty_flags else bool(manifest.get("git_dirty", False))
    run_status = "unofficial" if is_dirty else "official"

    manifest["stage"] = "stage2_merged_report"
    manifest["data_git_sha"] = data_git_sha
    manifest["report_git_sha"] = report_git_sha
    manifest["git_sha"] = data_git_sha
    manifest["git_dirty"] = is_dirty
    manifest["run_status"] = run_status
    if chat_models:
        manifest["chat_model"] = chat_models[0]
    if prompt_shas:
        manifest["system_prompt_sha256"] = prompt_shas[0]
    manifest["total_queries"] = total_queries_sum
    manifest["total_cache_hits"] = total_cache_hits_sum
    manifest["cache_hit_rate"] = round(total_cache_hits_sum / total_queries_sum, 4) if total_queries_sum > 0 else 0.0
    manifest["failed_provider_ids"] = all_failed_provider_ids
    manifest["failed_provider_count"] = len(all_failed_provider_ids)

    results_data = {
        "manifest": manifest,
        "calibrations": calibrations,
        "arms": merged_arms,
    }

    results_json_path = out_dir / "results.json"
    with open(results_json_path, "w", encoding="utf-8") as f:
        json.dump(results_data, f, ensure_ascii=False, indent=2)

    # Generate Summary CSV
    summary_csv_path = out_dir / "summary.csv"
    with open(summary_csv_path, "w", encoding="utf-8", newline="") as f:
        writer = csv.writer(f)
        writer.writerow([
            "arm", "operating_point", "cohort", "q_count", "answer_rate",
            "not_in_docs_far", "not_in_docs_trr", "answerable_frr", "frr_low_similarity", "frr_model_refusal",
            "kw_recall", "num_recall", "unit_recall_strict", "unit_recall_prox",
            "citation_validity", "citation_page_hit", "citation_precision", "citation_recall",
        ])

        cohorts = [
            ("overall", lambda q: True),
            ("heldout_all", lambda q: q.get("docset") == "heldout"),
            ("heldout_test", lambda q: q.get("docset") == "heldout" and q.get("split") == "test"),
            ("tuned_test", lambda q: q.get("docset") == "tuned" and q.get("split") == "test"),
            ("tuned_dev_in_sample", lambda q: q.get("docset") == "tuned" and q.get("split") == "dev"),
            ("heldout_cmap", lambda q: q.get("source_file") in HELDOUT_CMAP_FILES),
            ("primary_cmap", lambda q: q.get("source_file") in PRIMARY_CMAP_FILES),
        ]

        operating_points = [
            ("prod_055", lambda arm: 0.55),
            ("calibrated_tau", lambda arm: calibrations.get(arm, 0.55)),
            ("ungated", lambda arm: 0.0),
        ]

        for arm_k, arm_items in merged_arms.items():
            for op_name, tau_fn in operating_points:
                tau = tau_fn(arm_k)
                for c_name, c_filter in cohorts:
                    c_items = [it for it in arm_items if c_filter(it)]
                    if not c_items:
                        continue

                    ans_items = [it for it in c_items if it.get("type") != "not-in-docs"]
                    nid_items = [it for it in c_items if it.get("type") == "not-in-docs"]

                    sim_ans = [simulate_threshold(it, tau) for it in ans_items]
                    sim_nid = [simulate_threshold(it, tau) for it in nid_items]

                    # NID metrics
                    nid_fa = sum(1 for s in sim_nid if not s["notFound"])
                    far = nid_fa / len(nid_items) if nid_items else 0.0
                    trr = 1.0 - far

                    # Answerable FRR & attribution
                    ans_fr = sum(1 for s in sim_ans if s["notFound"])
                    frr = ans_fr / len(ans_items) if ans_items else 0.0
                    fr_low_sim = sum(1 for s in sim_ans if s["no_answer_reason"] == "LowSimilarity")
                    fr_model = sum(1 for s in sim_ans if s["no_answer_reason"] == "ModelRefusal")
                    frr_low = fr_low_sim / len(ans_items) if ans_items else 0.0
                    frr_mod = fr_model / len(ans_items) if ans_items else 0.0

                    answered_items = [s for s in sim_ans if not s["notFound"]]
                    ans_rate = len(answered_items) / len(ans_items) if ans_items else 0.0

                    # Generation metrics across cohort (non-vacuous denominators invariant)
                    kw_rec = sum(1 for s in sim_ans if s.get("kw_hit")) / len(ans_items) if ans_items else 0.0

                    num_items = [s for s in sim_ans if s.get("num_hit") is not None]
                    num_rec = sum(1 for s in num_items if s.get("num_hit") is True) / len(num_items) if num_items else None

                    unit_items = [s for s in sim_ans if s.get("unit_hit_strict") is not None]
                    unit_rec_s = sum(1 for s in unit_items if s.get("unit_hit_strict") is True) / len(unit_items) if unit_items else None
                    unit_rec_p = sum(1 for s in unit_items if s.get("unit_hit_proximity_10") is True) / len(unit_items) if unit_items else None

                    cit_val = sum(1 for s in answered_items if s.get("citation_validity")) / len(answered_items) if answered_items else 0.0
                    cit_hit = sum(1 for s in answered_items if s.get("citation_page_hit")) / len(answered_items) if answered_items else 0.0
                    cit_prec = np.mean([s.get("citation_precision", 0.0) for s in answered_items]) if answered_items else 0.0
                    cit_rec = np.mean([s.get("citation_recall", 0.0) for s in answered_items]) if answered_items else 0.0

                    writer.writerow([
                        arm_k, op_name, c_name, len(c_items), round(ans_rate, 4),
                        round(far, 4), round(trr, 4), round(frr, 4), round(frr_low, 4), round(frr_mod, 4),
                        round(kw_rec, 4), round(num_rec, 4) if num_rec is not None else "N/A",
                        round(unit_rec_s, 4) if unit_rec_s is not None else "N/A",
                        round(unit_rec_p, 4) if unit_rec_p is not None else "N/A",
                        round(cit_val, 4), round(cit_hit, 4), round(cit_prec, 4), round(cit_rec, 4),
                    ])

    # Generate Markdown Report
    report_md_path = out_dir / "report.md"
    run_status = manifest.get("run_status", "unofficial" if manifest.get("git_dirty", False) else "official")
    report_lines = [
        "# DocSearch Stage 2 End-to-End Evaluation Report (T19b)",
        "",
        f"- **Run Status:** `{run_status}`",
        f"- **Data generated at Git SHA:** `{manifest.get('data_git_sha', manifest.get('git_sha', 'unknown'))}`",
        f"- **Report generated at Git SHA:** `{manifest.get('report_git_sha', 'unknown')}`",
        f"- **Git Working Tree Dirty:** `{manifest.get('git_dirty', False)}`",
        f"- **Chat Model:** `{manifest.get('chat_model', 'unknown')}`",
        f"- **System Prompt SHA:** `{(manifest.get('system_prompt_sha256') or 'unknown')[:12]}...`",
        f"- **Cache Hit Rate:** `{float(manifest.get('cache_hit_rate') or 0.0) * 100:.1f}%`",
        f"- **Provider Failures:** `{manifest.get('failed_provider_count', 0)}`",
        "",
        "## 1. Pre-Registered Primary Endpoints (Held-out Generalization)",
        "",
        "Primary Endpoint 2.1 evaluates answerable False Rejection Rate (FRR) on `heldout` ($N=52$).",
        "Primary Endpoint 2.2 evaluates Not-In-Docs False Acceptance Rate (FAR) on `heldout` ($N=12$).",
        "",
        "| Arm | Operating Point | Held-out Answerable FRR (N=52) | LowSimilarity FRR | ModelRefusal FRR | Held-out NID FAR (N=12) |",
        "|---|---|---|---|---|---|",
    ]

    for arm_k in ["arm1_vector_ocr_off", "arm2_hybrid_ocr_off", "arm3_hybrid_ocr_defective"]:
        items = merged_arms.get(arm_k, [])
        h_ans = [it for it in items if it.get("docset") == "heldout" and it.get("type") != "not-in-docs"]
        h_nid = [it for it in items if it.get("docset") == "heldout" and it.get("type") == "not-in-docs"]

        for op_name, tau in [("prod_055", 0.55), ("calibrated_tau", calibrations.get(arm_k, 0.55))]:
            sim_ans = [simulate_threshold(it, tau) for it in h_ans]
            sim_nid = [simulate_threshold(it, tau) for it in h_nid]

            fr_count = sum(1 for s in sim_ans if s["notFound"])
            frr = fr_count / len(h_ans) if h_ans else 0.0
            frr_low = sum(1 for s in sim_ans if s["no_answer_reason"] == "LowSimilarity") / len(h_ans) if h_ans else 0.0
            frr_mod = sum(1 for s in sim_ans if s["no_answer_reason"] == "ModelRefusal") / len(h_ans) if h_ans else 0.0
            fa_count = sum(1 for s in sim_nid if not s["notFound"])
            far = fa_count / len(h_nid) if h_nid else 0.0

            ci_frr = wilson_score_interval(fr_count, len(h_ans))
            ci_far = wilson_score_interval(fa_count, len(h_nid))

            report_lines.append(
                f"| `{arm_k}` | `{op_name}` | {frr*100:.1f}% [{ci_frr[0]*100:.1f}%, {ci_frr[1]*100:.1f}%] | "
                f"{frr_low*100:.1f}% | {frr_mod*100:.1f}% | {far*100:.1f}% [{ci_far[0]*100:.1f}%, {ci_far[1]*100:.1f}%] |"
            )

    report_lines.extend([
        "",
        "## 2. Pre-Registered Secondary Endpoint: Granular Extraction on `heldout-CMap` (Descriptive Only)",
        "",
        "Pre-registered under the **`ungated` operating point** per ADR 0011 (§5.2) on `tarh-dakheli.pdf` and `forsat-motaleati.pdf` ($N=27$).",
        "",
        "| Metric | Arm 2 (Hybrid + OCR Off) | Arm 3 (Hybrid + Selective OCR) | Delta | Status / Test |",
        "|---|---|---|---|---|",
    ])

    a2_items = merged_arms.get("arm2_hybrid_ocr_off", [])
    a3_items = merged_arms.get("arm3_hybrid_ocr_defective", [])
    h_cmap_a2 = [it for it in a2_items if it.get("source_file") in HELDOUT_CMAP_FILES and it.get("type") != "not-in-docs"]
    h_cmap_a3 = [it for it in a3_items if it.get("source_file") in HELDOUT_CMAP_FILES and it.get("type") != "not-in-docs"]

    # Number recall
    num_tot = sum(1 for it in h_cmap_a3 if it.get("num_hit") is not None)
    num_h2 = sum(1 for it in h_cmap_a2 if it.get("num_hit") is True)
    num_h3 = sum(1 for it in h_cmap_a3 if it.get("num_hit") is True)
    rec2_n = (num_h2 / num_tot) if num_tot else 0.0
    rec3_n = (num_h3 / num_tot) if num_tot else 0.0

    # Unit recall
    unit_tot = sum(1 for it in h_cmap_a3 if it.get("unit_hit_strict") is not None)
    unit_s2 = sum(1 for it in h_cmap_a2 if it.get("unit_hit_strict") is True)
    unit_s3 = sum(1 for it in h_cmap_a3 if it.get("unit_hit_strict") is True)
    unit_p2 = sum(1 for it in h_cmap_a2 if it.get("unit_hit_proximity_10") is True)
    unit_p3 = sum(1 for it in h_cmap_a3 if it.get("unit_hit_proximity_10") is True)

    rec2_us = (unit_s2 / unit_tot) if unit_tot else 0.0
    rec3_us = (unit_s3 / unit_tot) if unit_tot else 0.0
    rec2_up = (unit_p2 / unit_tot) if unit_tot else 0.0
    rec3_up = (unit_p3 / unit_tot) if unit_tot else 0.0

    report_lines.append(f"| Multi-digit Numbers ($N={num_tot}$) | {num_h2}/{num_tot} ({rec2_n*100:.1f}%) | {num_h3}/{num_tot} ({rec3_n*100:.1f}%) | +{(rec3_n - rec2_n)*100:.1f}% | Descriptive only (ADR 0010 §7.1) |")
    report_lines.append(f"| Unit Phrases Strict ($N={unit_tot}$) | {unit_s2}/{unit_tot} ({rec2_us*100:.1f}%) | {unit_s3}/{unit_tot} ({rec3_us*100:.1f}%) | +{(rec3_us - rec2_us)*100:.1f}% | Descriptive only |")
    report_lines.append(f"| Unit Phrases Proximity<=10 ($N={unit_tot}$) | {unit_p2}/{unit_tot} ({rec2_up*100:.1f}%) | {unit_p3}/{unit_tot} ({rec3_up*100:.1f}%) | +{(rec3_up - rec2_up)*100:.1f}% | Descriptive only |")

    report_lines.extend([
        "",
        "## 3. Citation Quality & Performance Across All Questions",
        "",
        "| Arm | Operating Point | Validity Rate | Page-Hit Rate | Precision | Recall | Latency p50 (Live) | Latency p95 (Live) |",
        "|---|---|---|---|---|---|---|---|",
    ])

    for arm_k in ["arm1_vector_ocr_off", "arm2_hybrid_ocr_off", "arm3_hybrid_ocr_defective"]:
        items = merged_arms.get(arm_k, [])
        ans_items = [it for it in items if it.get("type") != "not-in-docs"]
        sim_items = [simulate_threshold(it, 0.55) for it in ans_items]
        answered = [it for it, s in zip(ans_items, sim_items) if not s["notFound"]]

        c_val = sum(1 for it in answered if it.get("citation_validity")) / len(answered) if answered else 0.0
        c_hit = sum(1 for it in answered if it.get("citation_page_hit")) / len(answered) if answered else 0.0
        c_prec = np.mean([it.get("citation_precision", 0.0) for it in answered]) if answered else 0.0
        c_rec = np.mean([it.get("citation_recall", 0.0) for it in answered]) if answered else 0.0

        live_times = [it.get("total_ms", 0.0) for it in items if not it.get("is_cached", False)]
        p50 = float(np.percentile(live_times, 50)) if live_times else 0.0
        p95 = float(np.percentile(live_times, 95)) if live_times else 0.0

        report_lines.append(
            f"| `{arm_k}` | `prod_055` | {c_val*100:.1f}% | {c_hit*100:.1f}% | "
            f"{c_prec*100:.1f}% | {c_rec*100:.1f}% | {p50:.1f} ms | {p95:.1f} ms |"
        )

    with open(report_md_path, "w", encoding="utf-8") as f:
        f.write("\n".join(report_lines) + "\n")

    # Generate Plots
    plots = generate_stage2_plots(results_data, out_dir)

    print("\n[OK] Stage 2 Reports & Plots Generated Successfully!")
    print(f"  Results JSON: {results_json_path}")
    print(f"  Summary CSV:  {summary_csv_path}")
    print(f"  Report MD:    {report_md_path}")
    for p in plots:
        print(f"  Plot PNG:     {p}")

    return {
        "results_json": results_json_path,
        "summary_csv": summary_csv_path,
        "report_md": report_md_path,
        "plots": plots,
    }
