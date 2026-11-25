#!/usr/bin/env python3
"""audit_docs_numbers.py — Reproducible verification script for docs/EVAL-RESULTS.md.

Programmatically computes ground-truth numbers from JSON/CSV evaluation artifacts and
asserts exact presence, row-scoped table consistency, and anti-fabrication / strict-hygiene
rules in docs/EVAL-RESULTS.md.

Usage:
    python eval/audit_docs_numbers.py [--doc docs/EVAL-RESULTS.md]

Exit code:
    0: 100% checks passed cleanly.
    1: One or more checks failed.
"""

import argparse
import csv
import json
import math
import re
import sys
from pathlib import Path
from statistics import NormalDist
from typing import Any, Dict, List, Optional, Tuple

import numpy as np

REPO_ROOT = Path(__file__).resolve().parent.parent
EVAL_DIR = Path(__file__).resolve().parent
if str(EVAL_DIR) not in sys.path:
    sys.path.insert(0, str(EVAL_DIR))

from stage2 import (  # noqa: E402
    HELDOUT_CMAP_FILES,
    PRIMARY_CMAP_FILES,
    mcnemar_exact,
    simulate_threshold,
)


def wilson_interval_exact(successes: int, total: int, confidence: float = 0.95) -> Tuple[float, float]:
    """Computes full-precision Wilson score CI using exact normal quantile (avoids 4-decimal double-rounding)."""
    if total <= 0:
        return (0.0, 0.0)
    z = NormalDist().inv_cdf(0.5 + confidence / 2.0)  # 1.959963984540054 for 95%
    p = successes / total
    z2 = z * z
    denom = 1.0 + z2 / total
    center = (p + z2 / (2.0 * total)) / denom
    margin = (z * math.sqrt((p * (1.0 - p) + z2 / (4.0 * total)) / total)) / denom
    return (max(0.0, center - margin), min(1.0, center + margin))

POSIX_ARCHIVE_SHA256 = "34b64841e785d72842f3860ea64d81eea54f26a4d1d4cc097f048f1d680b2310"

FORBIDDEN_STRINGS = [
    "farsiparsed",
    "Gemini 2.5",
    "+200%",
    "three-quarters",
    "universal gate",
    "0/78",
    "40.0% (4/10)",
    "4 / 10",
    "socket resets",
    "HTTP 503",
    "streaming generation",
    "< 35 ms",
    "146 questions",
    "N=146",
    "RRF Parameter Stability Sweep",
    "48 months",
    "U+FFFD",
    "IEEE",
    "Procedural / Legal",
    "close to chance",
    "near-domain",
    "eliminating dangerous",
    "Paired Wilcoxon",
    "greedy decoding",
    "introducing no observed degradation",
    "Selective OCR eliminated 6 of the 7 corrupted numeral errors",
    "e8d7e4fc92c6e7e2f3965be9ad1f0e3e325ab030f9d31bd2db667df6209f2b95",
    "Board of Directors",
]


class DocAuditor:
    def __init__(self, doc_path: Path):
        self.doc_path = doc_path
        self.doc_text = doc_path.read_text(encoding="utf-8")
        self.passes: List[str] = []
        self.failures: List[Tuple[str, str]] = []

        # Lazy-loaded by run_all_audits() so CI unit tests run without git-ignored eval/results/
        self.s1_results = None
        self.s2_results = None
        self.s2_summary_csv = ""
        self.questions: List[Dict[str, Any]] = []

    def _load_artifacts(self):
        self.s1_results = json.loads((REPO_ROOT / "eval/results/t19a_stage1/results.json").read_text(encoding="utf-8"))
        self.s2_results = json.loads((REPO_ROOT / "eval/results/t19b_stage2/results.json").read_text(encoding="utf-8"))
        self.s2_summary_csv = (REPO_ROOT / "eval/results/t19b_stage2/summary.csv").read_text(encoding="utf-8")
        self.questions = json.loads((REPO_ROOT / "eval/questions.json").read_text(encoding="utf-8"))

    def check(self, name: str, condition: bool, err_msg: str):
        if condition:
            self.passes.append(name)
            print(f"  [PASS] {name}")
        else:
            self.failures.append((name, err_msg))
            print(f"  [FAIL] {name}: {err_msg}")

    def assert_contains(self, name: str, expected_snippet: str):
        self.check(
            name,
            expected_snippet in self.doc_text,
            f"Snippet not found in document: {expected_snippet!r}",
        )

    def assert_regex(self, name: str, pattern: str):
        self.check(
            name,
            bool(re.search(pattern, self.doc_text)),
            f"Pattern not found in document: {pattern!r}",
        )

    def _section_lines(self, section: Optional[str]) -> List[str]:
        lines = self.doc_text.splitlines()
        if not section:
            return lines
        in_section = False
        section_level = 0
        out: List[str] = []
        for line in lines:
            stripped = line.strip()
            if not in_section:
                if section in line:
                    in_section = True
                    if stripped.startswith("#"):
                        section_level = len(stripped) - len(stripped.lstrip("#"))
                    out.append(line)
            else:
                if stripped.startswith("#"):
                    cur_level = len(stripped) - len(stripped.lstrip("#"))
                    if section_level == 0 or cur_level <= section_level:
                        break
                out.append(line)
        return out

    def assert_row(
        self,
        name: str,
        row_key: str,
        *expected_cells: str,
        section: Optional[str] = None,
    ):
        """Locates markdown table line(s) matching row_key (optionally scoped to section) and asserts expected_cells appear on that row."""
        lines = self._section_lines(section)
        if section and not lines:
            self.check(name, False, f"Section {section!r} not found in document")
            return

        matching_rows = [
            line for line in lines
            if row_key in line and line.strip().startswith("|")
        ]
        if not matching_rows:
            scope_note = f" in section {section!r}" if section else ""
            self.check(name, False, f"Table row with key {row_key!r} not found{scope_note}")
            return

        for row in matching_rows:
            if all(cell in row for cell in expected_cells):
                self.check(name, True, "")
                return

        best_row = max(matching_rows, key=lambda r: sum(1 for c in expected_cells if c in r))
        missing = [c for c in expected_cells if c not in best_row]
        self.check(name, False, f"Row with key {row_key!r} missing cells {missing!r}. Found row: {best_row.strip()!r}")

    def run_all_audits(self) -> bool:
        self._load_artifacts()
        print("\n========================================================")
        print(f"  Auditing {self.doc_path.name} against ground-truth data")
        print("========================================================")

        self._audit_hygiene_and_forbidden_strings()
        self._audit_dataset_structure()
        self._audit_stage1_metrics()
        self._audit_stage2_provenance_and_validation()
        self._audit_primary_endpoints()
        self._audit_vector_roc_auc()
        self._audit_hybrid_vs_vector_and_counterfactual()
        self._audit_null_experiment()
        self._audit_secondary_endpoint_heldout_cmap()
        self._audit_frr_decomposition()
        self._audit_question_types_and_citations()
        self._audit_regime_b_case_study()
        self._audit_latencies()
        self._audit_production_reliability()

        print("\n--------------------------------------------------------")
        print(f"  Audit Summary: {len(self.passes)} PASSED, {len(self.failures)} FAILED")
        print("--------------------------------------------------------")
        return len(self.failures) == 0

    def _audit_hygiene_and_forbidden_strings(self):
        # 1. Zero file:/// links
        self.check(
            "hygiene_zero_file_urls",
            "file:///" not in self.doc_text,
            "Found broken 'file:///' link in document (must use relative paths)!",
        )

        # 2. Relative figures check
        for fig_name in [
            "figures/plot1_stage2_quality_comparison.png",
            "figures/plot2_threshold_tradeoff_curves.png",
            "figures/plot3_ocr_generation_impact.png",
        ]:
            self.assert_contains(f"relative_figure_{fig_name}", fig_name)

        # 3. Forbidden strings check
        for s in FORBIDDEN_STRINGS:
            pattern = re.compile(re.escape(s), re.IGNORECASE)
            self.check(
                f"forbidden_string_{s.replace(' ', '_')}",
                not pattern.search(self.doc_text),
                f"Found forbidden/fabricated string: {s!r}",
            )

        # 4. Zero API keys or secrets
        api_key_match = re.search(r"AIza[0-9A-Za-z_-]{35}", self.doc_text)
        self.check(
            "hygiene_zero_api_keys",
            not api_key_match,
            f"Found potential API key in document: {api_key_match.group(0) if api_key_match else ''}",
        )

    def _audit_dataset_structure(self):
        unique_q_count = len(self.questions)
        ans_q_count = sum(1 for q in self.questions if q.get("type") != "not-in-docs")
        tuned_q = [q for q in self.questions if q.get("docset") == "tuned"]
        heldout_q = [q for q in self.questions if q.get("docset") == "heldout"]
        self.check("dataset_unique_questions_138", unique_q_count == 138, f"Expected 138 questions, found {unique_q_count}")
        self.check("dataset_answerable_112", ans_q_count == 112, f"Expected 112 answerable questions, found {ans_q_count}")
        self.assert_contains("doc_mentions_138_questions", f"{unique_q_count} unique questions across 14 Regime A PDFs")
        self.assert_contains("doc_mentions_74_tuned", f"{len(tuned_q)} questions across 11 documents")
        self.assert_contains("doc_mentions_64_heldout", f"{len(heldout_q)} questions across **3 PDFs**")
        self.assert_contains("doc_mentions_heldout_3_pdfs", "forsat-motaleati.pdf`, `shiveh-karamoozi.pdf`, and `tarh-dakheli.pdf")
        self.assert_contains("doc_mentions_anti_leakage", "ANTI-LEAKAGE VIOLATION")
        self.assert_contains("doc_mentions_bootstrap_resamples", "B = 10,000")

    def _audit_stage1_metrics(self):
        s1_manifest = self.s1_results["manifest"]
        s1_arms = self.s1_results["arms"]

        # Section 1.1 provenance
        self.assert_row("s1_data_git_sha_row", "Data Generation Git SHA", s1_manifest["git_sha"], section="### 1.1 Dual-Stage Provenance Header")
        self.assert_row("s1_working_tree_row", "Git Working Tree Status", "pre-schema / unrecorded", "dirty: True", section="### 1.1 Dual-Stage Provenance Header")
        self.assert_row("s1_run_status_row", "Run Status", "pre-schema / unrecorded", section="### 1.1 Dual-Stage Provenance Header")

        # Table 3.1 MRR computed directly from Stage 1 arms
        cohorts = [
            ("s1_tbl31_overall", "**Overall** ($N=112$)", lambda q: q.get("type") != "not-in-docs"),
            ("s1_tbl31_pe1_heldout", "Primary Endpoint 1 (`heldout_all`, $N=52$)", lambda q: q.get("docset") == "heldout" and q.get("type") != "not-in-docs"),
            ("s1_tbl31_pe2_cmap", "Primary Endpoint 2 (`heldout-CMap`, $N=27$)", lambda q: q.get("source_file") in HELDOUT_CMAP_FILES and q.get("type") != "not-in-docs"),
            ("s1_tbl31_heldout_test", "`heldout_test` ($N=39$)", lambda q: q.get("docset") == "heldout" and q.get("split") == "test" and q.get("type") != "not-in-docs"),
            ("s1_tbl31_tuned_test", "`tuned_test` ($N=42$)", lambda q: q.get("docset") == "tuned" and q.get("split") == "test" and q.get("type") != "not-in-docs"),
            ("s1_tbl31_tuned_dev", "`tuned_dev` ($N=18$)", lambda q: q.get("docset") == "tuned" and q.get("split") == "dev" and q.get("type") != "not-in-docs"),
        ]
        for check_name, row_key, pred in cohorts:
            m1 = np.mean([r["mrr"] for r in s1_arms["arm1_vector_ocr_off"] if pred(r)])
            m2 = np.mean([r["mrr"] for r in s1_arms["arm2_hybrid_ocr_off"] if pred(r)])
            m3 = np.mean([r["mrr"] for r in s1_arms["arm3_hybrid_ocr_defective"] if pred(r)])
            d21 = m2 - m1
            d32 = m3 - m2
            s21 = f"{d21:+.4f}" if check_name != "s1_tbl31_overall" else f"{d21:.4f}"
            s32 = f"{d32:+.4f}" if check_name != "s1_tbl31_overall" else f"{d32:.4f}"
            self.assert_row(
                check_name,
                row_key,
                f"{m1:.4f}",
                f"{m2:.4f}",
                f"{m3:.4f}",
                s21,
                s32,
                section="### 3.1 Primary Endpoints 1 & 2 Outcomes",
            )

        # CMap heterogeneity computed from Stage 1 arms
        for doc_fn, cname in [("forsat-motaleati.pdf", "forsat"), ("tarh-dakheli.pdf", "tarh")]:
            m2_d = np.mean([r["mrr"] for r in s1_arms["arm2_hybrid_ocr_off"] if r.get("source_file") == doc_fn and r.get("type") != "not-in-docs"])
            m3_d = np.mean([r["mrr"] for r in s1_arms["arm3_hybrid_ocr_defective"] if r.get("source_file") == doc_fn and r.get("type") != "not-in-docs"])
            self.assert_contains(f"s1_{cname}_mrr_a2", f"{m2_d:.4f}")
            self.assert_contains(f"s1_{cname}_mrr_a3", f"{m3_d:.4f}")
            self.assert_contains(f"s1_{cname}_delta", f"{m3_d - m2_d:+.4f}")

        # Pure OCR timing computed from manifest ocr_timing
        ocr_t = self.s1_results["ocr_timing"]
        self.assert_contains("s1_pure_ocr_overall", f"{ocr_t['pure_ocr_sec_per_page']:.3f}\\text{{ s/page}}")
        self.assert_contains("s1_pure_ocr_regime_a", f"{ocr_t['regime_a_sec_per_page']:.3f}\\text{{ s/page}}")
        self.assert_contains("s1_pure_ocr_regime_b", f"{ocr_t['regime_b_sec_per_page']:.3f}\\text{{ s/page}}")

        # Stage 1 Regime B retrieval computed from regime_b_summary
        rb_sum = self.s1_results["regime_b_summary"]
        mrr_scan_off = rb_sum["scan_off"]["mrr"]
        mrr_scan_def = rb_sum["scan_defective"]["mrr"]
        mrr_dig_ref = rb_sum["digital_reference"]["mrr"]
        delta_rb = rb_sum["delta_mrr_defective_vs_digital_ref"]
        self.assert_contains("s1_regb_scan_off_mrr", f"{mrr_scan_off:.3f}")
        self.assert_contains("s1_regb_scan_def_mrr", f"{mrr_scan_def:.3f}")
        self.assert_contains("s1_regb_digital_ref_mrr", f"{mrr_dig_ref:.4f}")
        self.assert_contains("s1_regb_delta", f"{delta_rb:+.4f}")

    def _audit_stage2_provenance_and_validation(self):
        s1_manifest = self.s1_results["manifest"]
        s2_manifest = self.s2_results["manifest"]

        # Section 1.1 provenance computed from manifests
        self.assert_row("s2_data_git_sha_row", "Data Generation Git SHA", s1_manifest["git_sha"], s2_manifest["data_git_sha"], section="### 1.1 Dual-Stage Provenance Header")
        self.assert_row("s2_report_git_sha_row", "Report Rendering Git SHA", s2_manifest["report_git_sha"], section="### 1.1 Dual-Stage Provenance Header")
        self.assert_row("s2_working_tree_row", "Git Working Tree Status", "Clean on data generation", f"git_dirty: {str(s2_manifest['git_dirty']).lower()}", section="### 1.1 Dual-Stage Provenance Header")
        self.assert_row("s2_run_status_row", "Run Status", s2_manifest["run_status"], section="### 1.1 Dual-Stage Provenance Header")
        self.assert_row(
            "reproducibility_archive_row",
            "Reproducibility Archive",
            f"t19a_stage1/` folder, generated at `{s1_manifest['git_sha'][:7]}`; untagged",
            "Git tag `t19b-stage2-data`",
            s2_manifest["data_git_sha"][:7],
            s2_manifest["report_git_sha"][:7],
            POSIX_ARCHIVE_SHA256,
            section="### 1.1 Dual-Stage Provenance Header",
        )
        self.assert_row("chat_model_row", "Chat Generation Model", s2_manifest["chat_model"], section="### 1.1 Dual-Stage Provenance Header")
        self.assert_row("temperature_row", "Generation Temperature", str(s2_manifest["temperature"]), "non-deterministic across sessions as shown by `q015`", section="### 1.1 Dual-Stage Provenance Header")
        self.assert_row("top_k_row", "Retrieved Context Top-K", f"Top-K = {s2_manifest['top_k']}", section="### 1.1 Dual-Stage Provenance Header")
        self.assert_row("prompt_sha_row", "System Prompt SHA-256", s2_manifest["system_prompt_sha256"], section="### 1.1 Dual-Stage Provenance Header")
        cache_pct = f"{s2_manifest['cache_hit_rate'] * 100:.1f}%"
        self.assert_row("cache_hit_rate_row", "Cache Hit Rate (Stage 2)", cache_pct, f"{s2_manifest['total_cache_hits']}/{s2_manifest['total_queries']}", section="### 1.1 Dual-Stage Provenance Header")
        self.assert_row("failed_providers_row", "Failed Provider Count", f"`{s2_manifest['failed_provider_count']}`", "16 transient first-attempt failures recovered on retry", section="### 1.1 Dual-Stage Provenance Header")

        # Section 2 ADR reference updates & tightened statuses
        s2_sec = "## 2. Pre-Registered vs. Post-Hoc Registry Table"
        self.assert_row("adr_ref_pe1", "Primary Endpoint 1", "Pre-Registered", "ADR 0010 §3 & §5", section=s2_sec)
        self.assert_row("adr_ref_pe2", "Primary Endpoint 2", "Pre-Registered", "ADR 0010 §3 & §5", section=s2_sec)
        self.assert_row("adr_ref_sec_ep", "Secondary Endpoint (Granular Extraction)", "Pre-Registered (post-Stage 1, pre-Stage 2)", "ADR 0010 §7.1 & ADR 0011 §5.2", section=s2_sec)
        self.assert_row("adr_ref_regb", "Regime B Scanned Twin Benchmark", "Pre-Registered", "ADR 0010 §2.2 & ADR 0011 §2.2", section=s2_sec)
        self.assert_row("adr_ref_ocr_lat", "Pure OCR Extractor Latency", "Planned measurement", "ADR 0009 §5", section=s2_sec)
        self.assert_row("adr_ref_sec86", "54-Judgment Manual Correctness Audit", "Post-Hoc / Diagnostic", "Section 8.6 of this report", section=s2_sec)
        self.assert_row("adr_ref_cit", "Citation Precision & Page-Hit Audit", "Pre-specified metric definitions", "ADR 0011 §7", section=s2_sec)

        # Live validation cross-check
        self.assert_row("live_verification_agreement_row", "Live Verification Agreement", "36/36 (100%) pre-LLM gate agreement at $\\tau = 0.55$", section="### 1.1 Dual-Stage Provenance Header")
        self.assert_contains("s2_val_18_18_vector", "18 / 18 (100.0%)")
        self.assert_contains("s2_val_17_18_post_llm", "17 / 18 (94.4%)")
        self.assert_contains("s2_val_q015_variation", "`q015`")

    def _audit_primary_endpoints(self):
        arms = self.s2_results["arms"]
        calibrations = self.s2_results["calibrations"]

        arm_display = [
            ("arm1_vector_ocr_off", "Arm 1 (Vector)", "a1"),
            ("arm2_hybrid_ocr_off", "Arm 2 (Hybrid)", "a2"),
            ("arm3_hybrid_ocr_defective", "Arm 3 (Full System)", "a3"),
        ]
        partitions = [
            ("heldout", "#### Held-out Generalization Partition", lambda it: it.get("docset") == "heldout"),
            ("dev", "#### Tuned Dev Partition", lambda it: it.get("docset") == "tuned" and it.get("split") == "dev"),
            ("test", "#### Tuned Test Partition", lambda it: it.get("docset") == "tuned" and it.get("split") == "test"),
        ]

        for p_code, sec_heading, p_filter in partitions:
            for arm_k, row_label, arm_short in arm_display:
                items = [it for it in arms[arm_k] if p_filter(it)]
                ans_items = [it for it in items if it.get("type") != "not-in-docs"]
                nid_items = [it for it in items if it.get("type") == "not-in-docs"]

                for op_name, tau in [
                    ("ungated", 0.0),
                    ("prod_055", 0.55),
                    ("calibrated_tau", calibrations[arm_k]),
                ]:
                    sim_ans = [simulate_threshold(it, tau) for it in ans_items]
                    sim_nid = [simulate_threshold(it, tau) for it in nid_items]

                    fr_cnt = sum(1 for s in sim_ans if s["notFound"])
                    low_cnt = sum(1 for s in sim_ans if s["no_answer_reason"] == "LowSimilarity")
                    mod_cnt = sum(1 for s in sim_ans if s["no_answer_reason"] == "ModelRefusal")
                    fa_cnt = sum(1 for s in sim_nid if not s["notFound"])

                    frr_pct = f"{fr_cnt / len(ans_items) * 100:.1f}%"
                    low_pct = f"{low_cnt / len(ans_items) * 100:.1f}%"
                    mod_pct = f"{mod_cnt / len(ans_items) * 100:.1f}%"
                    far_pct = f"{fa_cnt / len(nid_items) * 100:.1f}%"

                    ci_frr = wilson_interval_exact(fr_cnt, len(ans_items))
                    ci_far = wilson_interval_exact(fa_cnt, len(nid_items))
                    ci_frr_str = f"[{ci_frr[0] * 100:.1f}%, {ci_frr[1] * 100:.1f}%]"
                    ci_far_str = f"[{ci_far[0] * 100:.1f}%, {ci_far[1] * 100:.1f}%]"

                    op_cell = f"`{op_name}`"
                    tau_cell = f"$\\tau^*={tau:.2f}$" if op_name == "calibrated_tau" else op_cell
                    self.assert_row(
                        f"s51_{p_code}_{arm_short}_{op_name}",
                        row_label,
                        op_cell,
                        tau_cell,
                        frr_pct,
                        ci_frr_str,
                        low_pct,
                        mod_pct,
                        far_pct,
                        ci_far_str,
                        section=sec_heading,
                    )

        # Section 5.1: Overall NID Table (N=26) computed directly from Stage 2 arms
        nid_arm_display = [
            ("arm1_vector_ocr_off", "Arm 1 (Vector-Only)", "a1"),
            ("arm2_hybrid_ocr_off", "Arm 2 (Hybrid, OCR-Off)", "a2"),
            ("arm3_hybrid_ocr_defective", "Arm 3 (Full System)", "a3"),
        ]
        nid_section = "#### Overall Not-in-Docs (NID) False Acceptance Rate Summary"
        for arm_k, row_label, arm_short in nid_arm_display:
            nid_items = [it for it in arms[arm_k] if it.get("type") == "not-in-docs"]
            n_tot = len(nid_items)
            for op_name, tau in [
                ("ungated", 0.0),
                ("prod_055", 0.55),
                ("calibrated_tau", calibrations[arm_k]),
            ]:
                sim_nid = [simulate_threshold(it, tau) for it in nid_items]
                fa_items = [s for s in sim_nid if not s["notFound"]]
                fa_cnt = len(fa_items)
                low_cnt = sum(1 for s in sim_nid if s["no_answer_reason"] == "LowSimilarity")
                mod_cnt = sum(1 for s in sim_nid if s["no_answer_reason"] == "ModelRefusal")
                far_pct = f"{fa_cnt / n_tot * 100:.1f}%"
                ci_far = wilson_interval_exact(fa_cnt, n_tot)
                ci_far_str = f"[{ci_far[0] * 100:.1f}%, {ci_far[1] * 100:.1f}%]"
                attrib = f"{low_cnt} LowSim / {mod_cnt} ModRef"
                if fa_cnt > 0:
                    fa_ids = ", ".join(f"`{x['id']}`" for x in fa_items)
                    attrib = f"{attrib} ({fa_cnt} FA on {fa_ids})"
                self.assert_row(
                    f"s51_overall_nid_{arm_short}_{op_name}",
                    row_label,
                    f"`{op_name}`",
                    str(n_tot),
                    str(fa_cnt),
                    far_pct,
                    ci_far_str,
                    attrib,
                    section=nid_section,
                )

    def _audit_vector_roc_auc(self):
        # Compute ROC-AUC of bestVectorScore directly from results.json
        def _roc_auc(pos_scores: List[float], neg_scores: List[float]) -> float:
            wins = 0.0
            for ps in pos_scores:
                for ns in neg_scores:
                    if ps > ns:
                        wins += 1.0
                    elif ps == ns:
                        wins += 0.5
            return wins / (len(pos_scores) * len(neg_scores))

        arms = self.s2_results["arms"]
        for row_key, filt in [
            ("Overall", lambda it: True),
            ("Held-out", lambda it: it.get("docset") == "heldout"),
        ]:
            a12_pos = [it["bestVectorScore"] for it in arms["arm1_vector_ocr_off"] if filt(it) and it.get("type") != "not-in-docs"]
            a12_neg = [it["bestVectorScore"] for it in arms["arm1_vector_ocr_off"] if filt(it) and it.get("type") == "not-in-docs"]
            a3_pos = [it["bestVectorScore"] for it in arms["arm3_hybrid_ocr_defective"] if filt(it) and it.get("type") != "not-in-docs"]
            a3_neg = [it["bestVectorScore"] for it in arms["arm3_hybrid_ocr_defective"] if filt(it) and it.get("type") == "not-in-docs"]

            auc12 = _roc_auc(a12_pos, a12_neg)
            auc3 = _roc_auc(a3_pos, a3_neg)
            self.assert_row(
                f"roc_auc_{row_key.lower().replace('-', '_')}_row",
                row_key,
                f"{auc12:.4f}",
                f"{auc3:.4f}",
                section="### 5.5 Vector-Component ROC-AUC Analysis",
            )

    def _audit_hybrid_vs_vector_and_counterfactual(self):
        arms = self.s2_results["arms"]
        a1 = {it["id"]: simulate_threshold(it, 0.55) for it in arms["arm1_vector_ocr_off"] if it.get("type") != "not-in-docs"}
        a2 = {it["id"]: simulate_threshold(it, 0.55) for it in arms["arm2_hybrid_ocr_off"] if it.get("type") != "not-in-docs"}
        a3 = {it["id"]: simulate_threshold(it, 0.55) for it in arms["arm3_hybrid_ocr_defective"] if it.get("type") != "not-in-docs"}

        all_ids = sorted(a1.keys())
        h_ids = [qid for qid in all_ids if a1[qid].get("docset") == "heldout"]

        def _disc(mA: Dict[str, Dict[str, Any]], mB: Dict[str, Dict[str, Any]], ids: List[str], field: str, invert: bool = False) -> Tuple[int, int, float]:
            val = (lambda r: not r[field]) if invert else (lambda r: bool(r[field]))
            b = sum(1 for qid in ids if val(mA[qid]) and not val(mB[qid]))
            c = sum(1 for qid in ids if not val(mA[qid]) and val(mB[qid]))
            return b, c, mcnemar_exact(b, c)

        ans1 = sum(1 for qid in all_ids if not a1[qid]["notFound"])
        ans2 = sum(1 for qid in all_ids if not a2[qid]["notFound"])
        b_ov_ans, c_ov_ans, p_ov_ans = _disc(a1, a2, all_ids, "notFound", invert=True)
        self.assert_contains(
            "mcnemar_overall_ans",
            f"Arm 1 = {ans1 / len(all_ids) * 100:.1f}% ({ans1}/{len(all_ids)}) vs. Arm 2 = {ans2 / len(all_ids) * 100:.1f}% ({ans2}/{len(all_ids)}). Discordant pairs: $A_{{\\text{{only}}}} = {b_ov_ans}, B_{{\\text{{only}}}} = {c_ov_ans}$, exact McNemar **$p = {p_ov_ans:.3f}$**",
        )

        kw1 = sum(1 for qid in all_ids if a1[qid]["kw_hit"])
        kw2 = sum(1 for qid in all_ids if a2[qid]["kw_hit"])
        b_ov_kw, c_ov_kw, p_ov_kw = _disc(a1, a2, all_ids, "kw_hit")
        self.assert_contains(
            "mcnemar_overall_kw",
            f"Arm 1 = {kw1 / len(all_ids) * 100:.1f}% ({kw1}/{len(all_ids)}) vs. Arm 2 = {kw2 / len(all_ids) * 100:.1f}% ({kw2}/{len(all_ids)}). Discordant pairs: $A_{{\\text{{only}}}} = {b_ov_kw}, B_{{\\text{{only}}}} = {c_ov_kw}$, exact McNemar **$p = {p_ov_kw:.3f}$**",
        )

        b_h_ans, c_h_ans, p_h_ans = _disc(a1, a2, h_ids, "notFound", invert=True)
        self.assert_contains(
            "mcnemar_heldout_ans",
            f"Discordant pairs: $A_{{\\text{{only}}}} = {b_h_ans}, B_{{\\text{{only}}}} = {c_h_ans}$, exact McNemar **$p = {p_h_ans:.4f}$**",
        )

        b_h_kw, c_h_kw, p_h_kw = _disc(a1, a2, h_ids, "kw_hit")
        self.assert_contains(
            "mcnemar_heldout_kw",
            f"Discordant pairs: $A_{{\\text{{only}}}} = {b_h_kw}, B_{{\\text{{only}}}} = {c_h_kw}$, exact McNemar **$p = {p_h_kw:.3f}$**",
        )

        _, _, p_13_ans = _disc(a1, a3, h_ids, "notFound", invert=True)
        _, _, p_13_kw = _disc(a1, a3, h_ids, "kw_hit")
        self.check("mcnemar_a1_vs_a3_heldout_parity", p_13_ans == 1.0 and p_13_kw == 1.0, f"Expected p=1.0, got ans={p_13_ans}, kw={p_13_kw}")

        # Section 6.2 FTS-bypass counterfactual at tau=0.75 computed from data
        h_a2_raw = [it for it in arms["arm2_hybrid_ocr_off"] if it.get("docset") == "heldout" and it.get("type") != "not-in-docs"]
        with_fts = sum(1 for it in h_a2_raw if simulate_threshold(it, 0.75)["no_answer_reason"] == "LowSimilarity")
        no_fts = sum(1 for it in h_a2_raw if simulate_threshold({**it, "hasFtsHit": False}, 0.75)["no_answer_reason"] == "LowSimilarity")
        self.assert_contains("fts_counterfactual_with", f"Held-out LowSimilarity FRR = **{with_fts / len(h_a2_raw) * 100:.1f}%** ({with_fts}/{len(h_a2_raw)})")
        self.assert_contains("fts_counterfactual_without", f"Held-out LowSimilarity FRR = **{no_fts / len(h_a2_raw) * 100:.1f}%** ({no_fts}/{len(h_a2_raw)})")

    def _audit_null_experiment(self):
        # Compute clean-doc null experiment counts from Stage 2 arms (evaluated under ungated context)
        arms = self.s2_results["arms"]
        a2 = {it["id"]: simulate_threshold(it, 0.0) for it in arms["arm2_hybrid_ocr_off"] if it.get("type") != "not-in-docs"}
        a3 = {it["id"]: simulate_threshold(it, 0.0) for it in arms["arm3_hybrid_ocr_defective"] if it.get("type") != "not-in-docs"}
        non_ocr_docs = {
            "aein-91.pdf", "bein-almelali.pdf", "bge-m3-paper.pdf", "dastyar-amoozeshi.pdf",
            "rag-lewis-2020.pdf", "shiveh-karamoozi.pdf", "shoraha-pazhuheshi.pdf", "ziro-guide.pdf",
        }
        ids_76 = [qid for qid, it in a2.items() if it.get("source_file") in non_ocr_docs]
        ans_dis_76 = sum(1 for qid in ids_76 if a2[qid]["notFound"] != a3[qid]["notFound"])
        ans_both_76 = sum(1 for qid in ids_76 if not a2[qid]["notFound"])
        kw_dis_76 = sum(1 for qid in ids_76 if a2[qid]["kw_hit"] != a3[qid]["kw_hit"])
        kw_both_76 = sum(1 for qid in ids_76 if a2[qid]["kw_hit"])

        ids_83 = [qid for qid, it in a2.items() if not it.get("text_layer_defect", False)]
        ans_dis_83 = sum(1 for qid in ids_83 if a2[qid]["notFound"] != a3[qid]["notFound"])
        kw_diff_83 = sorted(qid for qid in ids_83 if a2[qid]["kw_hit"] != a3[qid]["kw_hit"])

        self.assert_contains("null_exp_0_76_disagreements", f"Answer Status Disagreements:** **{ans_dis_76} / {len(ids_76)}** (both configurations answered {ans_both_76}/{len(ids_76)})")
        self.assert_contains("null_exp_kw_0_76", f"Keyword Hit Disagreements:** **{kw_dis_76} / {len(ids_76)}** (both configurations achieved {kw_both_76}/{len(ids_76)} keyword hits")
        self.assert_contains("null_exp_defect_false_0_83", f"Answer status disagreements: **{ans_dis_83} / {len(ids_83)}**")
        self.assert_contains("null_exp_defect_false_kw_2_83", f"Keyword hit disagreements: **{len(kw_diff_83)} / {len(ids_83)}**")
        self.assert_contains("null_exp_diff_q090_q094", " and ".join(f"`{q}`" for q in kw_diff_83))

    def audit_manual_cmap_from_csv(self) -> None:
        """CI-safe verification of Table 8.6 and its Per-Document table from tracked CSV + questions.json."""
        csv_path = REPO_ROOT / "eval/manual_audit_heldout_cmap.csv"
        questions_path = REPO_ROOT / "eval/questions.json"
        if not self.questions:
            self.questions = json.loads(questions_path.read_text(encoding="utf-8"))
        q_by_id = {q["id"]: q for q in self.questions}

        with open(csv_path, "r", encoding="utf-8", newline="") as f:
            reader = csv.DictReader(f)
            self.check(
                "manual_csv_header_exact",
                reader.fieldnames == ["id", "arm", "label", "note"],
                f"Expected columns ['id', 'arm', 'label', 'note'], got {reader.fieldnames}",
            )
            rows = list(reader)

        self.check("manual_csv_row_count_54", len(rows) == 54, f"Expected 54 rows in manual CSV, found {len(rows)}")
        valid_labels = {"correct", "partial", "wrong", "refused"}
        by_arm: Dict[str, Dict[str, Dict[str, str]]] = {"arm2": {}, "arm3": {}}
        for r in rows:
            qid, arm, lbl = r["id"], r["arm"], r["label"]
            if arm in by_arm and lbl in valid_labels and qid in q_by_id:
                by_arm[arm][qid] = r

        cmap_ids = {q["id"] for q in self.questions if q.get("source_file") in HELDOUT_CMAP_FILES and q.get("type") != "not-in-docs"}
        self.check(
            "manual_csv_coverage_27_ids_both_arms",
            set(by_arm["arm2"].keys()) == cmap_ids and set(by_arm["arm3"].keys()) == cmap_ids,
            f"Arm keys mismatch heldout-CMap IDs (expected {len(cmap_ids)})",
        )

        # Compute aggregate Table 8.6 counts from CSV
        n_total = len(cmap_ids)
        row_specs = [
            ("sec_tbl86_correct", "Correct", "correct"),
            ("sec_tbl86_partial", "Partial (no error)", "partial"),
            ("sec_tbl86_wrong", "Wrong (factual/numeric error)", "wrong"),
            ("sec_tbl86_refused", "Refused", "refused"),
        ]
        for check_name, row_key, lbl in row_specs:
            c2 = sum(1 for qid in cmap_ids if by_arm["arm2"][qid]["label"] == lbl)
            c3 = sum(1 for qid in cmap_ids if by_arm["arm3"][qid]["label"] == lbl)
            s2 = f"{c2} / {n_total} ({c2 / n_total * 100:.1f}%)"
            s3 = f"{c3} / {n_total} ({c3 / n_total * 100:.1f}%)"
            delta_pp = f"{(c3 - c2) / n_total * 100:+.1f} pp"
            extra: List[str] = []
            if lbl == "wrong":
                b = sum(1 for qid in cmap_ids if by_arm["arm2"][qid]["label"] == "wrong" and by_arm["arm3"][qid]["label"] != "wrong")
                c = sum(1 for qid in cmap_ids if by_arm["arm2"][qid]["label"] != "wrong" and by_arm["arm3"][qid]["label"] == "wrong")
                p_val = mcnemar_exact(b, c)
                extra.append(f"Paired discordant {b} vs {c}")
                extra.append(f"p = {p_val:.3f}")
            self.assert_row(
                check_name,
                row_key,
                s2,
                s3,
                delta_pp,
                *extra,
                section="### 8.6 54-Judgment Manual Correctness Audit",
            )

        # CMap digit-corruption subset computed from CSV annotations
        cmap_d2 = sum(1 for qid in cmap_ids if "CMap digit" in by_arm["arm2"][qid]["note"])
        cmap_d3 = sum(1 for qid in cmap_ids if by_arm["arm3"][qid]["label"] == "wrong" and "slip" in by_arm["arm3"][qid]["note"])
        self.assert_row(
            "sec_tbl86_cmap_subset",
            "CMap Digit-Corruption Subset",
            f"{cmap_d2} / {n_total} ({cmap_d2 / n_total * 100:.1f}%)",
            f"{cmap_d3} / {n_total} ({cmap_d3 / n_total * 100:.1f}%)",
            f"{(cmap_d3 - cmap_d2) / n_total * 100:+.1f} pp",
            section="### 8.6 54-Judgment Manual Correctness Audit",
        )

        # Per-Document Breakdown table computed directly from CSV
        order_lbls = ["correct", "partial", "wrong", "refused"]
        for doc_fn in ["forsat-motaleati.pdf", "tarh-dakheli.pdf"]:
            doc_ids = [qid for qid in cmap_ids if q_by_id[qid]["source_file"] == doc_fn]
            s2_doc = " / ".join(str(sum(1 for qid in doc_ids if by_arm["arm2"][qid]["label"] == lbl)) for lbl in order_lbls)
            s3_doc = " / ".join(str(sum(1 for qid in doc_ids if by_arm["arm3"][qid]["label"] == lbl)) for lbl in order_lbls)
            self.assert_row(
                f"sec_tbl86_perdoc_{doc_fn.split('.')[0]}",
                f"`{doc_fn}`",
                f"$N={len(doc_ids)}$",
                s2_doc,
                s3_doc,
                section="#### Per-Document Breakdown",
            )

        # Rater provenance & wording checks
        self.assert_contains("sec86_rater_provenance", "single-rater, unblinded, author-reviewed judgments (LLM-assisted)")
        self.assert_contains("sec86_csv_link", "eval/manual_audit_heldout_cmap.csv")
        self.assert_contains(
            "sec86_accurate_7_err_outcome",
            "**4 fully corrected answers (`q079, q096, q097, q099`), 2 converted to partial/refusal (`q081` partial, `q102` refused — abstention is not a correct answer), and 1 numeric error remaining (`q080`)**",
        )
        self.assert_contains("sec86_hypotheses_labeled", "**Hypotheses (not verified by ablation):**")
        self.assert_contains("sec86_doc_wide_ocr_hypothesis", "**Document-Wide OCR Propagation Hypothesis:**")
        self.assert_contains("sec86_presidential_board_translation", "University Presidential Board")
        self.assert_contains("sec86_mcnemar_borderline_invariance", "strictly on the binary `wrong` vs. non-`wrong`")
        self.assert_contains("sec11_2_rater_bias_caveat", "single-rater, unblinded, and LLM-assisted, introducing potential confirmation bias")

    def _audit_secondary_endpoint_heldout_cmap(self):
        # 1. Table 8.1 computed directly from Stage 2 results.json (ungated operating point)
        arms = self.s2_results["arms"]
        a2_map = {it["id"]: simulate_threshold(it, 0.0) for it in arms["arm2_hybrid_ocr_off"] if it.get("source_file") in HELDOUT_CMAP_FILES and it.get("type") != "not-in-docs"}
        a3_map = {it["id"]: simulate_threshold(it, 0.0) for it in arms["arm3_hybrid_ocr_defective"] if it.get("source_file") in HELDOUT_CMAP_FILES and it.get("type") != "not-in-docs"}

        sec81_specs = [
            ("sec_tbl81_numbers", "Multi-digit Numbers", "num_hit"),
            ("sec_tbl81_unit_strict", "Unit Phrases Strict", "unit_hit_strict"),
            ("sec_tbl81_unit_prox", "Unit Phrases Proximity", "unit_hit_proximity_10"),
        ]
        for cname, rkey, field in sec81_specs:
            ids = [qid for qid, it in a3_map.items() if it.get(field) is not None]
            n = len(ids)
            h2 = sum(1 for qid in ids if a2_map[qid].get(field) is True)
            h3 = sum(1 for qid in ids if a3_map[qid].get(field) is True)
            ci2 = wilson_interval_exact(h2, n)
            ci3 = wilson_interval_exact(h3, n)
            b = sum(1 for qid in ids if a2_map[qid].get(field) is True and a3_map[qid].get(field) is not True)
            c = sum(1 for qid in ids if a2_map[qid].get(field) is not True and a3_map[qid].get(field) is True)
            pval = mcnemar_exact(b, c)
            self.assert_row(
                cname,
                rkey,
                f"$N={n}$",
                f"{h2} / {n} ({h2 / n * 100:.1f}%)",
                f"[{ci2[0] * 100:.1f}%, {ci2[1] * 100:.1f}%]",
                f"{h3} / {n} ({h3 / n * 100:.1f}%)",
                f"[{ci3[0] * 100:.1f}%, {ci3[1] * 100:.1f}%]",
                f"{(h3 - h2) / n * 100:+.1f} pp",
                f"p = {pval:.3f}",
                section="### 8.1 Secondary Endpoint Metrics",
            )

        # 2. Table 8.5 Contextual Comparison computed directly from results.json (ungated)
        a2_all = [simulate_threshold(it, 0.0) for it in arms["arm2_hybrid_ocr_off"] if it.get("type") != "not-in-docs"]
        a3_all = [simulate_threshold(it, 0.0) for it in arms["arm3_hybrid_ocr_defective"] if it.get("type") != "not-in-docs"]
        cohorts_85 = [
            ("sec_tbl85_heldout_cmap", "`heldout-CMap` ($N=27, G=2$)", lambda it: it.get("source_file") in HELDOUT_CMAP_FILES),
            ("sec_tbl85_primary_cmap", "`primary_cmap` ($N=36, G=6$)", lambda it: it.get("source_file") in PRIMARY_CMAP_FILES),
            ("sec_tbl85_overall", "`overall` ($N=112$)", lambda it: True),
        ]
        for cname, rkey, flt in cohorts_85:
            s2 = [it for it in a2_all if flt(it)]
            s3 = [it for it in a3_all if flt(it)]

            def _pair_fmt(field: str, use_all: bool = False) -> str:
                if use_all:
                    n2, n3 = len(s2), len(s3)
                    k2 = sum(1 for x in s2 if x.get(field) is True)
                    k3 = sum(1 for x in s3 if x.get(field) is True)
                else:
                    sub2 = [x for x in s2 if x.get(field) is not None]
                    sub3 = [x for x in s3 if x.get(field) is not None]
                    n2, n3 = len(sub2), len(sub3)
                    k2 = sum(1 for x in sub2 if x.get(field) is True)
                    k3 = sum(1 for x in sub3 if x.get(field) is True)
                return f"{k2}/{n2} ({k2 / n2 * 100:.1f}%) vs. {k3}/{n3} ({k3 / n3 * 100:.1f}%)"

            self.assert_row(
                cname,
                rkey,
                _pair_fmt("num_hit"),
                _pair_fmt("unit_hit_strict"),
                _pair_fmt("unit_hit_proximity_10"),
                _pair_fmt("kw_hit", use_all=True),
                section="### 8.5 Contextual Comparison Across Cohorts",
            )

        # 3. Table 8.6 & Per-Doc Breakdown computed directly from CSV
        self.audit_manual_cmap_from_csv()

    def _audit_frr_decomposition(self):
        # Compute prod_055 answerable FRR decomposition directly from results.json + S1 MRR top-6 rank proxy
        arms = self.s2_results["arms"]
        s1_arms = self.s1_results["arms"]
        for arm_k, label, s1_k in [
            ("arm1_vector_ocr_off", "Arm 1", "arm1_vector_ocr_off"),
            ("arm2_hybrid_ocr_off", "Arm 2", "arm2_hybrid_ocr_off"),
            ("arm3_hybrid_ocr_defective", "Arm 3", "arm3_hybrid_ocr_defective"),
        ]:
            s1_by_id = {it["id"]: it for it in s1_arms[s1_k]}
            sim_ans = [simulate_threshold(it, 0.55) for it in arms[arm_k] if it.get("type") != "not-in-docs"]
            refused = [s for s in sim_ans if s["notFound"]]
            low_ids = sorted(s["id"] for s in refused if s["no_answer_reason"] == "LowSimilarity")
            rem = [s for s in refused if s["no_answer_reason"] != "LowSimilarity"]
            ret_miss_ids = sorted(s["id"] for s in rem if s1_by_id[s["id"]].get("mrr", 0.0) < (1.0 / 6.0 - 1e-6))
            top6_def_t = sorted(s["id"] for s in rem if s["id"] not in ret_miss_ids and s.get("text_layer_defect"))
            top6_def_f = sorted(s["id"] for s in rem if s["id"] not in ret_miss_ids and not s.get("text_layer_defect"))

            self.assert_contains(
                f"decomp_{arm_k}_tot",
                f"{label} ($Tot={len(refused)}$):** `LowSimilarity = {len(low_ids)}` (`{', '.join(low_ids)}`), "
                f"`RetMiss = {len(ret_miss_ids)}` (`{', '.join(ret_miss_ids)}`), "
                f"`Gold-in-top-6 (defect=True) = {len(top6_def_t)}` (`{', '.join(top6_def_t)}`), "
                f"`Gold-in-top-6 (defect=False) = {len(top6_def_f)}` (`{', '.join(top6_def_f)}`)",
            )

    def _audit_question_types_and_citations(self):
        # 1. Programmatic cell-level and row-scoped verification of ALL 6 rows × 3 arms of Table 9.3 at prod_055
        questions_by_id = {q["id"]: q for q in self.questions}
        table_counts: Dict[str, Dict[str, Dict[str, int]]] = {}
        for arm_key in ["arm1_vector_ocr_off", "arm2_hybrid_ocr_off", "arm3_hybrid_ocr_defective"]:
            rows = self.s2_results["arms"][arm_key]
            counts_by_type: Dict[str, Dict[str, int]] = {}
            for r in rows:
                qid = r["id"]
                q = questions_by_id[qid]
                if q.get("type") == "not-in-docs":
                    continue
                qtype = q.get("type", "unknown")
                if qtype not in counts_by_type:
                    counts_by_type[qtype] = {"ans": 0, "kw": 0, "total": 0}
                counts_by_type[qtype]["total"] += 1

                sim = simulate_threshold(r, 0.55)
                if not sim["notFound"]:
                    counts_by_type[qtype]["ans"] += 1
                if sim.get("kw_hit"):
                    counts_by_type[qtype]["kw"] += 1
            table_counts[arm_key] = counts_by_type

        for t in ["numeric", "legal-reference", "paraphrase", "multi-chunk", "factual", "english"]:
            c1 = table_counts["arm1_vector_ocr_off"][t]
            c2 = table_counts["arm2_hybrid_ocr_off"][t]
            c3 = table_counts["arm3_hybrid_ocr_defective"][t]
            tot = c1["total"]
            self.assert_row(
                f"tbl93_{t}_row",
                f"`{t}`",
                str(tot),
                f"{c1['ans'] / tot * 100:.1f}% ({c1['ans']}/{tot})",
                f"{c1['kw'] / tot * 100:.1f}% ({c1['kw']}/{tot})",
                f"{c2['ans'] / tot * 100:.1f}% ({c2['ans']}/{tot})",
                f"{c2['kw'] / tot * 100:.1f}% ({c2['kw']}/{tot})",
                f"{c3['ans'] / tot * 100:.1f}% ({c3['ans']}/{tot})",
                f"{c3['kw'] / tot * 100:.1f}% ({c3['kw']}/{tot})",
                section="### 9.3 Question-Type Breakdown",
            )

        # 2. Table 9.4 Citations at prod_055 computed directly via simulate_threshold
        cit_stats: Dict[str, Dict[str, Any]] = {}
        for arm_key in ["arm1_vector_ocr_off", "arm2_hybrid_ocr_off", "arm3_hybrid_ocr_defective"]:
            ans_items = [it for it in self.s2_results["arms"][arm_key] if it.get("type") != "not-in-docs"]
            sim = [simulate_threshold(it, 0.55) for it in ans_items]
            answered = [s for s in sim if not s["notFound"]]
            n_ans = len(answered)
            val_cnt = sum(1 for s in answered if s["citation_validity"])
            page_cnt = sum(1 for s in answered if s["citation_page_hit"])
            prec_mean = float(np.mean([s["citation_precision"] for s in answered]))
            rec_mean = float(np.mean([s["citation_recall"] for s in answered]))
            cit_stats[arm_key] = {
                "n_ans": n_ans,
                "val": f"{val_cnt / n_ans * 100:.1f}% ({val_cnt}/{n_ans})",
                "page": f"{page_cnt / n_ans * 100:.1f}% ({page_cnt}/{n_ans})",
                "prec": f"{prec_mean * 100:.1f}%",
                "rec": f"{rec_mean * 100:.1f}%",
            }

        c1_s = cit_stats["arm1_vector_ocr_off"]
        c2_s = cit_stats["arm2_hybrid_ocr_off"]
        c3_s = cit_stats["arm3_hybrid_ocr_defective"]
        cit_section = "### 9.4 Citation Metrics"
        self.assert_row(
            "cit_tbl94_header_n_ans",
            "Metric",
            f"$N_{{\\text{{ans}}}}={c1_s['n_ans']}$",
            f"$N_{{\\text{{ans}}}}={c2_s['n_ans']}$",
            f"$N_{{\\text{{ans}}}}={c3_s['n_ans']}$",
            section=cit_section,
        )
        self.assert_row("cit_tbl94_val", "Citation Validity Rate", c1_s["val"], c2_s["val"], c3_s["val"], section=cit_section)
        self.assert_row("cit_tbl94_page", "Citation Page-Hit Rate", c1_s["page"], c2_s["page"], c3_s["page"], section=cit_section)
        self.assert_row("cit_tbl94_prec", "Citation Precision (Mean)", c1_s["prec"], c2_s["prec"], c3_s["prec"], section=cit_section)
        self.assert_row("cit_tbl94_rec", "Citation Recall (Mean)", c1_s["rec"], c2_s["rec"], c3_s["rec"], section=cit_section)

    def _audit_regime_b_case_study(self):
        # Compute Regime B case study counts at prod_055 directly from results.json
        arms = self.s2_results["arms"]
        rb_off = [simulate_threshold(it, 0.55) for it in arms["regime_b_scan_off"]]
        rb_def = [simulate_threshold(it, 0.55) for it in arms["regime_b_scan_defective"]]
        rb_ids = {it["id"] for it in arms["regime_b_scan_defective"]}
        dig_ref = [simulate_threshold(it, 0.55) for it in arms["arm2_hybrid_ocr_off"] if it["id"] in rb_ids]

        off_ans = sum(1 for s in rb_off if not s["notFound"])
        def_ans = sum(1 for s in rb_def if not s["notFound"])
        dig_ans = sum(1 for s in dig_ref if not s["notFound"])
        self.assert_contains("regb_scan_off_ans", f"**{off_ans} / {len(rb_off)} ({off_ans / len(rb_off) * 100:.1f}%) answered**")
        self.assert_contains("regb_scan_def_ans", f"**{def_ans} / {len(rb_def)} ({def_ans / len(rb_def) * 100:.1f}%) answered**")
        self.assert_contains("regb_digital_ref_ans", f"**{dig_ans} / {len(dig_ref)} ({dig_ans / len(dig_ref) * 100:.1f}%) answered**")

    def _audit_latencies(self):
        # Table 9.6 Live System Latency Profile computed directly from uncached queries
        arms = self.s2_results["arms"]
        lat_rows = [
            ("lat_tbl96_a1", "Arm 1 (Vector-Only)", "arm1_vector_ocr_off"),
            ("lat_tbl96_a2", "Arm 2 (Hybrid)", "arm2_hybrid_ocr_off"),
            ("lat_tbl96_a3", "Arm 3 (Full System)", "arm3_hybrid_ocr_defective"),
            ("lat_tbl96_regb", "Regime B Defective", "regime_b_scan_defective"),
        ]
        for cname, row_key, arm_k in lat_rows:
            live = [it for it in arms[arm_k] if not it.get("is_cached", False)]
            ret = [it["retrieval_ms"] for it in live]
            gen = [it["generation_ms"] for it in live]
            tot = [it["total_ms"] for it in live]
            self.assert_row(
                cname,
                row_key,
                f"$n={len(live)}$",
                f"{np.percentile(ret, 50):.1f} ms / {np.percentile(ret, 95):.1f} ms",
                f"{np.percentile(gen, 50):.1f} ms / {np.percentile(gen, 95):.1f} ms",
                f"{np.percentile(tot, 50):.1f} ms / {np.percentile(tot, 95):.1f} ms",
                section="### 9.6 Live System Latency Profile",
            )

    def _audit_production_reliability(self):
        self.assert_contains("rel_degraded_16_417", "**16 / 417 initial requests**")
        self.assert_contains("rel_recovered_16_16", "**16 / 16 recovery**")
        self.assert_contains("rel_unclassified_15", "the remaining 15 first-attempt degraded responses are unclassified (not broken down by HTTP 429/5xx/timeout in saved logs)")


def main():
    parser = argparse.ArgumentParser(description="Audit docs/EVAL-RESULTS.md numbers and hygiene.")
    parser.add_argument(
        "--doc",
        type=Path,
        default=REPO_ROOT / "docs" / "EVAL-RESULTS.md",
        help="Path to evaluation results document to audit.",
    )
    args = parser.parse_args()

    auditor = DocAuditor(args.doc)
    success = auditor.run_all_audits()
    if not success:
        sys.exit(1)
    sys.exit(0)


if __name__ == "__main__":
    main()
