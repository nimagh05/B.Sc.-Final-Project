#!/usr/bin/env python3
"""audit_docs_numbers.py — Reproducible verification script for docs/EVAL-RESULTS.md.

Programmatically computes ground-truth numbers from JSON/CSV evaluation artifacts and
asserts exact presence and anti-fabrication / strict-hygiene rules in docs/EVAL-RESULTS.md.

Usage:
    python eval/audit_docs_numbers.py [--doc docs/EVAL-RESULTS.md]

Exit code:
    0: 100% checks passed cleanly.
    1: One or more checks failed.
"""

import argparse
import json
import re
import sys
from pathlib import Path
from typing import List, Tuple

REPO_ROOT = Path(__file__).resolve().parent.parent

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
        self.questions = []

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
            # Case-insensitive search
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
        # 138 unique questions in questions.json across 14 PDFs
        unique_q_count = len(self.questions)
        self.check("dataset_unique_questions_138", unique_q_count == 138, f"Expected 138 questions, found {unique_q_count}")
        self.assert_contains("doc_mentions_138_questions", "138")
        self.assert_contains("doc_mentions_14_pdfs", "14")
        self.assert_contains("doc_mentions_74_tuned", "74")
        self.assert_contains("doc_mentions_64_heldout", "64")
        self.assert_contains("doc_mentions_heldout_3_pdfs", "3 PDFs")
        self.assert_contains("doc_mentions_shiveh_karamoozi", "shiveh-karamoozi.pdf")

    def _audit_stage1_metrics(self):
        # Stage 1 Git SHA and MRR
        s1_manifest = self.s1_results["manifest"]
        self.assert_contains("s1_data_git_sha", s1_manifest["git_sha"])
        self.assert_contains("s1_overall_mrr_a1_08500", "0.8500")
        self.assert_contains("s1_overall_mrr_a2_07837", "0.7837")
        self.assert_contains("s1_overall_mrr_a3_07822", "0.7822")
        self.assert_contains("s1_overall_delta_2vs1", "-0.0663")
        self.assert_contains("s1_heldout_mrr_a1_08638", "0.8638")
        self.assert_contains("s1_heldout_mrr_a2_07919", "0.7919")
        self.assert_contains("s1_heldout_mrr_a3_07903", "0.7903")
        self.assert_contains("s1_heldout_delta_2vs1", "-0.0719")

        # CMap heterogeneity
        self.assert_contains("s1_forsat_delta_pos_01153", "+0.1153")
        self.assert_contains("s1_tarh_delta_neg_00978", "-0.0978")

        # Pure OCR timing
        self.assert_contains("s1_pure_ocr_overall_4131", "4.131")
        self.assert_contains("s1_pure_ocr_regime_a_3608", "3.608")
        self.assert_contains("s1_pure_ocr_regime_b_6026", "6.026")
        self.assert_contains("s1_ocr_pages_37", "37")
        self.assert_contains("s1_ocr_pages_29", "29")
        self.assert_contains("s1_ocr_pages_8", "8")

        # Stage 1 Regime B retrieval
        self.assert_contains("s1_regb_scan_off_0000", "0.000")
        self.assert_contains("s1_regb_scan_def_0875", "0.875")
        self.assert_contains("s1_regb_digital_ref_09167", "0.9167")
        self.assert_contains("s1_regb_delta_neg_00417", "-0.0417")

    def _audit_stage2_provenance_and_validation(self):
        s2_manifest = self.s2_results["manifest"]
        self.assert_contains("s2_data_git_sha", s2_manifest["data_git_sha"])
        self.assert_contains("s2_chat_model", "gemini-3.5-flash-lite")
        self.assert_contains("s2_temperature_00", "0.0")
        self.assert_contains("s2_top_k_6", "6")
        self.assert_contains("s2_prompt_sha256", "3d7c8fe28584d70e188198b9cc72f740f813b70fd6b910f82ff363e01fe8265f")
        self.assert_contains("s2_cache_hit_rate_12", "1.2%")
        self.assert_contains("s2_failed_providers_0", "0")

        # Live validation cross-check
        self.assert_regex("s2_val_36_36_pre_llm", r"36\s*/\s*36")
        self.assert_regex("s2_val_18_18_vector", r"18\s*/\s*18")
        self.assert_regex("s2_val_17_18_post_llm", r"17\s*/\s*18")
        self.assert_contains("s2_val_q015_variation", "q015")

    def _audit_primary_endpoints(self):
        # Held-out Answerable FRR (N=52)
        self.assert_regex("pe21_a1_prod055_frr", r"15\.4%.*?\[8\.0%,\s*27\.5%\]")
        self.assert_regex("pe21_a1_calib_frr", r"76\.9%.*?\[63\.9%,\s*86\.3%\]")
        self.assert_regex("pe21_a2_prod055_frr", r"11\.5%.*?\[5\.4%,\s*23\.0%\]")
        self.assert_regex("pe21_a2_calib_frr", r"40\.4%.*?\[28\.2%,\s*53\.9%\]")
        self.assert_regex("pe21_a3_prod055_frr", r"15\.4%.*?\[8\.0%,\s*27\.5%\]")
        self.assert_regex("pe21_a3_calib_frr", r"40\.4%.*?\[28\.2%,\s*53\.9%\]")

        # Held-out NID FAR (N=12)
        self.assert_regex("pe22_floor_limited_far", r"0\.0%.*?\[0\.0%,\s*24\.2%\]")
        self.assert_contains("pe22_nid_count_12", "N=12")

        # In-sample dev and test
        self.assert_contains("pe_dev_n_ans_18", "18")
        self.assert_contains("pe_dev_n_nid_7", "7")
        self.assert_contains("pe_test_n_ans_42", "42")
        self.assert_regex("pe_dev_a2_far_143", r"14\.3%")
        self.assert_contains("pe_sole_fa_q043", "q043")

        # Full dev / test / overall NID breakdown tables
        self.assert_regex("pe_dev_a1_prod055_frr", r"5\.6%.*?\[1\.0%,\s*25\.8%\]")
        self.assert_regex("pe_dev_a1_calib_frr", r"61\.1%.*?\[38\.6%,\s*79\.7%\]")
        self.assert_regex("pe_test_a1_prod055_frr", r"9\.5%.*?\[3\.8%,\s*22\.1%\]")
        self.assert_regex("pe_test_a1_calib_frr", r"61\.9%.*?\[46\.8%,\s*75\.0%\]")
        self.assert_regex("pe_overall_nid_a1_far", r"0\.0%.*?\[0\.0%,\s*12\.9%\]")
        self.assert_regex("pe_overall_nid_a2_far", r"3\.8%.*?\[0\.7%,\s*18\.9%\]")

    def _audit_vector_roc_auc(self):
        # Overall
        self.assert_contains("roc_auc_overall_a1_07424", "0.7424")
        self.assert_contains("roc_auc_overall_a1_ci", "[0.649, 0.836]")
        self.assert_contains("roc_auc_overall_a3_07208", "0.7208")
        self.assert_contains("roc_auc_overall_a3_ci", "[0.623, 0.818]")

        # Held-out
        self.assert_contains("roc_auc_heldout_a1_06250", "0.6250")
        self.assert_contains("roc_auc_heldout_a1_ci", "[0.459, 0.791]")
        self.assert_contains("roc_auc_heldout_a3_05641", "0.5641")
        self.assert_contains("roc_auc_heldout_a3_ci", "[0.388, 0.740]")

    def _audit_hybrid_vs_vector_and_counterfactual(self):
        # McNemar p-values
        self.assert_regex("mcnemar_overall_ans_p1000", r"p\s*=\s*1\.000")
        self.assert_regex("mcnemar_heldout_ans_p06875", r"p\s*=\s*0\.6875")

        # FTS-bypass counterfactual
        self.assert_contains("fts_counterfactual_with_346", "34.6%")
        self.assert_contains("fts_counterfactual_without_981", "98.1%")
        self.assert_regex("fts_counterfactual_count_51_52", r"51\s*/\s*52")

    def _audit_null_experiment(self):
        # 8 non-OCR PDFs: 0/76 disagreements
        self.assert_regex("null_exp_0_76_disagreements", r"0\s*/\s*76")
        self.assert_regex("null_exp_ans_69_76", r"69\s*/\s*76")
        self.assert_regex("null_exp_kw_61_76", r"61\s*/\s*76")
        self.assert_regex("null_exp_defect_false_0_83", r"0\s*/\s*83")
        self.assert_regex("null_exp_defect_false_kw_2_83", r"2\s*/\s*83")
        self.assert_contains("null_exp_diff_q090", "q090")
        self.assert_contains("null_exp_diff_q094", "q094")

    def _audit_secondary_endpoint_heldout_cmap(self):
        # heldout-CMap cohort: 27 answerable, 0 NID
        self.assert_contains("secondary_endpoint_n27", "N=27")
        self.assert_regex("sec_numbers_0_7_vs_3_7", r"0\s*/\s*7")
        self.assert_regex("sec_numbers_3_7", r"3\s*/\s*7")
        self.assert_regex("sec_numbers_p0250", r"p\s*=\s*0\.250")
        self.assert_regex("sec_units_strict_1_10_vs_2_10", r"1\s*/\s*10")
        self.assert_regex("sec_units_strict_2_10", r"2\s*/\s*10")
        self.assert_regex("sec_units_strict_p1000", r"p\s*=\s*1\.000")
        self.assert_regex("sec_units_prox_1_10_vs_3_10", r"3\s*/\s*10")
        self.assert_regex("sec_units_prox_p0625", r"p\s*=\s*0\.625")

        # Confident wrong numbers
        for q in ["q079", "q080", "q081", "q096", "q099"]:
            self.assert_contains(f"sec_wrong_num_{q}", q)

        # Per-doc CMap breakdown
        self.assert_regex("sec_forsat_num_0_3_vs_2_3", r"2\s*/\s*3")
        self.assert_regex("sec_tarh_num_0_4_vs_1_4", r"1\s*/\s*4")

    def _audit_frr_decomposition(self):
        # Arm 1 refused = 13 (LowSim=1, RetMiss=3, Defect=True=6, Defect=False=3)
        self.assert_contains("decomp_a1_tot_13", "Tot=13")
        self.assert_contains("decomp_a2_tot_12", "Tot=12")
        self.assert_contains("decomp_a3_tot_15", "Tot=15")
        for q in ["q015", "q034", "q056", "q058", "q078", "q088", "q100", "q101"]:
            self.assert_contains(f"decomp_q_{q}", q)

    def _audit_question_types_and_citations(self):
        # 6 question types
        for t, cnt in [("numeric", "22"), ("factual", "15"), ("legal-reference", "23"), ("multi-chunk", "18"), ("paraphrase", "22"), ("english", "12")]:
            self.assert_contains(f"qtype_{t}_{cnt}", cnt)

        # Programmatic cell-level verification of ALL 6 rows × 3 arms of Table 9.3 computed directly from results.json at prod_055
        questions_by_id = {q["id"]: q for q in self.questions}
        table_counts = {}
        for arm_key in ["arm1_vector_ocr_off", "arm2_hybrid_ocr_off", "arm3_hybrid_ocr_defective"]:
            rows = self.s2_results["arms"][arm_key]
            counts_by_type = {}
            for r in rows:
                qid = r["id"]
                q = questions_by_id[qid]
                if q.get("type") == "not-in-docs":
                    continue
                qtype = q.get("type", "unknown")
                if qtype not in counts_by_type:
                    counts_by_type[qtype] = {"ans": 0, "kw": 0, "total": 0}
                counts_by_type[qtype]["total"] += 1

                score = r.get("bestVectorScore", 0.0)
                has_fts = r.get("hasFtsHit", False)
                is_hybrid = "hybrid" in arm_key
                low_sim = (score < 0.55) if not is_hybrid else (score < 0.55 and not has_fts)
                answered = (not low_sim) and (not r.get("notFound", False))
                kw_hit = (not low_sim) and r.get("kw_hit", False)
                if answered:
                    counts_by_type[qtype]["ans"] += 1
                if kw_hit:
                    counts_by_type[qtype]["kw"] += 1
            table_counts[arm_key] = counts_by_type

        # Verify all 6 rows × 3 arms in Table 9.3 (ans and kw counts)
        for t in ["numeric", "legal-reference", "paraphrase", "multi-chunk", "factual", "english"]:
            c1 = table_counts["arm1_vector_ocr_off"][t]
            c2 = table_counts["arm2_hybrid_ocr_off"][t]
            c3 = table_counts["arm3_hybrid_ocr_defective"][t]
            tot = c1["total"]
            self.assert_regex(f"tbl93_{t}_a1_ans_{c1['ans']}_{tot}", rf"{c1['ans']}\s*/\s*{tot}")
            self.assert_regex(f"tbl93_{t}_a1_kw_{c1['kw']}_{tot}", rf"{c1['kw']}\s*/\s*{tot}")
            self.assert_regex(f"tbl93_{t}_a2_ans_{c2['ans']}_{tot}", rf"{c2['ans']}\s*/\s*{tot}")
            self.assert_regex(f"tbl93_{t}_a2_kw_{c2['kw']}_{tot}", rf"{c2['kw']}\s*/\s*{tot}")
            self.assert_regex(f"tbl93_{t}_a3_ans_{c3['ans']}_{tot}", rf"{c3['ans']}\s*/\s*{tot}")
            self.assert_regex(f"tbl93_{t}_a3_kw_{c3['kw']}_{tot}", rf"{c3['kw']}\s*/\s*{tot}")

        # Citations denominators and rates
        self.assert_contains("cit_denom_99", "99")
        self.assert_contains("cit_denom_100", "100")
        self.assert_contains("cit_denom_97", "97")
        self.assert_contains("cit_val_a1_838", "83.8%")
        self.assert_contains("cit_val_a2_830", "83.0%")
        self.assert_contains("cit_val_a3_856", "85.6%")
        self.assert_contains("cit_page_a1_717", "71.7%")
        self.assert_contains("cit_page_a2_730", "73.0%")
        self.assert_contains("cit_page_a3_742", "74.2%")
        self.assert_contains("cit_prec_a1_673", "67.3%")
        self.assert_contains("cit_prec_a2_691", "69.1%")
        self.assert_contains("cit_prec_a3_719", "71.9%")
        self.assert_contains("cit_rec_a1_677", "67.7%")
        self.assert_contains("cit_rec_a2_675", "67.5%")
        self.assert_contains("cit_rec_a3_686", "68.6%")

    def _audit_regime_b_case_study(self):
        self.assert_regex("regb_scan_off_0_8", r"0\s*/\s*8")
        self.assert_regex("regb_scan_def_6_8", r"6\s*/\s*8")
        self.assert_regex("regb_digital_ref_8_8", r"8\s*/\s*8")
        self.assert_contains("regb_scan_def_750", "75.0%")

    def _audit_latencies(self):
        # Arm 1: Ret 193.5 / 360.9, Gen 1738.5 / 10495.8, Tot 2111.6 / 11713.7
        self.assert_contains("lat_a1_ret", "193.5")
        self.assert_contains("lat_a1_gen", "1738.5")
        self.assert_contains("lat_a1_tot", "2111.6")
        self.assert_contains("lat_a2_ret", "209.0")
        self.assert_contains("lat_a2_gen", "2917.0")
        self.assert_contains("lat_a2_tot", "3258.1")
        self.assert_contains("lat_a3_ret", "204.0")
        self.assert_contains("lat_a3_gen", "2058.0")
        self.assert_contains("lat_a3_tot", "2426.7")
        self.assert_contains("lat_regb_ret", "203.0")
        self.assert_contains("lat_regb_gen", "1459.5")
        self.assert_contains("lat_regb_tot", "1746.4")

    def _audit_production_reliability(self):
        self.assert_regex("rel_degraded_16_417", r"16\s*/\s*417")
        self.assert_regex("rel_recovered_16_16", r"16\s*/\s*16")


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
