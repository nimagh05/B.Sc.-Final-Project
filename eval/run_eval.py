"""Evaluation harness CLI for DocSearch.

Evaluates retrieval and generation quality against questions in eval/questions.json.
Full metrics (Recall@K, MRR, correctness, bootstrap CI) and plotting arrive in T19.

Usage:
    python eval/run_eval.py --base-url http://localhost:5000 --collection <COLLECTION_ID> \
                            --email admin@ci.test --password Ci_Admin_123! \
                            --split test --out eval/results/run-01
"""

import argparse
import datetime
import json
import os
import sys
import time
from pathlib import Path
from typing import Any, Dict, List, Optional

import requests


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Run DocSearch evaluation harness against a collection.",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter,
    )
    parser.add_argument(
        "--base-url",
        default=os.environ.get("BASE_URL", "http://localhost:5000"),
        help="Base URL of DocSearch API",
    )
    parser.add_argument(
        "--email",
        default=os.environ.get("ADMIN_EMAIL", "admin@ci.test"),
        help="User email for API authentication",
    )
    parser.add_argument(
        "--password",
        default=os.environ.get("ADMIN_PASSWORD", "Ci_Admin_123!"),
        help="User password for API authentication",
    )
    parser.add_argument(
        "--collection",
        required=False,
        default=None,
        help="Target collection UUID to query",
    )
    parser.add_argument(
        "--questions",
        type=Path,
        default=Path("eval/questions.json"),
        help="Path to questions.json dataset",
    )
    parser.add_argument(
        "--split",
        choices=["dev", "test", "all"],
        default="test",
        help="Dataset split to evaluate",
    )
    parser.add_argument(
        "--out",
        type=Path,
        default=Path("eval/results/skeleton_run"),
        help="Output directory for results",
    )
    parser.add_argument(
        "--k",
        nargs="+",
        type=int,
        default=[1, 3, 5, 10],
        help="Retrieval top-K evaluation thresholds",
    )
    parser.add_argument(
        "--timeout",
        type=float,
        default=60.0,
        help="Per-question HTTP request timeout in seconds",
    )
    parser.add_argument(
        "--sleep",
        type=float,
        default=0.0,
        help="Delay in seconds between requests to avoid rate limits",
    )
    parser.add_argument(
        "--resume",
        action="store_true",
        help="Resume from existing cached results in out directory",
    )
    parser.add_argument(
        "--limit",
        type=int,
        default=None,
        help="Optional limit on number of questions to evaluate (for dry runs)",
    )
    parser.add_argument(
        "--judge",
        action="store_true",
        help="Enable LLM-as-judge scoring (feature in T19)",
    )
    return parser.parse_args()


def login(base_url: str, email: str, password: str) -> str:
    """Authenticates against /api/v1/auth/login and returns JWT access token."""
    login_url = f"{base_url.rstrip('/')}/api/v1/auth/login"
    try:
        resp = requests.post(login_url, json={"email": email, "password": password}, timeout=15.0)
        resp.raise_for_status()
        token = resp.json().get("accessToken")
        if not token:
            raise ValueError(f"No accessToken returned by {login_url}: {resp.text}")
        return token
    except Exception as exc:
        raise RuntimeError(f"Authentication failed against {login_url}: {exc}") from exc


def load_questions(questions_path: Path, split: str, limit: Optional[int]) -> List[Dict[str, Any]]:
    """Loads and filters questions by split."""
    if not questions_path.exists():
        raise FileNotFoundError(f"Questions file not found: {questions_path}")

    with open(questions_path, "r", encoding="utf-8") as f:
        data = json.load(f)

    if split != "all":
        data = [q for q in data if q.get("split") == split]

    if limit is not None and limit > 0:
        data = data[:limit]

    return data


def run_evaluation(args: argparse.Namespace) -> None:
    print(f"DocSearch Evaluation Harness")
    print(f"Target API:     {args.base_url}")
    print(f"Questions file: {args.questions} (split: {args.split})")
    print(f"Output dir:     {args.out}")

    questions = load_questions(args.questions, args.split, args.limit)
    print(f"Loaded {len(questions)} questions.")

    if not args.collection:
        print("[WARNING] No target --collection specified. Dry run mode only.")
        return

    # Authenticate
    token = login(args.base_url, args.email, args.password)
    headers = {
        "Authorization": f"Bearer {token}",
        "Content-Type": "application/json",
    }

    args.out.mkdir(parents=True, exist_ok=True)
    results_path = args.out / "results.json"

    cached_results: Dict[str, Dict[str, Any]] = {}
    if args.resume and results_path.exists():
        try:
            with open(results_path, "r", encoding="utf-8") as f:
                prev_run = json.load(f)
                for item in prev_run.get("items", []):
                    cached_results[item["question_id"]] = item
            print(f"Resuming run: loaded {len(cached_results)} existing responses.")
        except Exception as e:
            print(f"[WARN] Failed to load previous results for resume: {e}")

    ask_url = f"{args.base_url.rstrip('/')}/api/v1/ask"
    max_k = max(args.k)
    eval_items: List[Dict[str, Any]] = []

    for idx, q in enumerate(questions, start=1):
        q_id = q.get("id", f"q{idx:03d}")

        if args.resume and q_id in cached_results:
            print(f"[{idx}/{len(questions)}] {q_id} (cached)")
            eval_items.append(cached_results[q_id])
            continue

        payload = {
            "question": q["question"],
            "collectionIds": [args.collection],
            "topK": max_k,
        }

        # Issue request with retry on network error
        resp_data = None
        latency_ms = 0.0
        status_code = 0
        error_msg = None

        start_time = time.perf_counter()
        for attempt in (1, 2):
            try:
                resp = requests.post(ask_url, json=payload, headers=headers, timeout=args.timeout)
                latency_ms = (time.perf_counter() - start_time) * 1000.0
                status_code = resp.status_code
                if resp.status_code == 200:
                    resp_data = resp.json()
                    break
                else:
                    error_msg = f"HTTP {resp.status_code}: {resp.text}"
            except requests.exceptions.RequestException as exc:
                latency_ms = (time.perf_counter() - start_time) * 1000.0
                error_msg = str(exc)
                if attempt == 1:
                    time.sleep(1.0)

        record: Dict[str, Any] = {
            "question_id": q_id,
            "question": q["question"],
            "type": q.get("type"),
            "language": q.get("language"),
            "expected_answer": q.get("expected_answer"),
            "answer_keywords": q.get("answer_keywords", []),
            "source_file": q.get("source_file"),
            "source_pages": q.get("source_pages", []),
            "response": {
                "status_code": status_code,
                "latency_ms": round(latency_ms, 2),
                "answer": resp_data.get("answer") if resp_data else None,
                "notFound": resp_data.get("notFound") if resp_data else None,
                "degraded": resp_data.get("degraded") if resp_data else None,
                "conversationId": resp_data.get("conversationId") if resp_data else None,
                "sources": resp_data.get("sources", []) if resp_data else [],
                "error": error_msg,
            },
        }
        eval_items.append(record)
        print(f"[{idx}/{len(questions)}] {q_id} -> HTTP {status_code} ({latency_ms:.1f} ms)")

        if args.sleep > 0:
            time.sleep(args.sleep)

    # Save output JSON
    output_data: Dict[str, Any] = {
        "run_id": args.out.name,
        "timestamp_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "config": {
            "base_url": args.base_url,
            "collection": args.collection,
            "split": args.split,
            "k": args.k,
            "total_questions": len(questions),
        },
        "summary": {
            "total": len(eval_items),
            "successful_calls": sum(1 for item in eval_items if item["response"]["status_code"] == 200),
            "not_found_count": sum(1 for item in eval_items if item["response"].get("notFound") is True),
            "degraded_count": sum(1 for item in eval_items if item["response"].get("degraded") is True),
            "mean_latency_ms": (
                round(sum(item["response"]["latency_ms"] for item in eval_items) / len(eval_items), 2)
                if eval_items
                else 0.0
            ),
        },
        "items": eval_items,
    }

    with open(results_path, "w", encoding="utf-8") as f:
        json.dump(output_data, f, ensure_ascii=False, indent=2)

    print(f"\n[OK] Results successfully saved to {results_path}")
    print(f"Summary: {output_data['summary']}")
    print("Full metrics computation (Recall@K, MRR, correctness, bootstrap CIs) will be executed in T19.")


def main() -> None:
    args = parse_args()
    run_evaluation(args)


if __name__ == "__main__":
    main()
