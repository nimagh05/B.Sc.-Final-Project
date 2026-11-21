"""CI-safe fast unit tests for DocSearch Evaluation Harness (Task T19a).

Runs in < 2 seconds with zero network, zero Ollama, zero Docker, and zero real PDFs.
"""

import json
import os
import sys
from pathlib import Path
import pytest
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
    get_auth_token,
    is_chunk_hit,
    load_collection_manifest,
    paired_bootstrap_ci,
    save_collection_manifest,
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
