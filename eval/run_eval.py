#!/usr/bin/env python3
"""Evaluation Harness & Multi-Arm Benchmark CLI for DocSearch (Task T19a).

Stage 1: Declarative ingestion, retrieval evaluation, per-arm plateau threshold calibration,
and paired bootstrap statistical inference (question-level and document-cluster).

Usage:
    # 1. Ingestion phase (OcrMode=Off)
    python eval/run_eval.py --ingest-only --ocr-mode Off

    # 2. Ingestion phase (OcrMode=Defective)
    python eval/run_eval.py --ingest-only --ocr-mode Defective

    # 3. Stage 1 Evaluation phase (reuses ingested collections)
    python eval/run_eval.py --stage1 --reuse-collections --out eval/results/t19a_run
"""

import argparse
import csv
import datetime
import hashlib
import json
import os
import platform
import subprocess
import sys
import time
import unicodedata
from collections import defaultdict
from pathlib import Path
from typing import Any, Dict, List, Optional, Set, Tuple

import numpy as np
import requests
import yaml

REPO_ROOT = Path(__file__).resolve().parent.parent

# Exact expected OCR page sets verified in T12 (ADR 0008 / ADR 0009)
EXPECTED_OCR_PAGES_DEFECTIVE: Dict[str, List[int]] = {
    "tarh-dakheli.pdf": [1, 2, 3, 4, 5, 6, 7, 8, 9, 10],
    "forsat-motaleati.pdf": [1, 2, 3, 4, 5, 6, 7, 8],
    "dastyar-pazhuheshi.pdf": [1, 2, 3, 4, 5, 6],
    "kod-akhlaghi.pdf": [1, 2],
    "bedoon-factor.pdf": [1, 2],
    "hamayesh.pdf": [1],
    "bein-almelali-scan.pdf": [1, 2, 3, 4, 5, 6, 7, 8],
    # 8 clean digital PDFs (must trigger zero OCR pages):
    "aein-91.pdf": [],
    "bein-almelali.pdf": [],
    "bge-m3-paper.pdf": [],
    "dastyar-amoozeshi.pdf": [],
    "rag-lewis-2020.pdf": [],
    "shiveh-karamoozi.pdf": [],
    "shoraha-pazhuheshi.pdf": [],
    "ziro-guide.pdf": [],
}

UNIT_ONLY_QS: Set[str] = {"q022", "q097", "q100", "q102", "q103"}
SCREENSHOT_QS: Set[str] = {"q034", "q039"}
HELDOUT_CMAP_FILES: Set[str] = {"tarh-dakheli.pdf", "forsat-motaleati.pdf"}
PRIMARY_CMAP_FILES: Set[str] = {
    "bedoon-factor.pdf",
    "dastyar-pazhuheshi.pdf",
    "hamayesh.pdf",
    "kod-akhlaghi.pdf",
    "tarh-dakheli.pdf",
    "forsat-motaleati.pdf",
}

COHORTS_DEF = [
    # (docset, split)
    ("overall", lambda q: True),
    ("heldout_all", lambda q: q.get("docset") == "heldout"),
    ("heldout_test", lambda q: q.get("docset") == "heldout" and q.get("split") == "test"),
    ("tuned_test", lambda q: q.get("docset") == "tuned" and q.get("split") == "test"),
    ("tuned_dev", lambda q: q.get("docset") == "tuned" and q.get("split") == "dev"),
    # Question type
    ("type_legal_reference", lambda q: q.get("type") == "legal-reference"),
    ("type_numeric", lambda q: q.get("type") == "numeric"),
    ("type_paraphrase", lambda q: q.get("type") == "paraphrase"),
    ("type_multi_chunk", lambda q: q.get("type") == "multi-chunk"),
    ("type_factual", lambda q: q.get("type") == "factual"),
    ("type_english", lambda q: q.get("type") == "english"),
    # ADR 0008 defect cohorts
    ("primary_cmap", lambda q: q.get("source_file") in PRIMARY_CMAP_FILES),
    ("heldout_cmap", lambda q: q.get("source_file") in HELDOUT_CMAP_FILES),
    ("heldout_cmap_tarh_dakheli", lambda q: q.get("source_file") == "tarh-dakheli.pdf"),
    ("heldout_cmap_forsat_motaleati", lambda q: q.get("source_file") == "forsat-motaleati.pdf"),
    ("secondary_cmap_union", lambda q: q.get("defect_mechanism") == "cmap"),
    ("secondary_auto_cmap", lambda q: q.get("defect_mechanism") == "cmap" and q.get("id") not in {"q023", "q028", "q032", "q038", "q082", "q083", "q084", "q086", "q088", "q091", "q093"}),
    ("breakout_unit_only", lambda q: q.get("id") in UNIT_ONLY_QS),
    ("separate_screenshot", lambda q: q.get("defect_mechanism") == "screenshot"),
    ("clean", lambda q: q.get("defect_mechanism") == "none"),
]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="DocSearch Evaluation Harness & Multi-Arm Benchmark (T19a)",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter,
    )
    parser.add_argument("--base-url", default=os.environ.get("BASE_URL", "http://localhost:5000"), help="DocSearch API base URL")
    parser.add_argument("--email", default=None, help="User email (defaults to EVAL_EMAIL env var)")
    parser.add_argument("--password", default=None, help="User password (defaults to EVAL_PASSWORD env var)")
    parser.add_argument("--config", type=Path, default=Path("eval/experiments.yaml"), help="Path to experiments.yaml")
    parser.add_argument("--questions", type=Path, default=Path("eval/questions.json"), help="Path to frozen questions.json")
    parser.add_argument("--pdfs-dir", type=Path, default=Path("eval/pdfs"), help="Directory containing digital evaluation PDFs")
    parser.add_argument("--scan-pdf", type=Path, default=Path("eval/pdfs/ocr/bein-almelali-scan.pdf"), help="Path to scanned PDF twin")
    parser.add_argument("--sidecar-url", default="http://localhost:8000", help="Extractor sidecar URL for pure OCR timing")
    parser.add_argument("--cache-dir", type=Path, default=Path("eval/cache"), help="Cache directory for manifests and timings")
    parser.add_argument("--benchmark-ocr", action="store_true", help="Run standalone pure OCR timing benchmark on extractor sidecar")
    parser.add_argument("--ingest-only", action="store_true", help="Run ingestion only for the specified --ocr-mode")
    parser.add_argument("--ocr-mode", choices=["Off", "Defective"], default=None, help="Server OCR mode for --ingest-only")
    parser.add_argument("--eval-only", action="store_true", help="Run Stage 1 evaluation without ingesting")
    parser.add_argument("--stage1", action="store_true", default=True, help="Execute Stage 1 retrieval & calibration")
    parser.add_argument("--stage2", action="store_true", help="Execute Stage 2 end-to-end evaluation")
    parser.add_argument("--arms", default=None, help="Comma-separated arm keys to execute or validate")
    parser.add_argument("--chat-model", default=None, help="Chat model name (must match API Ai:Chat:Model, e.g. gemini-2.5-flash)")
    parser.add_argument("--temperature", type=float, default=0.0, help="Temperature setting (default: 0.0)")
    parser.add_argument("--top-k", type=int, default=6, help="Top-K context chunks (default: 6)")
    parser.add_argument("--stage1-results", type=Path, default=None, help="Path to Stage 1 results.json (for locked tau* cross-check)")
    parser.add_argument("--validate-offline-sim", action="store_true", help="Run live cross-check of offline threshold simulation against API with MinSimilarity=0.55")
    parser.add_argument("--stage2-phase-a", type=Path, default=None, help="Phase A results directory")
    parser.add_argument("--stage2-phase-b", type=Path, default=None, help="Phase B results directory")
    parser.add_argument("--phase-a-dir", type=Path, default=None, help="Phase A results directory (alias)")
    parser.add_argument("--phase-b-dir", type=Path, default=None, help="Phase B results directory (alias)")
    parser.add_argument("--stage2-report", action="store_true", help="Merge Phase A and Phase B results and generate Stage 2 summary, report, and plots")
    parser.add_argument("--no-cache", action="store_true", help="Bypass LLM cache")
    parser.add_argument("--allow-dirty", action="store_true", help="Allow running evaluation on a dirty git working tree")
    parser.add_argument("--reuse-collections", action="store_true", help="Reuse existing ready collections if present")
    parser.add_argument("--smoke", action="store_true", help="Run fast smoke test on 5 questions")
    parser.add_argument("--out", type=Path, default=None, help="Output directory for benchmark results")
    parser.add_argument("--sleep", type=float, default=0.0, help="Sleep between requests in seconds")
    parser.add_argument("--regenerate-report", type=Path, default=None, help="Regenerate report.md and summary.csv offline from a results.json file without running API or Ollama")
    return parser.parse_args()


# --- Authentication & Provenance ---

def get_auth_token(base_url: str, email: Optional[str], password: Optional[str]) -> str:
    resolved_email = email or os.environ.get("EVAL_EMAIL")
    resolved_password = password or os.environ.get("EVAL_PASSWORD")

    if not resolved_email or not resolved_password:
        raise ValueError(
            "Authentication credentials missing! Provide --email and --password CLI flags "
            "or set EVAL_EMAIL and EVAL_PASSWORD environment variables. Never read local .env."
        )

    login_url = f"{base_url.rstrip('/')}/api/v1/auth/login"
    resp = requests.post(login_url, json={"email": resolved_email, "password": resolved_password}, timeout=15.0)
    resp.raise_for_status()
    token = resp.json().get("accessToken")
    if not token:
        raise ValueError(f"No accessToken in login response: {resp.text}")
    return token


def compute_file_sha256(path: Path) -> str:
    if not path.exists():
        return "missing"
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest()


def get_git_sha() -> str:
    try:
        return subprocess.check_output(["git", "rev-parse", "HEAD"], stderr=subprocess.DEVNULL, text=True).strip()
    except Exception:
        return "unknown"


def build_reproducibility_manifest(questions_path: Path, base_url: str) -> Dict[str, Any]:
    git_sha = get_git_sha()
    git_branch = "unknown"
    try:
        git_branch = subprocess.check_output(["git", "rev-parse", "--abbrev-ref", "HEAD"], stderr=subprocess.DEVNULL, text=True).strip()
    except Exception:
        pass

    git_dirty = False
    try:
        status_out = subprocess.check_output(["git", "status", "--porcelain"], stderr=subprocess.DEVNULL, text=True).strip()
        git_dirty = len(status_out) > 0
    except Exception:
        pass

    ollama_digest = "unavailable"
    try:
        resp = requests.get("http://localhost:11434/api/tags", timeout=3.0)
        if resp.status_code == 200:
            models = resp.json().get("models", [])
            for m in models:
                if "bge-m3" in m.get("name", ""):
                    ollama_digest = m.get("digest", "found_without_digest")
                    break
    except Exception:
        pass

    docker_digest = "unavailable"
    try:
        out = subprocess.check_output(["docker", "compose", "images", "-q", "extractor"], stderr=subprocess.DEVNULL, text=True).strip()
        if out:
            docker_digest = out.splitlines()[0]
        else:
            out = subprocess.check_output(["docker", "inspect", "--format={{.Id}}", "extractor"], stderr=subprocess.DEVNULL, text=True).strip()
            if out:
                docker_digest = out
    except Exception:
        try:
            out = subprocess.check_output(["docker", "inspect", "--format={{.Id}}", "extractor"], stderr=subprocess.DEVNULL, text=True).strip()
            if out:
                docker_digest = out
        except Exception:
            pass

    return {
        "git_sha": git_sha,
        "git_branch": git_branch,
        "git_dirty": git_dirty,
        "eval_dataset_tag": "eval-dataset-v2.2",
        "questions_sha256_lf": compute_file_sha256(questions_path),
        "timestamp_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "ollama_bge_m3_digest": ollama_digest,
        "extractor_docker_digest": docker_digest,
        "tesseract_fas_model": "422KB Debian package (tessdata_fast)",
        "platform": {
            "system": platform.system(),
            "release": platform.release(),
            "python_version": platform.python_version(),
            "cpu_count": os.cpu_count(),
        },
    }


# --- Hit Rules & Metric Calculation ---

def get_hit_at_k(item: Dict[str, Any], k: int) -> float:
    """Safely extracts hit@k handling both int and stringified JSON keys."""
    hits = item.get("hits_at_k", {})
    return float(hits.get(k, hits.get(str(k), 0.0)))


def is_chunk_hit(
    chunk_file: str,
    page_start: int,
    page_end: int,
    gold_file: Optional[str],
    gold_pages: List[int],
    strict: bool = False,
    twin_mapping: Optional[Dict[str, str]] = None,
) -> bool:
    if not gold_file or not gold_pages:
        return False

    effective_file = chunk_file
    if twin_mapping and chunk_file in twin_mapping:
        effective_file = twin_mapping[chunk_file]

    if effective_file != gold_file:
        return False

    # Check page intersection
    gold_min, gold_max = min(gold_pages), max(gold_pages)
    intersects = max(page_start, gold_min) <= min(page_end, gold_max)
    if not intersects:
        return False

    if strict:
        span = page_end - page_start + 1
        if span > 3:
            return False

    return True


def evaluate_retrieval_ranking(
    chunks: List[Dict[str, Any]],
    gold_file: Optional[str],
    gold_pages: List[int],
    k_list: List[int],
    strict: bool = False,
    twin_mapping: Optional[Dict[str, str]] = None,
) -> Dict[str, Any]:
    hit_ranks = []
    for rank, chunk in enumerate(chunks, 1):
        if is_chunk_hit(
            chunk_file=chunk.get("fileName", ""),
            page_start=chunk.get("pageStart", 1),
            page_end=chunk.get("pageEnd", 1),
            gold_file=gold_file,
            gold_pages=gold_pages,
            strict=strict,
            twin_mapping=twin_mapping,
        ):
            hit_ranks.append(rank)

    first_hit_rank = hit_ranks[0] if hit_ranks else None
    rr = 1.0 / first_hit_rank if first_hit_rank else 0.0

    hits_at_k = {}
    for k in k_list:
        hits_at_k[k] = 1 if (first_hit_rank is not None and first_hit_rank <= k) else 0

    return {
        "first_hit_rank": first_hit_rank,
        "mrr": rr,
        "hits_at_k": hits_at_k,
    }


# --- Per-Arm Plateau Threshold Calibration ---

def calibrate_min_similarity(
    dev_items: List[Dict[str, Any]],
    sweep_range: Optional[List[float]] = None,
    plateau_epsilon: float = 0.04,  # Weight of 1 question on N=25 (1/25 = 0.04)
) -> Dict[str, Any]:
    """Sweeps MinSimilarity strictly on (tuned, dev) questions using plateau center rule."""
    if sweep_range is None:
        sweep_range = [round(float(x), 4) for x in np.arange(0.30, 0.801, 0.025)]

    # Hard programmatic anti-leakage guard
    for item in dev_items:
        docset = item.get("docset")
        split = item.get("split")
        if docset != "tuned" or split != "dev":
            raise ValueError(
                f"ANTI-LEAKAGE VIOLATION: Item '{item.get('id')}' with docset='{docset}', "
                f"split='{split}' entered calibrate_min_similarity! "
                "Per ADR 0008 §3, hyperparameter calibration MUST ONLY use docset=='tuned' and split=='dev'."
            )

    ans_items = [it for it in dev_items if it.get("type") != "not-in-docs"]
    not_items = [it for it in dev_items if it.get("type") == "not-in-docs"]

    n_ans = len(ans_items)
    n_not = len(not_items)
    if n_ans == 0 or n_not == 0:
        raise ValueError(f"Calibration cohort incomplete: {n_ans} answerable, {n_not} not-in-docs (expected 18 ans + 7 NID).")

    sweep_records = []
    best_error = float("inf")

    for tau in sweep_range:
        # Rejection rule matching RagService: (best_vector_score < tau && !has_fts_hit)
        false_rejects = 0
        for it in ans_items:
            best_score = it.get("bestVectorScore", 0.0)
            has_fts = it.get("hasFtsHit", False)
            if best_score < tau and not has_fts:
                false_rejects += 1
        frr = false_rejects / n_ans

        false_accepts = 0
        for it in not_items:
            best_score = it.get("bestVectorScore", 0.0)
            has_fts = it.get("hasFtsHit", False)
            # Not rejected = accepted
            if not (best_score < tau and not has_fts):
                false_accepts += 1
        far = false_accepts / n_not

        # Balanced error rate: J = (FAR + FRR) / 2
        j_err = (far + frr) / 2.0
        if j_err < best_error:
            best_error = j_err

        # Pure cosine rejection sensitivity (for audit logging)
        pure_cos_fr = sum(1 for it in ans_items if it.get("bestVectorScore", 0.0) < tau)
        pure_cos_fa = sum(1 for it in not_items if it.get("bestVectorScore", 0.0) >= tau)

        sweep_records.append({
            "threshold": float(tau),
            "far": round(float(far), 4),
            "frr": round(float(frr), 4),
            "balanced_error": round(float(j_err), 4),
            "balanced_accuracy": round(float(1.0 - j_err), 4),
            "pure_cosine_far": round(float(pure_cos_fa / n_not), 4),
            "pure_cosine_frr": round(float(pure_cos_fr / n_ans), 4),
        })

    # Select the center of the optimal plateau: { tau | J(tau) <= best_error + epsilon }
    plateau_taus = [
        rec["threshold"] for rec in sweep_records
        if rec["balanced_error"] <= (best_error + plateau_epsilon + 1e-6)
    ]
    min_plateau = float(min(plateau_taus))
    max_plateau = float(max(plateau_taus))
    center_tau = round(float((min_plateau + max_plateau) / 2.0), 3)

    # Snap center_tau to nearest discrete step in sweep_range
    locked_tau = float(min(sweep_range, key=lambda t: abs(t - center_tau)))

    # Also compute pure cosine sensitivity (without FTS gate) for transparency
    cosine_best_error = min((rec["pure_cosine_far"] + rec["pure_cosine_frr"]) / 2.0 for rec in sweep_records)
    cosine_plateau_taus = [
        rec["threshold"] for rec in sweep_records
        if ((rec["pure_cosine_far"] + rec["pure_cosine_frr"]) / 2.0) <= (cosine_best_error + plateau_epsilon + 1e-6)
    ]
    cos_min_plateau = float(min(cosine_plateau_taus))
    cos_max_plateau = float(max(cosine_plateau_taus))
    cos_center_tau = round(float((cos_min_plateau + cos_max_plateau) / 2.0), 3)
    cos_locked_tau = float(min(sweep_range, key=lambda t: abs(t - cos_center_tau)))

    cosine_only_sensitivity = {
        "locked_min_similarity": cos_locked_tau,
        "best_balanced_error": round(float(cosine_best_error), 4),
        "plateau_range": [cos_min_plateau, cos_max_plateau],
    }

    return {
        "locked_min_similarity": locked_tau,
        "best_balanced_error": round(float(best_error), 4),
        "plateau_range": [min_plateau, max_plateau],
        "plateau_epsilon": float(plateau_epsilon),
        "dev_q_count": len(dev_items),
        "dev_ans_count": n_ans,
        "dev_nid_count": n_not,
        "sweep_records": sweep_records,
        "cosine_only_sensitivity": cosine_only_sensitivity,
    }


# --- Bootstrap Confidence Intervals ---

def paired_bootstrap_ci(
    values_a: List[float],
    values_b: List[float],
    n_resamples: int = 10000,
    seed: int = 42,
) -> Dict[str, Any]:
    """Question-level paired bootstrap 95% confidence interval for (B - A)."""
    assert len(values_a) == len(values_b), "Paired bootstrap requires identical sample sizes"
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

    # Two-sided empirical p-value for null hypothesis mean == 0
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
    metric_key: str = "mrr",
    n_resamples: int = 10000,
    seed: int = 42,
) -> Dict[str, Any]:
    """Document-cluster paired bootstrap 95% CI. Requires G >= 3 document clusters."""
    assert len(items_a) == len(items_b), "Cluster bootstrap requires matching items"
    # Group paired differences by source_file
    clusters: Dict[str, List[float]] = defaultdict(list)
    for it_a, it_b in zip(items_a, items_b):
        doc = it_a.get("source_file", "unknown")
        diff = float(it_b.get(metric_key, 0.0)) - float(it_a.get(metric_key, 0.0))
        clusters[doc].append(diff)

    doc_keys = sorted(clusters.keys())
    g = len(doc_keys)

    # Statistical guard: if G <= 2 clusters, cluster bootstrap is degenerate
    if g < 3:
        per_doc_means = {doc: round(float(np.mean(clusters[doc])), 4) for doc in doc_keys}
        return {
            "status": "N/A (G < 3 clusters)",
            "cluster_count": g,
            "per_doc_deltas": per_doc_means,
            "ci_low": None,
            "ci_high": None,
        }

    # Cluster resampling
    rng = np.random.default_rng(seed)
    boot_means = np.empty(n_resamples, dtype=float)
    cluster_arrays = [np.array(clusters[doc], dtype=float) for doc in doc_keys]

    for i in range(n_resamples):
        sampled_cluster_indices = rng.integers(0, g, size=g)
        sampled_diffs = np.concatenate([cluster_arrays[idx] for idx in sampled_cluster_indices])
        boot_means[i] = np.mean(sampled_diffs)

    all_diffs = np.concatenate(cluster_arrays)
    observed_mean = float(np.mean(all_diffs))
    ci_low = float(np.percentile(boot_means, 2.5))
    ci_high = float(np.percentile(boot_means, 97.5))

    return {
        "status": "valid",
        "cluster_count": g,
        "mean_delta": round(observed_mean, 4),
        "ci_low": round(ci_low, 4),
        "ci_high": round(ci_high, 4),
        "significant_at_05": (ci_low > 0.0) or (ci_high < 0.0),
    }


# --- API Client & Ingestion Routines ---

class DocSearchClient:
    def __init__(self, base_url: str, token: str):
        self.base_url = base_url.rstrip("/")
        self.token = token
        self.session = requests.Session()
        self.session.headers.update({
            "Authorization": f"Bearer {token}",
        })

    def get_collections(self) -> List[Dict[str, Any]]:
        resp = self.session.get(f"{self.base_url}/api/v1/collections?pageSize=100", timeout=15.0)
        resp.raise_for_status()
        return resp.json().get("items", [])

    def create_collection(self, name: str, description: str = "") -> str:
        resp = self.session.post(
            f"{self.base_url}/api/v1/collections",
            json={"name": name, "description": description},
            timeout=15.0,
        )
        if resp.status_code == 201:
            return resp.json()["id"]
        elif resp.status_code == 409:
            # Collection exists; retrieve its ID
            collections = self.get_collections()
            for col in collections:
                if col.get("name") == name:
                    return col["id"]
        resp.raise_for_status()
        return ""

    def upload_document(self, collection_id: str, pdf_path: Path) -> Dict[str, Any]:
        with open(pdf_path, "rb") as f:
            files = {"file": (pdf_path.name, f, "application/pdf")}
            resp = self.session.post(
                f"{self.base_url}/api/v1/collections/{collection_id}/documents",
                files=files,
                timeout=60.0,
            )
            if resp.status_code == 409:
                # Duplicate in collection
                return {"id": "duplicate", "status": "Uploaded"}
            resp.raise_for_status()
            return resp.json()

    def get_document(self, doc_id: str) -> Dict[str, Any]:
        resp = self.session.get(f"{self.base_url}/api/v1/documents/{doc_id}", timeout=15.0)
        resp.raise_for_status()
        return resp.json()

    def list_documents(self, collection_id: str) -> List[Dict[str, Any]]:
        resp = self.session.get(f"{self.base_url}/api/v1/collections/{collection_id}/documents?pageSize=100", timeout=15.0)
        resp.raise_for_status()
        return resp.json().get("items", [])

    def delete_document(self, doc_id: str) -> None:
        resp = self.session.delete(f"{self.base_url}/api/v1/documents/{doc_id}", timeout=15.0)
        if resp.status_code not in (200, 204, 404):
            resp.raise_for_status()

    def wait_until_ready(self, doc_id: str, timeout_sec: float = 900.0, expect_empty: bool = False) -> Dict[str, Any]:
        start = time.time()
        while time.time() - start < timeout_sec:
            doc = self.get_document(doc_id)
            status = doc.get("status")
            if status == "Ready":
                return doc
            if status == "Failed":
                if expect_empty:
                    return doc
                raise RuntimeError(f"Document {doc_id} ({doc.get('fileName')}) failed ingestion: {doc.get('errorMessage')}")
            time.sleep(1.0)
        raise TimeoutError(f"Document {doc_id} timed out waiting for completion after {timeout_sec}s.")

    def dev_retrieve(
        self,
        question: str,
        collection_ids: List[str],
        top_k: int = 10,
        use_hybrid: Optional[bool] = None,
    ) -> Dict[str, Any]:
        resp = self.session.post(
            f"{self.base_url}/api/v1/dev/retrieve",
            json={
                "question": question,
                "collectionIds": collection_ids,
                "topK": top_k,
                "useHybrid": use_hybrid,
            },
            timeout=30.0,
        )
        resp.raise_for_status()
        return resp.json()


# --- Collection Manifest & Pure OCR Timing Routines ---

def load_collection_manifest(manifest_path: Path) -> Dict[str, Any]:
    if manifest_path.exists():
        try:
            with open(manifest_path, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception as e:
            print(f"[WARN] Failed to read collection manifest {manifest_path}: {e}")
    return {}


def save_collection_manifest(manifest_path: Path, manifest: Dict[str, Any]) -> None:
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    with open(manifest_path, "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)


def benchmark_pure_ocr(
    sidecar_url: str,
    pdfs_dir: Path,
    scan_pdf_path: Path,
    cache_dir: Path,
    timeout: float = 120.0,
) -> Dict[str, Any]:
    """Measures pure OCR execution time per page via extractor sidecar POST /ocr."""
    print(f"\n--- Running Pure OCR Timing Benchmark on Extractor Sidecar ({sidecar_url}) ---")
    cache_dir.mkdir(parents=True, exist_ok=True)
    timing_file = cache_dir / "ocr_timing.json"

    target_files = [
        ("tarh-dakheli.pdf", pdfs_dir / "tarh-dakheli.pdf", EXPECTED_OCR_PAGES_DEFECTIVE["tarh-dakheli.pdf"], "regime_a"),
        ("forsat-motaleati.pdf", pdfs_dir / "forsat-motaleati.pdf", EXPECTED_OCR_PAGES_DEFECTIVE["forsat-motaleati.pdf"], "regime_a"),
        ("dastyar-pazhuheshi.pdf", pdfs_dir / "dastyar-pazhuheshi.pdf", EXPECTED_OCR_PAGES_DEFECTIVE["dastyar-pazhuheshi.pdf"], "regime_a"),
        ("kod-akhlaghi.pdf", pdfs_dir / "kod-akhlaghi.pdf", EXPECTED_OCR_PAGES_DEFECTIVE["kod-akhlaghi.pdf"], "regime_a"),
        ("bedoon-factor.pdf", pdfs_dir / "bedoon-factor.pdf", EXPECTED_OCR_PAGES_DEFECTIVE["bedoon-factor.pdf"], "regime_a"),
        ("hamayesh.pdf", pdfs_dir / "hamayesh.pdf", EXPECTED_OCR_PAGES_DEFECTIVE["hamayesh.pdf"], "regime_a"),
        ("bein-almelali-scan.pdf", scan_pdf_path, EXPECTED_OCR_PAGES_DEFECTIVE["bein-almelali-scan.pdf"], "regime_b"),
    ]

    regime_a_pages = 0
    regime_a_elapsed = 0.0
    regime_b_pages = 0
    regime_b_elapsed = 0.0
    file_details = {}

    ocr_endpoint = f"{sidecar_url.rstrip('/')}/ocr"

    for file_name, file_path, pages, regime in target_files:
        if not file_path.exists():
            print(f"  [WARN] PDF file {file_path} not found. Skipping {file_name}.")
            continue

        pages_str = ",".join(str(p) for p in pages)
        page_count = len(pages)

        with open(file_path, "rb") as f:
            files = {"file": (file_name, f, "application/pdf")}
            data = {
                "pages": pages_str,
                "dpi": 300,
                "lang": "fas+eng",
                "psm": 6,
            }

            t0 = time.perf_counter()
            resp = requests.post(ocr_endpoint, files=files, data=data, timeout=timeout)
            elapsed = time.perf_counter() - t0

            resp.raise_for_status()
            returned_pages = resp.json().get("pages", [])
            assert len(returned_pages) == page_count, (
                f"OCR returned {len(returned_pages)} pages, expected {page_count} for {file_name}"
            )

        sec_per_page = elapsed / page_count if page_count > 0 else 0.0
        file_details[file_name] = {
            "regime": regime,
            "page_count": page_count,
            "elapsed_sec": round(elapsed, 3),
            "sec_per_page": round(sec_per_page, 3),
        }
        print(f"  {file_name}: {page_count} pages OCR'd in {elapsed:.2f}s ({sec_per_page:.2f} s/page)")

        if regime == "regime_a":
            regime_a_pages += page_count
            regime_a_elapsed += elapsed
        else:
            regime_b_pages += page_count
            regime_b_elapsed += elapsed

    total_pages = regime_a_pages + regime_b_pages
    total_elapsed = regime_a_elapsed + regime_b_elapsed

    reg_a_sec_per_page = round(regime_a_elapsed / regime_a_pages, 3) if regime_a_pages > 0 else 0.0
    reg_b_sec_per_page = round(regime_b_elapsed / regime_b_pages, 3) if regime_b_pages > 0 else 0.0
    overall_sec_per_page = round(total_elapsed / total_pages, 3) if total_pages > 0 else 0.0

    result = {
        "timestamp_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "sidecar_url": sidecar_url,
        "dpi": 300,
        "lang": "fas+eng",
        "psm": 6,
        "regime_a_cmap_pages": regime_a_pages,
        "regime_a_elapsed_sec": round(regime_a_elapsed, 3),
        "regime_a_sec_per_page": reg_a_sec_per_page,
        "regime_b_scan_pages": regime_b_pages,
        "regime_b_elapsed_sec": round(regime_b_elapsed, 3),
        "regime_b_sec_per_page": reg_b_sec_per_page,
        "overall_pages": total_pages,
        "overall_elapsed_sec": round(total_elapsed, 3),
        "pure_ocr_sec_per_page": overall_sec_per_page,
        "file_details": file_details,
    }

    with open(timing_file, "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False, indent=2)

    print(f"[OK] Pure OCR timing saved to {timing_file}:")
    print(f"     Regime A (CMap, {regime_a_pages} p): {reg_a_sec_per_page} s/page")
    print(f"     Regime B (Scan, {regime_b_pages} p): {reg_b_sec_per_page} s/page")
    print(f"     Overall ({total_pages} p):           {overall_sec_per_page} s/page")
    return result


# --- Ingestion Verification Routine (Fix 1, Fix 4, Fix 7) ---

def run_ingestion_phase(args: argparse.Namespace, config: Dict[str, Any], token: str) -> None:
    client = DocSearchClient(args.base_url, token)
    ocr_mode = args.ocr_mode
    if not ocr_mode:
        raise ValueError("Must specify --ocr-mode Off or --ocr-mode Defective with --ingest-only.")

    cache_dir = args.cache_dir if hasattr(args, "cache_dir") else Path("eval/cache")
    cache_dir.mkdir(parents=True, exist_ok=True)
    manifest_path = cache_dir / "collection_manifest.json"
    manifest = load_collection_manifest(manifest_path)

    git_sha = get_git_sha()

    print(f"\n========================================================")
    print(f"  DocSearch Ingestion Phase: OcrMode = {ocr_mode}")
    print(f"========================================================")

    if ocr_mode == "Off":
        target_collections = ["regime_a_ocr_off", "regime_b_scan_off"]
    else:
        target_collections = ["regime_a_ocr_defective", "regime_b_scan_defective"]

    for col_key in target_collections:
        col_meta = config["collections"][col_key]
        col_name = col_meta["id_name"]
        subset = col_meta["pdfs_subset"]
        print(f"\n--- Preparing Collection: {col_name} ({subset}) ---")

        # Check if collection can be reused
        col_id = None
        if args.reuse_collections:
            if col_name in manifest:
                col_id = manifest[col_name].get("id")
            if not col_id:
                for c in client.get_collections():
                    if c.get("name") == col_name:
                        col_id = c["id"]
                        break

            if col_id:
                docs = client.list_documents(col_id)
                expected_count = 14 if subset == "digital_14" else 1
                if len(docs) == expected_count:
                    all_ok = True
                    for d in docs:
                        is_scan_off = (d.get("fileName") == "bein-almelali-scan.pdf" and ocr_mode == "Off")
                        if is_scan_off:
                            if d.get("status") != "Failed":
                                all_ok = False
                        else:
                            if d.get("status") != "Ready":
                                all_ok = False
                    if all_ok:
                        print(f"  [REUSE] Collection {col_name} ({col_id}) already exists with {len(docs)} verified document(s). Skipping upload.")
                        manifest[col_name] = {
                            "id": col_id,
                            "collection_key": col_key,
                            "name": col_name,
                            "git_sha": git_sha,
                            "ocr_mode": ocr_mode,
                            "doc_count": len(docs),
                            "updated_at_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                        }
                        save_collection_manifest(manifest_path, manifest)
                        continue

        if not col_id:
            col_id = client.create_collection(col_name, col_meta.get("description", ""))

        # Identify files to upload
        if subset == "digital_14":
            pdf_files = sorted(args.pdfs_dir.glob("*.pdf"))
        else:
            pdf_files = [args.scan_pdf]

        existing_docs = {d["fileName"]: d for d in client.list_documents(col_id)}
        print(f"Uploading / verifying {len(pdf_files)} PDF(s) in {col_name}...")

        for pdf_path in pdf_files:
            file_name = pdf_path.name
            is_scan = (file_name == "bein-almelali-scan.pdf")
            expect_empty = (is_scan and ocr_mode == "Off")

            # Resumable check: skip if Ready, delete and re-upload if Failed (except expected empty scan)
            existing = existing_docs.get(file_name)
            if existing:
                doc_status = existing.get("status")
                doc_id = existing.get("id")

                if doc_status == "Ready":
                    doc_detail = client.get_document(doc_id)
                    ocr_pages = doc_detail.get("ocrPages") or []
                    print(f"  {file_name}: already Ready (id: {doc_id}, OCR pages: {ocr_pages}). Skipping re-upload.")

                    # Assert exact expected OCR pages
                    if ocr_mode == "Off":
                        assert len(ocr_pages) == 0, f"Expected 0 OCR pages in OcrMode=Off, but {file_name} had {ocr_pages}!"
                    else:
                        expected_pages = EXPECTED_OCR_PAGES_DEFECTIVE.get(file_name, [])
                        assert ocr_pages == expected_pages, (
                            f"OCR page set mismatch for {file_name} in Defective mode! "
                            f"Expected {expected_pages}, got {ocr_pages}"
                        )
                    continue

                if doc_status == "Failed":
                    if expect_empty:
                        print(f"  {file_name}: already in expected Failed state (0 text). Skipping re-upload.")
                        continue
                    else:
                        print(f"  {file_name}: previously Failed (id: {doc_id}). Deleting and re-uploading cleanly...")
                        client.delete_document(doc_id)
                        existing_docs.pop(file_name, None)

            upload_start = time.perf_counter()
            res = client.upload_document(col_id, pdf_path)
            doc_id = res.get("id")

            if doc_id == "duplicate":
                curr_docs = {d["fileName"]: d for d in client.list_documents(col_id)}
                doc_id = curr_docs.get(file_name, {}).get("id", doc_id)

            doc_detail = client.wait_until_ready(doc_id, timeout_sec=900.0, expect_empty=expect_empty)
            elapsed = time.perf_counter() - upload_start
            page_count = doc_detail.get("pageCount") or 1
            sec_per_page = elapsed / page_count

            ocr_pages = doc_detail.get("ocrPages") or []
            print(f"  {file_name}: {doc_detail.get('status')} ({page_count} pages, {elapsed:.1f}s, {sec_per_page:.2f} s/page, OCR pages: {ocr_pages})")

            # Assert exact expected OCR pages
            if ocr_mode == "Off":
                assert len(ocr_pages) == 0, f"Expected 0 OCR pages in OcrMode=Off, but {file_name} had {ocr_pages}!"
            else:
                expected_pages = EXPECTED_OCR_PAGES_DEFECTIVE.get(file_name, [])
                assert ocr_pages == expected_pages, (
                    f"OCR page set mismatch for {file_name} in Defective mode! "
                    f"Expected {expected_pages}, got {ocr_pages}"
                )

        manifest[col_name] = {
            "id": col_id,
            "collection_key": col_key,
            "name": col_name,
            "git_sha": git_sha,
            "ocr_mode": ocr_mode,
            "doc_count": len(pdf_files),
            "updated_at_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        }
        save_collection_manifest(manifest_path, manifest)

    # In Defective mode, automatically run pure OCR benchmark on sidecar
    if ocr_mode == "Defective":
        try:
            benchmark_pure_ocr(
                sidecar_url=args.sidecar_url,
                pdfs_dir=args.pdfs_dir,
                scan_pdf_path=args.scan_pdf,
                cache_dir=cache_dir,
            )
        except Exception as ex:
            print(f"[WARN] Sidecar pure OCR benchmark failed or sidecar not available: {ex}")

    print(f"\n[OK] Ingestion and OCR verification for OcrMode={ocr_mode} successfully verified!")
    print(f"     Collection manifest saved to: {manifest_path}")


# --- Stage 1 Evaluation Routine ---

def run_stage1_eval(args: argparse.Namespace, config: Dict[str, Any], token: str) -> None:
    client = DocSearchClient(args.base_url, token)

    # 1. Load questions
    with open(args.questions, "r", encoding="utf-8") as f:
        all_questions = json.load(f)

    if args.smoke:
        all_questions = all_questions[:5]
        print(f"[SMOKE TEST] Evaluating only {len(all_questions)} questions.")

    # 2. Map collection names to IDs (manifest first, then live API fallback)
    cache_dir = args.cache_dir if hasattr(args, "cache_dir") else Path("eval/cache")
    manifest_path = cache_dir / "collection_manifest.json"
    manifest = load_collection_manifest(manifest_path)

    existing_cols = {c["name"]: c["id"] for c in client.get_collections()}
    col_map = {}
    for col_key, col_meta in config["collections"].items():
        name = col_meta["id_name"]
        if name in manifest and manifest[name].get("id"):
            col_id = manifest[name]["id"]
        elif name in existing_cols:
            col_id = existing_cols[name]
        else:
            raise RuntimeError(
                f"Required collection '{name}' ({col_key}) not found in manifest or on server! "
                "Run --ingest-only --ocr-mode Off and --ingest-only --ocr-mode Defective first."
            )
        col_map[col_key] = col_id

        # Verify collection documents when --reuse-collections or --eval-only is passed
        if args.reuse_collections or args.eval_only:
            docs = client.list_documents(col_id)
            expected_count = 14 if col_meta["pdfs_subset"] == "digital_14" else 1
            if len(docs) != expected_count:
                print(f"[WARN] Collection {name} has {len(docs)} documents, expected {expected_count}.")
            for d in docs:
                is_scan_off = (d.get("fileName") == "bein-almelali-scan.pdf" and col_meta.get("ocr_mode") == "Off")
                if is_scan_off:
                    if d.get("status") != "Failed":
                        print(f"[WARN] Document {d.get('fileName')} in {name} expected Failed status, got {d.get('status')}")
                else:
                    if d.get("status") != "Ready":
                        raise RuntimeError(f"Document {d.get('fileName')} in {name} is in status '{d.get('status')}' (not Ready)!")

    out_dir = args.out or Path("eval/results") / f"stage1_{datetime.datetime.now().strftime('%Y%m%d_%H%M%S')}"
    out_dir.mkdir(parents=True, exist_ok=True)

    print(f"\n========================================================")
    print(f"  DocSearch Stage 1 Evaluation (Multi-Arm Benchmark)")
    print(f"  Output directory: {out_dir}")
    print(f"========================================================")

    k_list = config["global"]["top_k_list"]
    arms_data: Dict[str, List[Dict[str, Any]]] = {}

    # 3. Execute retrieval across arms
    for arm_key, arm_meta in config["arms"].items():
        arm_name = arm_meta["name"]
        col_ref = arm_meta["collection_ref"]
        col_id = col_map[col_ref]
        use_hybrid = arm_meta.get("use_hybrid", True)
        twin_mapping = arm_meta.get("twin_mapping")

        print(f"\n--- Evaluating Arm: {arm_name} ---")
        arm_results = []

        # Filter questions: Regime B only runs scan gold questions
        if "regime_b" in arm_key:
            target_qs = [q for q in all_questions if q.get("source_file") == "bein-almelali.pdf" and q.get("type") != "not-in-docs"]
        else:
            target_qs = all_questions

        for idx, q in enumerate(target_qs, 1):
            q_id = q.get("id", f"q{idx:03d}")
            q_text = q["question"]
            gold_file = q.get("source_file")
            gold_pages = q.get("source_pages") or []

            try:
                ret_resp = client.dev_retrieve(q_text, [col_id], top_k=max(k_list), use_hybrid=use_hybrid)
                best_score = ret_resp.get("bestVectorScore", 0.0)
                has_fts = ret_resp.get("hasFtsHit", False)
                elapsed_ms = ret_resp.get("elapsedMs", 0.0)
                chunks = ret_resp.get("chunks", [])
            except Exception as e:
                # If collection is empty (e.g. regime_b_scan_off), treat as 0 retrieved
                best_score = 0.0
                has_fts = False
                elapsed_ms = 0.0
                chunks = []

            # Score standard and strict hit rules
            std_eval = evaluate_retrieval_ranking(chunks, gold_file, gold_pages, k_list, strict=False, twin_mapping=twin_mapping)
            strict_eval = evaluate_retrieval_ranking(chunks, gold_file, gold_pages, k_list, strict=True, twin_mapping=twin_mapping)

            record = {
                "id": q_id,
                "question": q_text,
                "docset": q.get("docset"),
                "split": q.get("split"),
                "type": q.get("type"),
                "source_file": gold_file,
                "source_pages": gold_pages,
                "text_layer_defect": q.get("text_layer_defect", False),
                "defect_mechanism": q.get("defect_mechanism", "none"),
                "bestVectorScore": best_score,
                "hasFtsHit": has_fts,
                "elapsedMs": elapsed_ms,
                "retrieved_chunk_count": len(chunks),
                "mrr": std_eval["mrr"],
                "strict_mrr": strict_eval["mrr"],
                "hits_at_k": std_eval["hits_at_k"],
                "strict_hits_at_k": strict_eval["hits_at_k"],
            }
            arm_results.append(record)

            if args.sleep > 0:
                time.sleep(args.sleep)

        arms_data[arm_key] = arm_results
        print(f"  Processed {len(arm_results)} questions.")

    # 4. Per-Arm Plateau Threshold Calibration on (tuned, dev)
    calibrations: Dict[str, Dict[str, Any]] = {}
    print(f"\n--- Running Per-Arm Plateau Calibration on (tuned, dev) ---")
    for arm_key in ["arm1_vector_ocr_off", "arm2_hybrid_ocr_off", "arm3_hybrid_ocr_defective"]:
        dev_subset = [it for it in arms_data[arm_key] if it.get("docset") == "tuned" and it.get("split") == "dev"]
        if not args.smoke:
            calib = calibrate_min_similarity(dev_subset)
            calibrations[arm_key] = calib
            print(f"  {arm_key}: Locked MinSimilarity = {calib['locked_min_similarity']} (Plateau: {calib['plateau_range']}, Error: {calib['best_balanced_error']})")
        else:
            calibrations[arm_key] = {"locked_min_similarity": 0.55}

    # 5. Cohort Metrics & Statistical Significance
    regime_b_off_items = arms_data.get("regime_b_scan_off", [])
    regime_b_def_items = arms_data.get("regime_b_scan_defective", [])
    regime_b_ref_items = [
        it for it in arms_data.get("arm2_hybrid_ocr_off", [])
        if it.get("source_file") == "bein-almelali.pdf" and it.get("type") != "not-in-docs"
    ]
    n_b = len(regime_b_def_items)
    regime_b_summary = {}
    if n_b > 0 and len(regime_b_off_items) > 0 and len(regime_b_ref_items) > 0:
        mrr_off = float(np.mean([it["mrr"] for it in regime_b_off_items]))
        mrr_def = float(np.mean([it["mrr"] for it in regime_b_def_items]))
        mrr_ref = float(np.mean([it["mrr"] for it in regime_b_ref_items]))
        r1_off = float(np.mean([get_hit_at_k(it, 1) for it in regime_b_off_items]))
        r1_def = float(np.mean([get_hit_at_k(it, 1) for it in regime_b_def_items]))
        r1_ref = float(np.mean([get_hit_at_k(it, 1) for it in regime_b_ref_items]))
        r5_off = float(np.mean([get_hit_at_k(it, 5) for it in regime_b_off_items]))
        r5_def = float(np.mean([get_hit_at_k(it, 5) for it in regime_b_def_items]))
        r5_ref = float(np.mean([get_hit_at_k(it, 5) for it in regime_b_ref_items]))

        regime_b_summary = {
            "question_count": n_b,
            "scan_off": {"mrr": round(mrr_off, 4), "r1": round(r1_off, 4), "r5": round(r5_off, 4)},
            "scan_defective": {"mrr": round(mrr_def, 4), "r1": round(r1_def, 4), "r5": round(r5_def, 4)},
            "digital_reference": {"mrr": round(mrr_ref, 4), "r1": round(r1_ref, 4), "r5": round(r5_ref, 4)},
            "delta_mrr_defective_vs_off": round(mrr_def - mrr_off, 4),
            "delta_mrr_defective_vs_digital_ref": round(mrr_def - mrr_ref, 4),
        }

    # Load pure OCR timing if available
    ocr_timing_path = cache_dir / "ocr_timing.json"
    ocr_timing = {}
    if ocr_timing_path.exists():
        try:
            with open(ocr_timing_path, "r", encoding="utf-8") as f:
                ocr_timing = json.load(f)
        except Exception as e:
            print(f"[WARN] Failed to load ocr_timing.json: {e}")

    # 6. Save Manifest and Results
    manifest = build_reproducibility_manifest(args.questions, args.base_url)
    results_json_path = out_dir / "results.json"
    results_dict = {
        "manifest": manifest,
        "ocr_timing": ocr_timing,
        "calibrations": calibrations,
        "regime_b_summary": regime_b_summary,
        "arms": arms_data,
    }
    with open(results_json_path, "w", encoding="utf-8") as f:
        json.dump(results_dict, f, ensure_ascii=False, indent=2)

    # 7. Generate Reports
    out_paths = generate_stage1_reports(results_dict, out_dir, smoke=args.smoke)

    print(f"\n[OK] Stage 1 evaluation complete!")
    print(f"  Results JSON: {results_json_path}")
    print(f"  Summary CSV:  {out_paths['summary_csv']}")
    if "regime_b_csv" in out_paths:
        print(f"  Regime B CSV: {out_paths['regime_b_csv']}")
    print(f"  Report MD:    {out_paths['report_md']}")


def generate_stage1_reports(
    results_data: Dict[str, Any],
    out_dir: Path,
    smoke: bool = False,
) -> Dict[str, Path]:
    """Generates summary.csv, regime_b_summary.csv, and report.md from results data."""
    out_dir.mkdir(parents=True, exist_ok=True)
    manifest = results_data.get("manifest", {})
    ocr_timing = results_data.get("ocr_timing", {})
    calibrations = results_data.get("calibrations", {})
    regime_b_summary = results_data.get("regime_b_summary", {})
    arms_data = results_data.get("arms", {})

    # Compute summary_rows for Regime A cohorts strictly (excluding Regime B)
    summary_rows = []
    print(f"\n--- Computing Stage 1 Metrics & Paired Bootstrap CIs ---")
    for c_name, c_filter in COHORTS_DEF:
        row = {"cohort": c_name}
        for arm_key in ["arm1_vector_ocr_off", "arm2_hybrid_ocr_off", "arm3_hybrid_ocr_defective"]:
            items = [it for it in arms_data.get(arm_key, []) if c_filter(it) and it.get("type") != "not-in-docs"]
            n = len(items)
            row[f"{arm_key}_N"] = n
            if n > 0:
                row[f"{arm_key}_R1"] = round(float(np.mean([get_hit_at_k(it, 1) for it in items])), 4)
                row[f"{arm_key}_R5"] = round(float(np.mean([get_hit_at_k(it, 5) for it in items])), 4)
                row[f"{arm_key}_MRR"] = round(float(np.mean([it["mrr"] for it in items])), 4)
                row[f"{arm_key}_StrictMRR"] = round(float(np.mean([it["strict_mrr"] for it in items])), 4)

        # Paired differences: Arm 2 vs Arm 1 & Arm 3 vs Arm 2
        items_1 = [it for it in arms_data.get("arm1_vector_ocr_off", []) if c_filter(it) and it.get("type") != "not-in-docs"]
        items_2 = [it for it in arms_data.get("arm2_hybrid_ocr_off", []) if c_filter(it) and it.get("type") != "not-in-docs"]
        items_3 = [it for it in arms_data.get("arm3_hybrid_ocr_defective", []) if c_filter(it) and it.get("type") != "not-in-docs"]

        if len(items_1) > 0 and len(items_2) > 0 and not smoke:
            boot_q_2v1 = paired_bootstrap_ci([it["mrr"] for it in items_1], [it["mrr"] for it in items_2])
            boot_c_2v1 = document_cluster_paired_bootstrap_ci(items_1, items_2, "mrr")
            row["delta_mrr_arm2_vs_arm1"] = boot_q_2v1["mean_delta"]
            row["ci_q_arm2_vs_arm1"] = f"[{boot_q_2v1['ci_low']}, {boot_q_2v1['ci_high']}]"
            row["ci_cluster_arm2_vs_arm1"] = f"[{boot_c_2v1['ci_low']}, {boot_c_2v1['ci_high']}]" if boot_c_2v1["status"] == "valid" else boot_c_2v1["status"]

            boot_q_3v2 = paired_bootstrap_ci([it["mrr"] for it in items_2], [it["mrr"] for it in items_3])
            boot_c_3v2 = document_cluster_paired_bootstrap_ci(items_2, items_3, "mrr")
            row["delta_mrr_arm3_vs_arm2"] = boot_q_3v2["mean_delta"]
            row["ci_q_arm3_vs_arm2"] = f"[{boot_q_3v2['ci_low']}, {boot_q_3v2['ci_high']}]"
            row["ci_cluster_arm3_vs_arm2"] = f"[{boot_c_3v2['ci_low']}, {boot_c_3v2['ci_high']}]" if boot_c_3v2["status"] == "valid" else boot_c_3v2["status"]

        summary_rows.append(row)

    # 1. Write Summary CSV (Regime A)
    summary_csv_path = out_dir / "summary.csv"
    if summary_rows:
        fieldnames = list(summary_rows[0].keys())
        with open(summary_csv_path, "w", newline="", encoding="utf-8") as f:
            writer = csv.DictWriter(f, fieldnames=fieldnames)
            writer.writeheader()
            writer.writerows(summary_rows)

    # 2. Write Dedicated Regime B Summary CSV
    regime_b_csv_path = out_dir / "regime_b_summary.csv"
    if regime_b_summary:
        with open(regime_b_csv_path, "w", newline="", encoding="utf-8") as f:
            writer = csv.writer(f)
            writer.writerow(["variant", "question_count", "mrr", "recall_at_1", "recall_at_5"])
            q_cnt = regime_b_summary.get("question_count", 8)
            s_off = regime_b_summary.get("scan_off", {})
            s_def = regime_b_summary.get("scan_defective", {})
            d_ref = regime_b_summary.get("digital_reference", {})
            writer.writerow(["scan_twin_ocr_off", q_cnt, s_off.get("mrr", 0.0), s_off.get("r1", 0.0), s_off.get("r5", 0.0)])
            writer.writerow(["scan_twin_ocr_defective", q_cnt, s_def.get("mrr", 0.0), s_def.get("r1", 0.0), s_def.get("r5", 0.0)])
            writer.writerow(["digital_reference_hybrid", q_cnt, d_ref.get("mrr", 0.0), d_ref.get("r1", 0.0), d_ref.get("r5", 0.0)])

    # 3. Write Report Markdown
    report_md_path = out_dir / "report.md"
    data_git_sha = manifest.get("git_sha", "unknown")
    current_git_sha = get_git_sha()
    current_git_dirty = False
    try:
        status_out = subprocess.check_output(["git", "status", "--porcelain"], stderr=subprocess.DEVNULL, text=True).strip()
        current_git_dirty = len(status_out) > 0
    except Exception:
        pass

    with open(report_md_path, "w", encoding="utf-8") as f:
        f.write("# DocSearch Stage 1 Evaluation Report (T19a)\n\n")
        f.write(f"- **Data generated at Git SHA:** `{data_git_sha}`\n")
        f.write(f"- **Report rendered at Git SHA:** `{current_git_sha}` (dirty: `{current_git_dirty}`)\n")
        f.write(f"- **Dataset Tag:** `eval-dataset-v2.2`\n")
        f.write(f"- **Timestamp UTC:** {manifest.get('timestamp_utc', 'unknown')}\n\n")

        f.write("## 1. Pre-Registered Primary Endpoints\n\n")
        f.write("1. **Primary Endpoint 1 (Hybrid Generalization):** `heldout` answerable questions ($N=52$) $\\Delta$MRR for `Arm 2 (Hybrid) vs Arm 1 (Vector-Only)`.\n")
        f.write("2. **Primary Endpoint 2 (Selective OCR Generalization):** `heldout-CMap` ($N=27$) $\\Delta$MRR for `Arm 3 (Full System) vs Arm 2 (OCR Off)`.\n\n")

        f.write("## 2. Per-Arm Similarity Calibration (Tuned | Dev)\n\n")
        f.write("| Arm | Locked MinSimilarity | Plateau Center Range | Best Balanced Error | Pure Cosine Center |\n")
        f.write("|---|---|---|---|---|\n")
        for arm_k, c_info in calibrations.items():
            cos_info = c_info.get("cosine_only_sensitivity", {})
            plateau = c_info.get("plateau_range")
            if isinstance(plateau, (list, tuple)) and len(plateau) == 2:
                plateau_str = f"[{float(plateau[0]):.4g}, {float(plateau[1]):.4g}]"
            elif isinstance(plateau, str):
                plateau_str = plateau.replace("np.float64(", "").replace(")", "")
            else:
                plateau_str = str(plateau)

            locked_sim = c_info.get("locked_min_similarity")
            locked_sim_str = f"**{float(locked_sim):.4g}**" if locked_sim is not None else "**N/A**"

            cos_locked = cos_info.get("locked_min_similarity")
            cos_locked_str = f"{float(cos_locked):.4g}" if cos_locked is not None else "N/A"

            f.write(
                f"| `{arm_k}` | {locked_sim_str} | "
                f"{plateau_str} | {c_info.get('best_balanced_error', 'N/A')} | "
                f"{cos_locked_str} |\n"
            )
        f.write("\n")

        f.write("## 3. Retrieval Performance Summary by Cohort\n\n")
        f.write("| Cohort | Arm 1 MRR | Arm 2 MRR | Arm 3 MRR | ΔMRR (2 vs 1, Q-CI) | ΔMRR (3 vs 2, Q-CI) | Cluster CI (3 vs 2) |\n")
        f.write("|---|---|---|---|---|---|---|\n")
        for row in summary_rows:
            f.write(
                f"| `{row.get('cohort')}` | {row.get('arm1_vector_ocr_off_MRR', '-')} | "
                f"{row.get('arm2_hybrid_ocr_off_MRR', '-')} | {row.get('arm3_hybrid_ocr_defective_MRR', '-')} | "
                f"{row.get('delta_mrr_arm2_vs_arm1', '-')} {row.get('ci_q_arm2_vs_arm1', '')} | "
                f"{row.get('delta_mrr_arm3_vs_arm2', '-')} {row.get('ci_q_arm3_vs_arm2', '')} | "
                f"{row.get('ci_cluster_arm3_vs_arm2', '-')} |\n"
            )
        f.write("\n")

        if regime_b_summary:
            f.write("## 4. Regime B: Scanned Twin Evaluation (8 Questions)\n\n")
            f.write("| Variant | MRR | Recall@1 | Recall@5 |\n")
            f.write("|---|---|---|---|\n")
            f.write(f"| `Scan Twin (OCR Off)` | {regime_b_summary['scan_off']['mrr']} | {regime_b_summary['scan_off']['r1']} | {regime_b_summary['scan_off']['r5']} |\n")
            f.write(f"| `Scan Twin (Selective OCR)` | {regime_b_summary['scan_defective']['mrr']} | {regime_b_summary['scan_defective']['r1']} | {regime_b_summary['scan_defective']['r5']} |\n")
            f.write(f"| `Digital Reference (Hybrid)` | {regime_b_summary['digital_reference']['mrr']} | {regime_b_summary['digital_reference']['r1']} | {regime_b_summary['digital_reference']['r5']} |\n\n")
            f.write(f"- **ΔMRR (Defective vs Off):** `{regime_b_summary['delta_mrr_defective_vs_off']}`\n")
            f.write(f"- **ΔMRR (Defective vs Digital Ref):** `{regime_b_summary['delta_mrr_defective_vs_digital_ref']}`\n\n")

        if ocr_timing:
            f.write("## 5. Pure OCR Timing Benchmark (Extractor Sidecar)\n\n")
            f.write(f"- **Overall Pure OCR:** `{ocr_timing.get('pure_ocr_sec_per_page', 'N/A')} s/page` ({ocr_timing.get('overall_pages', 0)} pages in {ocr_timing.get('overall_elapsed_sec', 0)}s)\n")
            f.write(f"- **Regime A (CMap Defective):** `{ocr_timing.get('regime_a_sec_per_page', 'N/A')} s/page` ({ocr_timing.get('regime_a_cmap_pages', 0)} pages)\n")
            f.write(f"- **Regime B (Scan Twin):** `{ocr_timing.get('regime_b_sec_per_page', 'N/A')} s/page` ({ocr_timing.get('regime_b_scan_pages', 0)} pages)\n\n")

    return {
        "summary_csv": summary_csv_path,
        "regime_b_csv": regime_b_csv_path,
        "report_md": report_md_path,
    }


def main() -> None:
    args = parse_args()

    # Offline report regeneration from existing results.json (no API, Docker, or Ollama required)
    if args.regenerate_report:
        if not args.regenerate_report.exists():
            raise FileNotFoundError(f"Results file {args.regenerate_report} not found.")
        with open(args.regenerate_report, "r", encoding="utf-8") as f:
            results_data = json.load(f)
        out_dir = args.out or args.regenerate_report.parent
        out_paths = generate_stage1_reports(results_data, out_dir, smoke=args.smoke)
        print(f"\n[OK] Reports regenerated successfully in {out_dir}:")
        print(f"  Summary CSV:  {out_paths['summary_csv']}")
        if "regime_b_csv" in out_paths:
            print(f"  Regime B CSV: {out_paths['regime_b_csv']}")
        print(f"  Report MD:    {out_paths['report_md']}")
        return

    # Offline Stage 2 report & plot generation (no API, Docker, or Ollama required)
    if args.stage2_report:
        from stage2 import run_stage2_report
        run_stage2_report(args)
        return

    # Standalone pure OCR benchmark on sidecar (no API login required)
    if args.benchmark_ocr:
        benchmark_pure_ocr(
            sidecar_url=args.sidecar_url,
            pdfs_dir=args.pdfs_dir,
            scan_pdf_path=args.scan_pdf,
            cache_dir=args.cache_dir,
        )
        return

    # Load declarative config
    if not args.config.exists():
        raise FileNotFoundError(f"Configuration file {args.config} not found.")
    with open(args.config, "r", encoding="utf-8") as f:
        config = yaml.safe_load(f)

    # Authenticate once
    token = get_auth_token(args.base_url, args.email, args.password)

    if args.ingest_only:
        run_ingestion_phase(args, config, token)
    elif args.validate_offline_sim:
        from stage2 import run_validate_offline_sim
        run_validate_offline_sim(args, config, token)
    elif args.stage2:
        from stage2 import run_stage2_eval
        run_stage2_eval(args, config, token)
    else:
        run_stage1_eval(args, config, token)


if __name__ == "__main__":
    main()
