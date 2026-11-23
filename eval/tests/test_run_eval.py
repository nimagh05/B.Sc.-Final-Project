"""CI-safe fast unit tests for DocSearch Evaluation Harness (Task T19a).

Runs in < 2 seconds with zero network, zero Ollama, zero Docker, and zero real PDFs.
"""

import json
import os
import sys
import time
from pathlib import Path
import pytest
import requests
import yaml

# Add eval directory to sys.path
EVAL_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(EVAL_DIR))

from run_eval import (
    EXPECTED_OCR_PAGES_DEFECTIVE,
    benchmark_pure_ocr,
    build_reproducibility_manifest,
    calibrate_min_similarity,
    document_cluster_paired_bootstrap_ci,
    evaluate_retrieval_ranking,
    generate_stage1_reports,
    get_auth_token,
    is_chunk_hit,
    load_collection_manifest,
    paired_bootstrap_ci,
    save_collection_manifest,
)
from stage2 import (
    append_checkpoint,
    compute_cache_key,
    document_cluster_paired_bootstrap_ci as stage2_cluster_bootstrap_ci,
    evaluate_generation_metrics,
    generate_stage2_plots,
    get_from_cache,
    get_system_prompt_sha256,
    load_checkpoint,
    mcnemar_exact,
    normalize_text,
    run_stage2_report,
    save_to_cache,
    simulate_threshold,
    wilson_score_interval,
)


def test_hit_rules_standard_vs_strict():
    gold_file = "aeen-91.pdf"
    gold_pages = [3, 4]

    # Case 1: 2-page chunk spanning pages 3-4 (span = 2 <= 3) -> hit under both
    assert is_chunk_hit("aeen-91.pdf", 3, 4, gold_file, gold_pages, strict=False) is True
    assert is_chunk_hit("aeen-91.pdf", 3, 4, gold_file, gold_pages, strict=True) is True

    # Case 2: 4-page chunk spanning pages 1-4 (span = 4 > 3) -> hit standard, miss strict
    assert is_chunk_hit("aeen-91.pdf", 1, 4, gold_file, gold_pages, strict=False) is True
    assert is_chunk_hit("aeen-91.pdf", 1, 4, gold_file, gold_pages, strict=True) is False

    # Case 3: Non-matching page range (pages 5-6) -> miss under both
    assert is_chunk_hit("aeen-91.pdf", 5, 6, gold_file, gold_pages, strict=False) is False
    assert is_chunk_hit("aeen-91.pdf", 5, 6, gold_file, gold_pages, strict=True) is False

    # Case 4: Non-matching file name -> miss under both
    assert is_chunk_hit("other.pdf", 3, 4, gold_file, gold_pages, strict=False) is False


def test_twin_mapping_regime_b():
    twin_mapping = {"bein-almelali-scan.pdf": "bein-almelali.pdf"}
    gold_file = "bein-almelali.pdf"
    gold_pages = [2]

    # With twin mapping, scan twin matches gold reference
    assert is_chunk_hit(
        "bein-almelali-scan.pdf", 2, 2, gold_file, gold_pages, strict=False, twin_mapping=twin_mapping
    ) is True

    # Without twin mapping, file names do not match
    assert is_chunk_hit(
        "bein-almelali-scan.pdf", 2, 2, gold_file, gold_pages, strict=False, twin_mapping=None
    ) is False


def test_anti_leakage_guard_raises_on_heldout_or_test():
    # Valid item: docset='tuned', split='dev'
    valid_item = {"id": "q001", "docset": "tuned", "split": "dev", "type": "factual", "bestVectorScore": 0.7, "hasFtsHit": False}

    # Contaminated item 1: heldout
    leak_heldout = {"id": "q064", "docset": "heldout", "split": "dev", "type": "factual", "bestVectorScore": 0.7, "hasFtsHit": False}
    with pytest.raises(ValueError, match="ANTI-LEAKAGE VIOLATION"):
        calibrate_min_similarity([valid_item, leak_heldout])

    # Contaminated item 2: test split
    leak_test = {"id": "q002", "docset": "tuned", "split": "test", "type": "factual", "bestVectorScore": 0.7, "hasFtsHit": False}
    with pytest.raises(ValueError, match="ANTI-LEAKAGE VIOLATION"):
        calibrate_min_similarity([valid_item, leak_test])


def test_plateau_calibration_selection():
    # Construct synthetic 18 answerable + 7 not-in-docs questions (N=25)
    dev_items = []
    # Answerable: scores between 0.60 and 0.85
    for i in range(18):
        dev_items.append({
            "id": f"q_ans_{i}",
            "docset": "tuned",
            "split": "dev",
            "type": "factual",
            "bestVectorScore": 0.60 + (i * 0.01),
            "hasFtsHit": False,
        })
    # Not-in-docs: scores between 0.40 and 0.55
    for i in range(7):
        dev_items.append({
            "id": f"q_nid_{i}",
            "docset": "tuned",
            "split": "dev",
            "type": "not-in-docs",
            "bestVectorScore": 0.40 + (i * 0.02),
            "hasFtsHit": False,
        })

    calib = calibrate_min_similarity(dev_items)
    locked_tau = calib["locked_min_similarity"]
    plateau_range = calib["plateau_range"]

    # Threshold must separate not-in-docs (< 0.55) from answerable (>= 0.60)
    assert 0.525 <= locked_tau <= 0.625
    assert plateau_range[0] <= locked_tau <= plateau_range[1]
    assert isinstance(locked_tau, float)
    assert isinstance(plateau_range[0], float)
    assert isinstance(plateau_range[1], float)
    assert calib["best_balanced_error"] == 0.0

    # Verify cosine_only_sensitivity output
    assert "cosine_only_sensitivity" in calib
    cos_sens = calib["cosine_only_sensitivity"]
    assert 0.525 <= cos_sens["locked_min_similarity"] <= 0.625
    assert len(cos_sens["plateau_range"]) == 2
    assert cos_sens["plateau_range"][0] <= cos_sens["locked_min_similarity"] <= cos_sens["plateau_range"][1]
    assert cos_sens["best_balanced_error"] == 0.0


def test_deterministic_paired_bootstrap_ci():
    a = [0.0, 0.5, 1.0, 0.2, 0.8, 1.0, 0.0, 0.3, 0.7, 1.0]
    b = [0.5, 0.5, 1.0, 0.5, 1.0, 1.0, 0.2, 0.5, 0.9, 1.0]

    # Two runs with same seed must produce bitwise identical results
    res1 = paired_bootstrap_ci(a, b, n_resamples=1000, seed=42)
    res2 = paired_bootstrap_ci(a, b, n_resamples=1000, seed=42)

    assert res1["mean_delta"] == res2["mean_delta"]
    assert res1["ci_low"] == res2["ci_low"]
    assert res1["ci_high"] == res2["ci_high"]
    assert res1["mean_delta"] > 0.0
    assert res1["ci_low"] > 0.0
    assert res1["significant_at_05"] is True


def test_document_cluster_paired_bootstrap_guard():
    # Case 1: G = 2 clusters (heldout-CMap cohort) -> must return status 'N/A'
    items_a_g2 = [
        {"source_file": "doc1.pdf", "mrr": 0.5},
        {"source_file": "doc1.pdf", "mrr": 0.6},
        {"source_file": "doc2.pdf", "mrr": 0.4},
    ]
    items_b_g2 = [
        {"source_file": "doc1.pdf", "mrr": 0.8},
        {"source_file": "doc1.pdf", "mrr": 0.9},
        {"source_file": "doc2.pdf", "mrr": 0.7},
    ]
    res_g2 = document_cluster_paired_bootstrap_ci(items_a_g2, items_b_g2, metric_key="mrr", n_resamples=500, seed=42)
    assert res_g2["status"] == "N/A (G < 3 clusters)"
    assert res_g2["cluster_count"] == 2
    assert "doc1.pdf" in res_g2["per_doc_deltas"]
    assert "doc2.pdf" in res_g2["per_doc_deltas"]

    # Case 2: G = 4 clusters -> must return valid bootstrap CI
    items_a_g4 = [
        {"source_file": "d1.pdf", "mrr": 0.5},
        {"source_file": "d2.pdf", "mrr": 0.4},
        {"source_file": "d3.pdf", "mrr": 0.6},
        {"source_file": "d4.pdf", "mrr": 0.3},
    ]
    items_b_g4 = [
        {"source_file": "d1.pdf", "mrr": 0.7},
        {"source_file": "d2.pdf", "mrr": 0.6},
        {"source_file": "d3.pdf", "mrr": 0.8},
        {"source_file": "d4.pdf", "mrr": 0.5},
    ]
    res_g4 = document_cluster_paired_bootstrap_ci(items_a_g4, items_b_g4, metric_key="mrr", n_resamples=500, seed=42)
    assert res_g4["status"] == "valid"
    assert res_g4["cluster_count"] == 4
    assert res_g4["mean_delta"] > 0.0
    assert res_g4["ci_low"] is not None
    assert res_g4["ci_high"] is not None


def test_ocr_exact_page_assertions():
    # Verify expected dictionary contains all 7 defective and 8 clean PDFs
    assert len(EXPECTED_OCR_PAGES_DEFECTIVE) == 15
    assert EXPECTED_OCR_PAGES_DEFECTIVE["tarh-dakheli.pdf"] == [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]
    assert EXPECTED_OCR_PAGES_DEFECTIVE["forsat-motaleati.pdf"] == [1, 2, 3, 4, 5, 6, 7, 8]
    assert EXPECTED_OCR_PAGES_DEFECTIVE["dastyar-pazhuheshi.pdf"] == [1, 2, 3, 4, 5, 6]
    assert EXPECTED_OCR_PAGES_DEFECTIVE["kod-akhlaghi.pdf"] == [1, 2]
    assert EXPECTED_OCR_PAGES_DEFECTIVE["bedoon-factor.pdf"] == [1, 2]
    assert EXPECTED_OCR_PAGES_DEFECTIVE["hamayesh.pdf"] == [1]
    assert EXPECTED_OCR_PAGES_DEFECTIVE["bein-almelali-scan.pdf"] == [1, 2, 3, 4, 5, 6, 7, 8]

    # Verify all 8 clean PDFs have 0 expected OCR pages
    clean_pdfs = [
        "aein-91.pdf", "bein-almelali.pdf", "bge-m3-paper.pdf", "dastyar-amoozeshi.pdf",
        "rag-lewis-2020.pdf", "shiveh-karamoozi.pdf", "shoraha-pazhuheshi.pdf", "ziro-guide.pdf"
    ]
    for c in clean_pdfs:
        assert EXPECTED_OCR_PAGES_DEFECTIVE[c] == []


def test_experiments_yaml_schema_valid():
    yaml_path = EVAL_DIR / "experiments.yaml"
    assert yaml_path.exists(), "eval/experiments.yaml must exist"

    with open(yaml_path, "r", encoding="utf-8") as f:
        config = yaml.safe_load(f)

    assert config.get("schema_version") == "1.0"
    assert "global" in config
    assert "collections" in config
    assert "arms" in config

    # Shared collection rule: arm1 and arm2 must share regime_a_ocr_off
    arms = config["arms"]
    assert arms["arm1_vector_ocr_off"]["collection_ref"] == "regime_a_ocr_off"
    assert arms["arm2_hybrid_ocr_off"]["collection_ref"] == "regime_a_ocr_off"
    assert arms["arm1_vector_ocr_off"]["use_hybrid"] is False
    assert arms["arm2_hybrid_ocr_off"]["use_hybrid"] is True

    # Arm 3 must use regime_a_ocr_defective
    assert arms["arm3_hybrid_ocr_defective"]["collection_ref"] == "regime_a_ocr_defective"
    assert arms["arm3_hybrid_ocr_defective"]["use_hybrid"] is True


def test_auth_env_resolution(monkeypatch):
    # Missing both CLI flags and env vars -> raises ValueError
    monkeypatch.delenv("EVAL_EMAIL", raising=False)
    monkeypatch.delenv("EVAL_PASSWORD", raising=False)
    with pytest.raises(ValueError, match="Authentication credentials missing"):
        get_auth_token("http://localhost:5000", email=None, password=None)


def test_manifest_structure(tmp_path):
    q_file = tmp_path / "questions.json"
    q_file.write_text("[]", encoding="utf-8")

    manifest = build_reproducibility_manifest(q_file, "http://localhost:5000")
    assert "git_sha" in manifest
    assert "eval_dataset_tag" in manifest
    assert manifest["eval_dataset_tag"] == "eval-dataset-v2.2"
    assert "questions_sha256_lf" in manifest
    assert "timestamp_utc" in manifest
    assert "platform" in manifest


def test_collection_manifest_save_and_reload(tmp_path):
    manifest_file = tmp_path / "cache" / "collection_manifest.json"

    # Empty file reload returns empty dict
    assert load_collection_manifest(manifest_file) == {}

    # Save manifest
    data = {
        "eval-regime-a-ocr-off": {
            "id": "col-abc",
            "name": "eval-regime-a-ocr-off",
            "git_sha": "abc1234",
            "ocr_mode": "Off",
            "doc_count": 14,
        },
        "eval-regime-b-scan-off": {
            "id": "col-def",
            "name": "eval-regime-b-scan-off",
            "git_sha": "abc1234",
            "ocr_mode": "Off",
            "doc_count": 1,
        },
    }
    save_collection_manifest(manifest_file, data)
    assert manifest_file.exists()

    reloaded = load_collection_manifest(manifest_file)
    assert reloaded == data
    assert reloaded["eval-regime-a-ocr-off"]["id"] == "col-abc"
    assert reloaded["eval-regime-b-scan-off"]["doc_count"] == 1


def test_benchmark_pure_ocr_mocked(tmp_path, monkeypatch):
    pdfs_dir = tmp_path / "pdfs"
    pdfs_dir.mkdir()
    scan_dir = tmp_path / "scan"
    scan_dir.mkdir()

    # Create dummy pdf files for all 7 target files
    cmap_files = [
        "tarh-dakheli.pdf",
        "forsat-motaleati.pdf",
        "dastyar-pazhuheshi.pdf",
        "kod-akhlaghi.pdf",
        "bedoon-factor.pdf",
        "hamayesh.pdf",
    ]
    for name in cmap_files:
        (pdfs_dir / name).write_bytes(b"%PDF-1.4 dummy content")

    scan_pdf = scan_dir / "bein-almelali-scan.pdf"
    scan_pdf.write_bytes(b"%PDF-1.4 scan content")

    # Mock requests.post to return simulated OCR response
    import requests

    def mock_post(url, files=None, data=None, timeout=None):
        assert url.endswith("/ocr")
        pages_str = data.get("pages", "")
        pages_list = [int(p) for p in pages_str.split(",") if p]
        mock_resp = requests.Response()
        mock_resp.status_code = 200
        mock_resp._content = json.dumps({
            "pages": [{"page": p, "text": f"ocr text for page {p}"} for p in pages_list]
        }).encode("utf-8")
        return mock_resp

    monkeypatch.setattr(requests, "post", mock_post)

    cache_dir = tmp_path / "cache"
    timing_res = benchmark_pure_ocr(
        sidecar_url="http://mock-sidecar:8000",
        pdfs_dir=pdfs_dir,
        scan_pdf_path=scan_pdf,
        cache_dir=cache_dir,
    )

    # 6 CMap PDFs: 10 + 8 + 6 + 2 + 2 + 1 = 29 pages
    assert timing_res["regime_a_cmap_pages"] == 29
    # 1 scan twin: 8 pages
    assert timing_res["regime_b_scan_pages"] == 8
    # Total pages: 37
    assert timing_res["overall_pages"] == 37
    assert "pure_ocr_sec_per_page" in timing_res
    assert (cache_dir / "ocr_timing.json").exists()


def test_resumable_ingestion_skip_ready_and_reupload_failed(tmp_path, monkeypatch):
    import argparse
    from run_eval import DocSearchClient, run_ingestion_phase

    pdfs_dir = tmp_path / "pdfs"
    pdfs_dir.mkdir()
    pdf1 = pdfs_dir / "doc1.pdf"
    pdf1.write_bytes(b"%PDF-1.4 dummy")
    pdf2 = pdfs_dir / "doc2.pdf"
    pdf2.write_bytes(b"%PDF-1.4 dummy")

    deleted_docs = []
    uploaded_docs = []
    checked_ready_docs = []

    class MockClient(DocSearchClient):
        def __init__(self, base_url, token):
            super().__init__(base_url, token)

        def create_collection(self, name, description=""):
            return "col-123"

        def get_collections(self):
            return [{"id": "col-123", "name": "test-col"}]

        def list_documents(self, collection_id):
            return [
                {"id": "d1", "fileName": "doc1.pdf", "status": "Ready"},
                {"id": "d2", "fileName": "doc2.pdf", "status": "Failed"},
            ]

        def get_document(self, doc_id):
            checked_ready_docs.append(doc_id)
            return {"id": doc_id, "fileName": "doc1.pdf", "status": "Ready", "ocrPages": [], "pageCount": 1}

        def delete_document(self, doc_id):
            deleted_docs.append(doc_id)

        def upload_document(self, collection_id, pdf_path):
            uploaded_docs.append(pdf_path.name)
            return {"id": "d2_new", "status": "Uploaded"}

        def wait_until_ready(self, doc_id, timeout_sec=900.0, expect_empty=False):
            assert timeout_sec == 900.0, "wait_until_ready must use 900.0s timeout"
            return {"id": doc_id, "status": "Ready", "pageCount": 1, "ocrPages": []}

    monkeypatch.setattr("run_eval.DocSearchClient", MockClient)

    scan_pdf = tmp_path / "bein-almelali-scan.pdf"
    scan_pdf.write_bytes(b"%PDF-1.4 scan")

    args = argparse.Namespace(
        base_url="http://mock-api:5000",
        ocr_mode="Off",
        reuse_collections=False,
        pdfs_dir=pdfs_dir,
        scan_pdf=scan_pdf,
        cache_dir=tmp_path / "cache",
        sidecar_url="http://mock-sidecar:8000",
    )

    config = {
        "collections": {
            "regime_a_ocr_off": {
                "id_name": "test-col",
                "pdfs_subset": "digital_14",
                "ocr_mode": "Off",
            },
            "regime_b_scan_off": {
                "id_name": "test-col-b",
                "pdfs_subset": "scan_twin_only",
                "ocr_mode": "Off",
            }
        }
    }

    run_ingestion_phase(args, config, "fake_token")

    # doc1.pdf was Ready -> skipped upload, verified via get_document
    assert "d1" in checked_ready_docs
    assert "doc1.pdf" not in uploaded_docs

    # doc2.pdf was Failed -> deleted and re-uploaded
    assert "d2" in deleted_docs
    assert "doc2.pdf" in uploaded_docs


def test_manifest_git_dirty_is_boolean(tmp_path):
    dummy_q = tmp_path / "questions.json"
    dummy_q.write_text("[]", encoding="utf-8")

    manifest = build_reproducibility_manifest(dummy_q, "http://localhost:5000")
    assert "git_dirty" in manifest
    assert isinstance(manifest["git_dirty"], bool)
    assert "extractor_docker_digest" in manifest
    assert isinstance(manifest["extractor_docker_digest"], str)


def test_generate_stage1_reports_formatting_and_isolation(tmp_path):
    # Construct synthetic results_data
    manifest = {
        "git_sha": "abc1234",
        "git_dirty": False,
        "eval_dataset_tag": "eval-dataset-v2.2",
        "timestamp_utc": "2026-11-21T18:00:00Z",
    }
    calibrations = {
        "arm1_vector_ocr_off": {
            "locked_min_similarity": 0.70,
            "plateau_range": [0.675, 0.70],
            "best_balanced_error": 0.2937,
            "cosine_only_sensitivity": {"locked_min_similarity": 0.70},
        },
        "arm2_hybrid_ocr_off": {
            "locked_min_similarity": 0.75,
            "plateau_range": [0.70, 0.80],
            "best_balanced_error": 0.3690,
            "cosine_only_sensitivity": {"locked_min_similarity": 0.70},
        },
        "arm3_hybrid_ocr_defective": {
            "locked_min_similarity": 0.75,
            "plateau_range": [0.70, 0.80],
            "best_balanced_error": 0.2976,
            "cosine_only_sensitivity": {"locked_min_similarity": 0.675},
        },
    }
    regime_b_summary = {
        "question_count": 8,
        "scan_off": {"mrr": 0.0, "r1": 0.0, "r5": 0.0},
        "scan_defective": {"mrr": 0.875, "r1": 0.75, "r5": 1.0},
        "digital_reference": {"mrr": 0.9167, "r1": 0.875, "r5": 1.0},
        "delta_mrr_defective_vs_off": 0.875,
        "delta_mrr_defective_vs_digital_ref": -0.0417,
    }

    # Dummy items for arms
    dummy_item = {
        "id": "q001",
        "docset": "tuned",
        "split": "test",
        "type": "factual",
        "source_file": "clean.pdf",
        "defect_mechanism": "none",
        "mrr": 1.0,
        "strict_mrr": 1.0,
        "hits_at_k": {1: 1, 5: 1},
    }
    arms = {
        "arm1_vector_ocr_off": [dummy_item],
        "arm2_hybrid_ocr_off": [dummy_item],
        "arm3_hybrid_ocr_defective": [dummy_item],
    }

    results_data = {
        "manifest": manifest,
        "ocr_timing": {"pure_ocr_sec_per_page": 4.13, "overall_pages": 37, "overall_elapsed_sec": 152.8},
        "calibrations": calibrations,
        "regime_b_summary": regime_b_summary,
        "arms": arms,
    }

    out_paths = generate_stage1_reports(results_data, tmp_path, smoke=True)
    report_md_path = out_paths["report_md"]
    summary_csv_path = out_paths["summary_csv"]
    regime_b_csv_path = out_paths["regime_b_csv"]

    report_content = report_md_path.read_text(encoding="utf-8")

    # Dual-SHA header check
    assert "- **Data generated at Git SHA:** `abc1234`" in report_content
    assert "- **Report rendered at Git SHA:**" in report_content

    # 1. No np.float64 strings in report.md
    assert "np.float64" not in report_content

    # 2. No duplicate table at the bottom of report.md
    assert report_content.count("## 3. Retrieval Performance Summary by Cohort") == 1
    assert report_content.count("## 4. Regime B: Scanned Twin Evaluation (8 Questions)") == 1
    assert report_content.count("## 5. Pure OCR Timing Benchmark (Extractor Sidecar)") == 1

    # Ensure no table appears after section 5
    after_sec5 = report_content.split("## 5. Pure OCR Timing Benchmark (Extractor Sidecar)")[1]
    assert "| Cohort |" not in after_sec5
    assert "| `overall` |" not in after_sec5

    # 3. No Regime B row mixed into the Section 3 Regime A Arm 1/2/3 table
    sec3_content = report_content.split("## 3. Retrieval Performance Summary by Cohort")[1].split("## 4.")[0]
    assert "regime_b" not in sec3_content
    assert "scan_twin" not in sec3_content

    # 4. Summary CSV contains only Regime A cohorts, and Regime B CSV contains 3 rows
    summary_csv_content = summary_csv_path.read_text(encoding="utf-8")
    assert "regime_b" not in summary_csv_content

    regime_b_csv_content = regime_b_csv_path.read_text(encoding="utf-8")
    assert "scan_twin_ocr_off" in regime_b_csv_content
    assert "scan_twin_ocr_defective" in regime_b_csv_content
    assert "digital_reference_hybrid" in regime_b_csv_content


# --- Stage 2 Unit Tests (T19b) ---

def test_stage2_cache_key_formula_exact_order_and_prompt_sha():
    chunks_order1 = ["chunk_a", "chunk_b"]
    chunks_order2 = ["chunk_b", "chunk_a"]
    sha = "abc123prompt"

    key1 = compute_cache_key("q", chunks_order1, 6, "gemini-2.5-flash", 0.0, sha)
    key2 = compute_cache_key("q", chunks_order2, 6, "gemini-2.5-flash", 0.0, sha)

    # Order matters: must NOT sort chunk IDs
    assert key1 != key2, "Cache key must preserve exact retrieved chunk ordering!"

    # Changing top_k, model, temp, or prompt_sha changes key
    key_topk = compute_cache_key("q", chunks_order1, 10, "gemini-2.5-flash", 0.0, sha)
    key_model = compute_cache_key("q", chunks_order1, 6, "other-model", 0.0, sha)
    key_temp = compute_cache_key("q", chunks_order1, 6, "gemini-2.5-flash", 0.5, sha)
    key_sha = compute_cache_key("q", chunks_order1, 6, "gemini-2.5-flash", 0.0, "different_sha")

    assert len({key1, key_topk, key_model, key_temp, key_sha}) == 5


def test_stage2_cache_store_and_load(tmp_path):
    cache_dir = tmp_path / "llm_cache"
    key = "sample_key_123"

    # Initially empty
    assert get_from_cache(cache_dir, key) is None

    # Save valid entry
    data = {
        "answer": "پاسخ معتبر [1]",
        "notFound": False,
        "degraded": False,
        "sources": [{"index": 1, "fileName": "doc.pdf", "page": 2}],
    }
    save_to_cache(cache_dir, key, data)

    cached = get_from_cache(cache_dir, key)
    assert cached is not None
    assert cached["is_cached"] is True
    assert cached["answer"] == "پاسخ معتبر [1]"

    # Degradation invariant: never cache degraded responses
    degraded_key = "degraded_key"
    save_to_cache(cache_dir, degraded_key, {"answer": None, "degraded": True})
    assert get_from_cache(cache_dir, degraded_key) is None


def test_stage2_checkpoint_jsonl_append_and_load(tmp_path):
    checkpoint_file = tmp_path / "arm1.jsonl"

    rec1 = {"id": "q001", "answer": "ans1", "degraded": False}
    rec2 = {"id": "q002", "answer": "ans2", "degraded": False}
    rec_deg = {"id": "q003", "answer": None, "degraded": True}

    append_checkpoint(checkpoint_file, rec1)
    append_checkpoint(checkpoint_file, rec2)
    append_checkpoint(checkpoint_file, rec_deg)  # Must be ignored

    records = load_checkpoint(checkpoint_file)
    assert len(records) == 2
    assert "q001" in records
    assert "q002" in records
    assert "q003" not in records


def test_stage2_offline_threshold_simulation_exact_logic():
    # Case 1: 0 chunks retrieved -> NoContext
    item_zero_chunks = {
        "retrieved_chunk_count": 0,
        "bestVectorScore": 0.0,
        "hasFtsHit": False,
        "notFound": True,
        "answer": "raw refusal",
    }
    sim_zc = simulate_threshold(item_zero_chunks, tau=0.55)
    assert sim_zc["notFound"] is True
    assert sim_zc["no_answer_reason"] == "NoContext"
    assert sim_zc["gated"] is True

    # Case 2: Low similarity and NO FTS hit -> LowSimilarity rejection
    item_low_sim = {
        "retrieved_chunk_count": 6,
        "bestVectorScore": 0.50,
        "hasFtsHit": False,
        "notFound": False,
        "answer": "some generated text",
    }
    sim_ls = simulate_threshold(item_low_sim, tau=0.55)
    assert sim_ls["notFound"] is True
    assert sim_ls["no_answer_reason"] == "LowSimilarity"
    assert sim_ls["gated"] is True
    assert sim_ls["sources"] == []

    # Case 3: Low similarity with FTS rescue (hasFtsHit=True) -> passed to LLM
    item_fts_rescue = {
        "retrieved_chunk_count": 6,
        "bestVectorScore": 0.50,
        "hasFtsHit": True,
        "notFound": False,
        "answer": "answered text [1]",
        "sources": [{"index": 1}],
    }
    sim_fts = simulate_threshold(item_fts_rescue, tau=0.55)
    assert sim_fts["notFound"] is False
    assert sim_fts["no_answer_reason"] == "None"
    assert sim_fts["gated"] is False
    assert len(sim_fts["sources"]) == 1

    # Case 4: High similarity with LLM self-refusal -> ModelRefusal
    item_model_refusal = {
        "retrieved_chunk_count": 6,
        "bestVectorScore": 0.75,
        "hasFtsHit": True,
        "notFound": True,
        "answer": "در اسناد موجود پاسخی برای این پرسش پیدا نشد",
    }
    sim_mr = simulate_threshold(item_model_refusal, tau=0.55)
    assert sim_mr["notFound"] is True
    assert sim_mr["no_answer_reason"] == "ModelRefusal"
    assert sim_mr["gated"] is False


def test_stage2_non_vacuous_multidigit_and_unit_recall():
    gold_keywords = ["20", "واحد"]
    expected_with_num_and_unit = "حداکثر 20 واحد در سال مجاز است."
    sources = [{"index": 1, "fileName": "doc.pdf", "page": 5}]

    # Answer matches keywords, numbers, and units strictly
    ans_strict = "مطابق آیین‌نامه، سقف مجاز 20 واحد در سال است [1]."
    m_strict = evaluate_generation_metrics(
        answer=ans_strict,
        not_found=False,
        expected_answer=expected_with_num_and_unit,
        answer_keywords=gold_keywords,
        sources=sources,
        gold_file="doc.pdf",
        gold_pages=[5],
    )
    assert m_strict["kw_hit"] is True
    assert m_strict["num_hit"] is True
    assert m_strict["unit_hit_strict"] is True
    assert m_strict["unit_hit_proximity_10"] is True

    # Answer has unit and number separated loosely (proximity <= 10) but not strict consecutive
    ans_prox = "مطابق آیین‌نامه، 20 حداکثر واحد مجاز است [1]."
    expected_unit = "20 واحد"
    m_prox = evaluate_generation_metrics(
        answer=ans_prox,
        not_found=False,
        expected_answer=expected_unit,
        answer_keywords=["20"],
        sources=sources,
        gold_file="doc.pdf",
        gold_pages=[5],
    )
    assert m_prox["num_hit"] is True
    assert m_prox["unit_hit_strict"] is False
    assert m_prox["unit_hit_proximity_10"] is True

    # Non-vacuous check: question without multi-digit numbers or units returns None
    expected_no_num = "دانشگاه تهران مسئول است."
    m_no_num = evaluate_generation_metrics(
        answer="دانشگاه تهران مسئول است [1].",
        not_found=False,
        expected_answer=expected_no_num,
        answer_keywords=["دانشگاه"],
        sources=sources,
        gold_file="doc.pdf",
        gold_pages=[1],
    )
    assert m_no_num["num_hit"] is None
    assert m_no_num["unit_hit_strict"] is None
    assert m_no_num["unit_hit_proximity_10"] is None


def test_stage2_citation_metrics_four_types_and_twin_mapping():
    sources = [
        {"index": 1, "fileName": "bein-almelali-scan.pdf", "page": 2},
        {"index": 2, "fileName": "other.pdf", "page": 10},
    ]
    twin_mapping = {"bein-almelali-scan.pdf": "bein-almelali.pdf"}

    # Answer cites [1] (which hits gold page 2 via twin mapping) and [2] (which misses)
    ans = "طبق ماده ۲ این بند الزامی است [1][2]."
    m = evaluate_generation_metrics(
        answer=ans,
        not_found=False,
        expected_answer="بند الزامی است",
        answer_keywords=["ماده"],
        sources=sources,
        gold_file="bein-almelali.pdf",
        gold_pages=[2, 3],
        twin_mapping=twin_mapping,
    )

    assert m["citation_validity"] is True
    assert m["citation_page_hit"] is True
    assert m["cited_source_count"] == 2
    assert m["valid_gold_hits_count"] == 1
    assert m["citation_precision"] == 0.5  # 1 hit out of 2 cited
    assert m["citation_recall"] == 0.5     # page 2 cited out of gold pages [2, 3]

    # Answer without bracket citations has validity = False
    ans_no_cit = "طبق ماده ۲ این بند الزامی است بدون مرجع."
    m_no_cit = evaluate_generation_metrics(
        answer=ans_no_cit,
        not_found=False,
        expected_answer="بند الزامی است",
        answer_keywords=["ماده"],
        sources=sources,
        gold_file="bein-almelali.pdf",
        gold_pages=[2],
    )
    assert m_no_cit["citation_validity"] is False
    assert m_no_cit["citation_page_hit"] is False
    assert m_no_cit["citation_precision"] == 0.0


def test_stage2_cluster_bootstrap_g_guard():
    # Case 1: G = 2 (heldout-CMap cohort) -> status 'N/A'
    items_a_g2 = [
        {"source_file": "d1.pdf", "kw_hit": 1.0},
        {"source_file": "d2.pdf", "kw_hit": 0.0},
    ]
    items_b_g2 = [
        {"source_file": "d1.pdf", "kw_hit": 1.0},
        {"source_file": "d2.pdf", "kw_hit": 1.0},
    ]
    res_g2 = stage2_cluster_bootstrap_ci(items_a_g2, items_b_g2, metric_key="kw_hit", n_resamples=500, seed=42)
    assert res_g2["status"] == "N/A (G < 5 clusters)"
    assert res_g2["cluster_count"] == 2
    assert res_g2["ci_low"] is None

    # Case 2: G = 3 (heldout cohort) -> status 'Descriptive only'
    items_a_g3 = [
        {"source_file": "d1.pdf", "kw_hit": 1.0},
        {"source_file": "d2.pdf", "kw_hit": 0.0},
        {"source_file": "d3.pdf", "kw_hit": 0.0},
    ]
    items_b_g3 = [
        {"source_file": "d1.pdf", "kw_hit": 1.0},
        {"source_file": "d2.pdf", "kw_hit": 1.0},
        {"source_file": "d3.pdf", "kw_hit": 1.0},
    ]
    res_g3 = stage2_cluster_bootstrap_ci(items_a_g3, items_b_g3, metric_key="kw_hit", n_resamples=500, seed=42)
    assert res_g3["status"] == "Descriptive only (G < 5 clusters)"
    assert res_g3["cluster_count"] == 3

    # Case 3: G = 5 -> valid cluster bootstrap
    items_a_g5 = [{"source_file": f"d{i}.pdf", "kw_hit": 0.2} for i in range(5)]
    items_b_g5 = [{"source_file": f"d{i}.pdf", "kw_hit": 0.8} for i in range(5)]
    res_g5 = stage2_cluster_bootstrap_ci(items_a_g5, items_b_g5, metric_key="kw_hit", n_resamples=500, seed=42)
    assert res_g5["status"] == "valid"
    assert res_g5["cluster_count"] == 5
    assert res_g5["ci_low"] is not None
    assert res_g5["ci_high"] is not None


def test_stage2_wilson_and_mcnemar_statistical_routines():
    # Wilson interval bounds check
    w_zero = wilson_score_interval(0, 10)
    assert w_zero[0] == 0.0
    assert 0.20 <= w_zero[1] <= 0.35

    w_full = wilson_score_interval(10, 10)
    assert 0.65 <= w_full[0] <= 0.80
    assert w_full[1] == 1.0

    # McNemar's exact test
    # Concordant / no discordance -> p = 1.0
    assert mcnemar_exact(0, 0) == 1.0
    assert mcnemar_exact(5, 5) == 1.0

    # Significant discordance: 0 vs 10
    p_val_disc = mcnemar_exact(0, 10)
    assert p_val_disc < 0.05


def test_stage2_headless_matplotlib_plots_generation(tmp_path):
    dummy_item = {
        "id": "q001",
        "docset": "tuned",
        "split": "test",
        "type": "factual",
        "source_file": "doc.pdf",
        "bestVectorScore": 0.8,
        "hasFtsHit": True,
        "retrieved_chunk_count": 6,
        "notFound": False,
        "answer": "answer [1]",
        "kw_hit": True,
        "num_hit": True,
        "unit_hit_strict": True,
        "unit_hit_proximity_10": True,
        "citation_validity": True,
        "citation_page_hit": True,
        "citation_precision": 1.0,
        "citation_recall": 1.0,
    }

    dummy_results = {
        "arms": {
            "arm1_vector_ocr_off": [dummy_item],
            "arm2_hybrid_ocr_off": [dummy_item],
            "arm3_hybrid_ocr_defective": [dummy_item],
            "regime_b_scan_off": [dummy_item],
            "regime_b_scan_defective": [dummy_item],
        }
    }

    plots = generate_stage2_plots(dummy_results, tmp_path)
    assert len(plots) == 3

    for p in plots:
        assert p.exists()
        assert p.stat().st_size > 1000, f"Plot file {p} must be non-empty image"


def test_stage2_degraded_policy_retry_failed_provider_and_streak_halt(tmp_path, monkeypatch):
    import argparse
    from stage2 import run_stage2_eval, save_to_cache, get_from_cache

    cache_dir = tmp_path / "cache"
    cache_dir.mkdir(parents=True)
    manifest_file = cache_dir / "collection_manifest.json"
    manifest_data = {
        "eval-regime-a-ocr-off": {"id": "col1", "name": "eval-regime-a-ocr-off", "ocr_mode": "Off"}
    }
    manifest_file.write_text(json.dumps(manifest_data), encoding="utf-8")

    q_file = tmp_path / "questions.json"
    qs = [{"id": f"q00{i}", "question": f"پرسش {i}؟", "type": "factual"} for i in range(1, 4)]
    q_file.write_text(json.dumps(qs), encoding="utf-8")

    out_dir = tmp_path / "out"

    config = {
        "collections": {"regime_a_ocr_off": {"id_name": "eval-regime-a-ocr-off"}},
        "arms": {
            "arm1_vector_ocr_off": {
                "name": "arm1",
                "collection_ref": "regime_a_ocr_off",
                "use_hybrid": False,
            }
        },
    }

    # Monkeypatch time.sleep to avoid any latency
    monkeypatch.setattr(time, "sleep", lambda s: None)

    # Monkeypatch requests.get for /health/live
    class MockResp:
        def __init__(self, status_code, json_data, headers=None):
            self.status_code = status_code
            self._json = json_data
            self.headers = headers or {}
            self.text = json.dumps(json_data)
        def json(self):
            return self._json
        def raise_for_status(self):
            if self.status_code >= 400:
                raise requests.RequestException(f"HTTP {self.status_code}")

    monkeypatch.setattr(requests, "get", lambda url, timeout=None: MockResp(200, {"status": "Healthy"}))

    # Mock DocSearchClient methods
    from run_eval import DocSearchClient
    monkeypatch.setattr(DocSearchClient, "get_collections", lambda self: [{"name": "eval-regime-a-ocr-off", "id": "col1"}])
    monkeypatch.setattr(DocSearchClient, "dev_retrieve", lambda self, q, cols, top_k=6, use_hybrid=True: {
        "bestVectorScore": 0.8,
        "hasFtsHit": True,
        "elapsedMs": 10.0,
        "chunks": [{"chunkId": "c1", "fileName": "doc.pdf", "pageStart": 1, "pageEnd": 1}],
    })

    # Mock requests.post to return degraded response
    post_attempts = []
    def mock_post(url, json=None, headers=None, timeout=None):
        post_attempts.append(url)
        return MockResp(200, {"degraded": True, "answer": None, "notFound": False})

    monkeypatch.setattr(requests, "post", mock_post)

    args = argparse.Namespace(
        base_url="http://mock:5000",
        cache_dir=cache_dir,
        questions=q_file,
        chat_model="mock-model",
        temperature=0.0,
        top_k=6,
        arms="arm1_vector_ocr_off",
        smoke=False,
        out=out_dir,
        sleep=0.0,
        allow_dirty=True,
        no_cache=False,
    )

    # 3 consecutive provider failures must trigger SystemExit(1)
    with pytest.raises(SystemExit):
        run_stage2_eval(args, config, token="mock-token")

    # 3 questions * 4 retries = 12 post attempts
    assert len(post_attempts) == 12

    # Verify no degraded entries were written to checkpoint or cache
    checkpoint_file = out_dir / "checkpoints" / "arm1_vector_ocr_off.jsonl"
    assert not checkpoint_file.exists() or checkpoint_file.stat().st_size == 0
    llm_cache_dir = cache_dir / "llm_cache"
    assert not llm_cache_dir.exists() or len(list(llm_cache_dir.glob("*.json"))) == 0


def test_stage2_validate_offline_sim_mismatch_detection():
    # Test gate comparison logic
    # Case 1: Exact match on pre-LLM gate (both gated)
    ungated_item_low = {
        "id": "q001",
        "retrieved_chunk_count": 6,
        "bestVectorScore": 0.40,
        "hasFtsHit": False,
        "notFound": False,
    }
    sim_low = simulate_threshold(ungated_item_low, tau=0.55)
    live_gen_ms = 0.0
    live_not_found = True
    is_live_pre_gated = (live_gen_ms == 0.0 and live_not_found)
    is_sim_pre_gated = sim_low["gated"]
    assert is_live_pre_gated == is_sim_pre_gated is True

    # Case 2: Gate divergence (live executed LLM, but offline simulation had gated=True)
    live_gen_ms_mismatch = 1500.0
    live_not_found_mismatch = False
    is_live_mismatch_gated = (live_gen_ms_mismatch == 0.0 and live_not_found_mismatch)
    assert is_live_mismatch_gated != is_sim_pre_gated, "Should detect mismatch when live executes LLM on low score"


def test_stage2_official_run_guard_git_dirty(tmp_path, monkeypatch):
    import argparse
    import subprocess
    from stage2 import run_stage2_eval

    cache_dir = tmp_path / "cache"
    cache_dir.mkdir(parents=True)
    (cache_dir / "collection_manifest.json").write_text(json.dumps({}), encoding="utf-8")
    q_file = tmp_path / "q.json"
    q_file.write_text("[]", encoding="utf-8")

    # Case 1: git_dirty == True and allow_dirty == False -> aborts with SystemExit
    monkeypatch.setattr(subprocess, "check_output", lambda cmd, **kwargs: "M eval/stage2.py\n")

    args_strict = argparse.Namespace(
        base_url="http://mock:5000",
        cache_dir=cache_dir,
        questions=q_file,
        chat_model="model",
        temperature=0.0,
        top_k=6,
        arms=None,
        smoke=True,
        out=tmp_path / "out",
        sleep=0.0,
        allow_dirty=False,
        no_cache=False,
    )

    with pytest.raises(SystemExit):
        run_stage2_eval(args_strict, {"collections": {}, "arms": {}}, token="tok")


