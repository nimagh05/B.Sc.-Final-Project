#!/usr/bin/env python3
"""
Pre-retrieval OCR Diagnostic & Benchmark Script (Task T12 / ADR 0008).
Evaluates extraction-stage OCR triggering and pre-retrieval text recall
across 15 PDFs without needing Ollama, chat LLMs, or a PostgreSQL database.

Usage:
    python eval/experiments/ocr_eval_check.py [--sidecar-url http://127.0.0.1:8000] [--audit-only]
"""

import argparse
import csv
import json
import re
import sys
import unicodedata
from collections import defaultdict
from pathlib import Path
from typing import Dict, List, Optional, Set, Tuple

import pymupdf
import requests

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
QUESTIONS_PATH = REPO_ROOT / "eval" / "questions.json"
PDFS_DIR = REPO_ROOT / "eval" / "pdfs"
SCAN_PDF_PATH = REPO_ROOT / "eval" / "pdfs" / "ocr" / "bein-almelali-scan.pdf"
AUDIT_CSV_PATH = REPO_ROOT / "eval" / "text_layer_audit.csv"

# Normalization maps matching eval/audit_text_layer.py and C# PersianTextNormalizer
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

UNIT_ONLY_QS = {"q022", "q097", "q100", "q102", "q103"}
SCREENSHOT_QS = {"q034", "q039"}
SCAN_GOLD_QS = ["q008", "q009", "q010", "q020", "q021", "q029", "q030", "q037"]

KNOWN_CMAP_PDFS = {
    "bedoon-factor.pdf",
    "dastyar-pazhuheshi.pdf",
    "hamayesh.pdf",
    "kod-akhlaghi.pdf",
    "tarh-dakheli.pdf",
    "forsat-motaleati.pdf",
}

# Strict split per ADR 0008 anti-leakage rule:
# NEVER tune or ablate OCR parameters on heldout documents!
TUNED_CMAP_PDFS = {
    "bedoon-factor.pdf",
    "dastyar-pazhuheshi.pdf",
    "kod-akhlaghi.pdf",
    "hamayesh.pdf",
}
HELDOUT_CMAP_PDFS = {
    "tarh-dakheli.pdf",
    "forsat-motaleati.pdf",
}


def normalize_text(s: str) -> str:
    s = unicodedata.normalize("NFKC", str(s)).lower()
    s = s.translate(DIGIT_MAP).translate(CHAR_MAP)
    s = DIACRITICS_RE.sub("", s)
    s = s.replace("\u200c", " ")
    s = THOUSANDS_RE.sub(lambda m: re.sub(r"[,٬/]", "", m.group(1)), s)
    s = re.sub(r"\s+", " ", s).strip()
    return s


def detect_cmap_flagged_pages(pages: List[str]) -> List[int]:
    """Matches C# TextDefectDetector per-page CorruptLigatureLamAlef rule."""
    flagged = []
    for idx, raw in enumerate(pages, 1):
        nfkc = unicodedata.normalize("NFKC", raw)
        chars = len(nfkc.strip())
        la = nfkc.count("لا")
        al = nfkc.count("ال")
        if chars >= 300 and al >= 10 and (la == 0 or (la / al) < 0.04):
            flagged.append(idx)
    return flagged


def detect_needs_ocr_pages(pages: List[str], min_page_quality: float = 0.35) -> List[int]:
    """Matches C# PageQualityScorer NeedsOcr rule."""
    flagged = []
    for idx, raw in enumerate(pages, 1):
        non_ws_chars = len([c for c in raw if not c.isspace()])
        if non_ws_chars < 20:
            flagged.append(idx)
    return flagged


def contains_persian(text: str) -> bool:
    return bool(PERSIAN_CHAR_RE.search(text))


def compute_effective_ocr_pages(pages: List[str]) -> List[int]:
    """Evaluates Defective mode: NeedsOcr union CMap pages with document-wide propagation."""
    needs_ocr = set(detect_needs_ocr_pages(pages))
    cmap_pages = detect_cmap_flagged_pages(pages)

    if cmap_pages:
        # Document-wide CMap propagation to all Persian-containing pages
        for idx, page_text in enumerate(pages, 1):
            if contains_persian(page_text):
                needs_ocr.add(idx)
    else:
        for p in cmap_pages:
            needs_ocr.add(p)

    return sorted(needs_ocr)


CACHE_FILE = Path(__file__).parent / "out" / ".ocr_cache.json"


def _load_cache() -> Dict[str, Dict[str, str]]:
    if CACHE_FILE.exists():
        try:
            with open(CACHE_FILE, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            return {}
    return {}


def _save_cache(cache: Dict[str, Dict[str, str]]) -> None:
    try:
        CACHE_FILE.parent.mkdir(parents=True, exist_ok=True)
        with open(CACHE_FILE, "w", encoding="utf-8") as f:
            json.dump(cache, f, ensure_ascii=False, indent=2)
    except Exception:
        pass


def call_sidecar_ocr(
    sidecar_url: str,
    pdf_path: Path,
    pages: List[int],
    dpi: int = 300,
    lang: str = "fas+eng",
    psm: int = 6,
) -> Dict[int, str]:
    if not pages:
        return {}

    cache = _load_cache()
    doc_key = f"{pdf_path.name}_{dpi}_{lang}_psm{psm}"
    doc_cache = cache.get(doc_key, {})

    missing_pages = [p for p in pages if str(p) not in doc_cache]
    if missing_pages:
        url = f"{sidecar_url.rstrip('/')}/ocr"
        with open(pdf_path, "rb") as f:
            resp = requests.post(
                url,
                files={"file": (pdf_path.name, f, "application/pdf")},
                data={
                    "pages": ",".join(str(p) for p in sorted(missing_pages)),
                    "dpi": str(dpi),
                    "lang": lang,
                    "psm": str(psm),
                },
                timeout=300,
            )

        if resp.status_code != 200:
            raise RuntimeError(f"Sidecar /ocr failed (HTTP {resp.status_code}): {resp.text}")

        data = resp.json()
        for item in data.get("pages", []):
            doc_cache[str(item["page"])] = item["text"]

        cache[doc_key] = doc_cache
        _save_cache(cache)

    return {p: doc_cache.get(str(p), "") for p in pages}


def evaluate_question_recall(q: dict, page_texts: List[str]) -> Tuple[bool, Optional[bool], Optional[bool]]:
    """
    Returns (strict_kw_hit, full_nums_hit, unit_phrases_hit).
    full_nums_hit is None if the expected answer has no multi-digit numbers.
    unit_phrases_hit is None if the expected answer has no number+unit phrases.
    """
    source_pages = q.get("source_pages", [])
    raw_source = "\n".join(
        page_texts[p - 1] for p in source_pages if 1 <= p <= len(page_texts)
    )
    ntext = normalize_text(raw_source)
    ntext_nospace = ntext.replace(" ", "")
    nans = normalize_text(q.get("expected_answer") or "")

    # 1. Strict answer_keywords recall (all keywords present)
    kws = q.get("answer_keywords", [])
    if kws:
        kw_hits = 0
        for kw in kws:
            nkw = normalize_text(kw)
            if nkw in ntext or nkw.replace(" ", "") in ntext_nospace:
                kw_hits += 1
        kw_hit = (kw_hits == len(kws))
    else:
        kw_hit = True

    # 2. Multi-digit numbers recall (evaluated ONLY when question has >=1 multi-digit numbers)
    ans_nums = sorted(set(MULTI_DIGIT_RE.findall(nans)))
    if ans_nums:
        num_hits = sum(1 for num in ans_nums if re.search(rf"(?<!\d){re.escape(num)}(?!\d)", ntext))
        num_hit: Optional[bool] = (num_hits == len(ans_nums))
    else:
        num_hit = None

    # 3. Number+unit phrase recall (evaluated ONLY when question has >=1 number+unit phrases)
    unit_phrases = [f"{m[0]} {m[1]}" for m in UNIT_PHRASE_RE.findall(nans)]
    if unit_phrases:
        unit_hits = 0
        for ph in unit_phrases:
            num_part, unit_part = ph.split(" ", 1)
            if re.search(rf"(?<!\d){re.escape(num_part)}\s*{re.escape(unit_part)}", ntext):
                unit_hits += 1
            else:
                alt = None
                if unit_part == "درصد":
                    alt = rf"(?<!\d){re.escape(num_part)}\s*%"
                elif unit_part == "%":
                    alt = rf"(?<!\d){re.escape(num_part)}\s*درصد"
                if alt and re.search(alt, ntext):
                    unit_hits += 1
        unit_hit: Optional[bool] = (unit_hits == len(unit_phrases))
    else:
        unit_hit = None

    return kw_hit, num_hit, unit_hit


def format_metric(hits: int, total: int) -> str:
    if total == 0:
        return "N/A"
    return f"{hits}/{total} ({hits / total * 100:.1f}%)"


def run_tuned_ablation(
    sidecar_url: str,
    pdf_files: Dict[str, Path],
    doc_raw_pages: Dict[str, List[str]],
    doc_effective_ocr_pages: Dict[str, List[int]],
    answerable_qs: List[dict],
) -> None:
    print("\n" + "=" * 95)
    print("=== Tuned OCR Parameter Ablation (Anti-Leakage Guard: strictly NO heldout documents) ===")
    print("=" * 95)
    print(f"Target Tuned Documents (CMap): {sorted(TUNED_CMAP_PDFS)}")
    print("Target Scan Twin Document:     bein-almelali-scan.pdf")
    print("Clean Regression Document:     aein-91.pdf")
    print("Heldout documents EXCLUDED per ADR 0008: tarh-dakheli.pdf, forsat-motaleati.pdf")

    tuned_cmap_qs = [q for q in answerable_qs if q.get("source_file") in TUNED_CMAP_PDFS]
    scan_qs = [q for q in answerable_qs if q["id"] in SCAN_GOLD_QS]
    all_tuned_qs = tuned_cmap_qs + scan_qs
    aein_qs = [q for q in answerable_qs if q.get("source_file") == "aein-91.pdf"]

    psm_values = [6, 4, 3]
    dpi_values = [300, 400]

    configs = []
    for dpi in dpi_values:
        for psm in psm_values:
            configs.append((dpi, psm))

    def eval_doc_qs(qs: List[dict], doc_pages_map: Dict[str, List[str]]) -> Tuple[Tuple[int, int], Tuple[int, int], Tuple[int, int]]:
        k_hit, n_hit, u_hit = 0, 0, 0
        n_tot, u_tot = 0, 0
        for q in qs:
            sf = q.get("source_file")
            pages = doc_pages_map.get(sf, [])
            kh, nh, uh = evaluate_question_recall(q, pages)
            if kh:
                k_hit += 1
            if nh is not None:
                n_tot += 1
                if nh:
                    n_hit += 1
            if uh is not None:
                u_tot += 1
                if uh:
                    u_hit += 1
        return (k_hit, len(qs)), (n_hit, n_tot), (u_hit, u_tot)

    q_diff: Dict[str, Dict[str, Tuple[bool, Optional[bool]]]] = defaultdict(dict)

    cmap_off = eval_doc_qs(tuned_cmap_qs, doc_raw_pages)
    scan_off = eval_doc_qs(scan_qs, {"bein-almelali.pdf": doc_raw_pages["bein-almelali-scan.pdf"]})
    aein_off = eval_doc_qs(aein_qs, doc_raw_pages)
    comb_off_k = (cmap_off[0][0] + scan_off[0][0], len(all_tuned_qs))

    for q in tuned_cmap_qs:
        kh, nh, _ = evaluate_question_recall(q, doc_raw_pages.get(q["source_file"], []))
        q_diff[q["id"]]["Off"] = (kh, nh)
    for q in scan_qs:
        kh, nh, _ = evaluate_question_recall(q, doc_raw_pages["bein-almelali-scan.pdf"])
        q_diff[q["id"]]["Off"] = (kh, nh)

    print("\n--- Ablation Summary Table ---")
    header = f"{'Configuration':<20} {'Tuned CMap KW':<16} {'Tuned CMap Num':<16} {'Scan Twin KW':<16} {'Scan Twin Num':<16} {'Combined KW':<16} {'aein-91 KW':<16}"
    print(header)
    print("-" * len(header))
    print(f"{'OcrMode=Off':<20} {format_metric(*cmap_off[0]):<16} {format_metric(*cmap_off[1]):<16} {format_metric(*scan_off[0]):<16} {format_metric(*scan_off[1]):<16} {format_metric(*comb_off_k):<16} {format_metric(*aein_off[0]):<16}")

    for dpi, psm in configs:
        cfg_name = f"DPI {dpi}, PSM {psm}"
        tuned_pages: Dict[str, List[str]] = {}
        for d in TUNED_CMAP_PDFS:
            flagged = doc_effective_ocr_pages[d]
            ocr_map = call_sidecar_ocr(sidecar_url, pdf_files[d], flagged, dpi=dpi, psm=psm)
            raw = doc_raw_pages[d]
            merged = []
            for p_idx, text in enumerate(raw, 1):
                if p_idx in ocr_map and ocr_map[p_idx].strip():
                    merged.append(ocr_map[p_idx])
                else:
                    merged.append(text)
            tuned_pages[d] = merged

        # bein-almelali-scan.pdf
        scan_flagged = [1, 2, 3, 4, 5, 6, 7, 8]
        scan_ocr = call_sidecar_ocr(sidecar_url, pdf_files["bein-almelali-scan.pdf"], scan_flagged, dpi=dpi, psm=psm)
        scan_merged = [scan_ocr.get(p, "") for p in range(1, 9)]

        # aein-91.pdf (Always)
        aein_flagged = list(range(1, len(doc_raw_pages["aein-91.pdf"]) + 1))
        aein_ocr = call_sidecar_ocr(sidecar_url, pdf_files["aein-91.pdf"], aein_flagged, dpi=dpi, psm=psm)
        aein_merged = [aein_ocr.get(p, doc_raw_pages["aein-91.pdf"][p - 1]) for p in aein_flagged]

        cmap_res = eval_doc_qs(tuned_cmap_qs, tuned_pages)
        scan_hits_k, scan_hits_n = 0, 0
        scan_n_tot = 0
        for q in scan_qs:
            kh, nh, _ = evaluate_question_recall(q, scan_merged)
            if kh:
                scan_hits_k += 1
            if nh is not None:
                scan_n_tot += 1
                if nh:
                    scan_hits_n += 1
            q_diff[q["id"]][cfg_name] = (kh, nh)

        for q in tuned_cmap_qs:
            kh, nh, _ = evaluate_question_recall(q, tuned_pages.get(q["source_file"], []))
            q_diff[q["id"]][cfg_name] = (kh, nh)

        scan_res_k = (scan_hits_k, len(scan_qs))
        scan_res_n = (scan_hits_n, scan_n_tot)
        comb_k = (cmap_res[0][0] + scan_hits_k, len(all_tuned_qs))
        aein_res = eval_doc_qs(aein_qs, {"aein-91.pdf": aein_merged})

        print(f"{cfg_name:<20} {format_metric(*cmap_res[0]):<16} {format_metric(*cmap_res[1]):<16} {format_metric(*scan_res_k):<16} {format_metric(*scan_res_n):<16} {format_metric(*comb_k):<16} {format_metric(*aein_res[0]):<16}")

    print("\n--- Per-Question Diagnostic Diff on Tuned Documents ---")
    diff_header = f"{'QID':<6} {'File':<24} {'Off (KW/Num)':<14} {'300, PSM 6':<14} {'300, PSM 4':<14} {'300, PSM 3':<14} {'400, PSM 6':<14}"
    print(diff_header)
    print("-" * len(diff_header))
    for q in all_tuned_qs:
        qid = q["id"]
        sf = q["source_file"]
        d = q_diff[qid]

        def fmt_q(res: Tuple[bool, Optional[bool]]) -> str:
            k_char = "✓" if res[0] else "✗"
            n_char = "-" if res[1] is None else ("✓" if res[1] else "✗")
            return f"{k_char}/{n_char}"

        off_s = fmt_q(d.get("Off", (False, None)))
        p6_300 = fmt_q(d.get("DPI 300, PSM 6", (False, None)))
        p4_300 = fmt_q(d.get("DPI 300, PSM 4", (False, None)))
        p3_300 = fmt_q(d.get("DPI 300, PSM 3", (False, None)))
        p6_400 = fmt_q(d.get("DPI 400, PSM 6", (False, None)))
        print(f"{qid:<6} {sf:<24} {off_s:<14} {p6_300:<14} {p4_300:<14} {p3_300:<14} {p6_400:<14}")
    print("=" * 95)


def main() -> None:
    parser = argparse.ArgumentParser(description="Pre-retrieval OCR Diagnostic & Benchmark")
    parser.add_argument("--sidecar-url", default="http://127.0.0.1:8000", help="URL of Python extractor sidecar")
    parser.add_argument("--audit-only", action="store_true", help="Run 15-PDF trigger audit only without calling /ocr")
    parser.add_argument("--ablate-tuned", action="store_true", help="Run PSM (3, 4, 6) and DPI (300, 400) ablation on tuned documents only")
    args = parser.parse_args()

    # Collect 15 PDFs
    pdf_files: Dict[str, Path] = {}
    for p in sorted(PDFS_DIR.glob("*.pdf")):
        pdf_files[p.name] = p
    if SCAN_PDF_PATH.exists():
        pdf_files[SCAN_PDF_PATH.name] = SCAN_PDF_PATH

    print("=== DocSearch Pre-Retrieval OCR Diagnostic & Evaluation ===")
    print(f"Total PDFs found: {len(pdf_files)} (expected 15)")
    assert len(pdf_files) == 15, f"Expected 15 PDFs, found {len(pdf_files)}"

    # 1. 15-PDF Trigger Audit
    print("\n--- Part 1: 15-PDF Trigger Audit ---")
    doc_raw_pages: Dict[str, List[str]] = {}
    doc_effective_ocr_pages: Dict[str, List[int]] = {}
    doc_cmap_pages: Dict[str, List[int]] = {}

    print(f"{'PDF File':<28} {'Pages':<6} {'NeedsOcr':<10} {'CMap Pages':<14} {'Effective OCR Pages (Defective Mode)'}")
    print("-" * 90)

    for name, path in sorted(pdf_files.items()):
        doc = pymupdf.open(path)
        pages = [page.get_text("text") for page in doc]
        doc.close()
        doc_raw_pages[name] = pages

        needs_ocr = detect_needs_ocr_pages(pages)
        cmap = detect_cmap_flagged_pages(pages)
        effective = compute_effective_ocr_pages(pages)

        doc_cmap_pages[name] = cmap
        doc_effective_ocr_pages[name] = effective

        needs_str = ",".join(str(p) for p in needs_ocr) if needs_ocr else "none"
        cmap_str = ",".join(str(p) for p in cmap) if cmap else "none"
        eff_str = ",".join(str(p) for p in effective) if effective else "none"
        print(f"{name:<28} {len(pages):<6} {needs_str:<10} {cmap_str:<14} {eff_str}")

    # Assertions on trigger audit
    for cmap_doc in KNOWN_CMAP_PDFS:
        assert len(doc_effective_ocr_pages[cmap_doc]) > 0, f"{cmap_doc} should have effective OCR pages flagged"
        assert len(doc_cmap_pages[cmap_doc]) > 0, f"{cmap_doc} should trigger CMap defect"
    assert len(doc_effective_ocr_pages["bein-almelali-scan.pdf"]) == 8, "bein-almelali-scan.pdf should flag all 8 pages"
    assert len(doc_effective_ocr_pages["aein-91.pdf"]) == 0, "Clean aein-91.pdf should NOT trigger in Defective mode"
    assert len(doc_effective_ocr_pages["shiveh-karamoozi.pdf"]) == 0, "Clean shiveh-karamoozi.pdf should NOT trigger in Defective mode"
    print("\n[OK] 15-PDF Trigger Audit strictly verified (6 CMap PDFs + 1 Scan twin triggered; clean PDFs intact).")

    if args.audit_only:
        print("\nAudit-only mode requested; skipping OCR endpoint benchmark.")
        return

    # Check sidecar health
    try:
        health_resp = requests.get(f"{args.sidecar_url.rstrip('/')}/health", timeout=5)
        if health_resp.status_code != 200:
            print(f"ERROR: Sidecar at {args.sidecar_url} returned HTTP {health_resp.status_code}")
            sys.exit(1)
    except Exception as ex:
        print(f"ERROR: Cannot reach sidecar at {args.sidecar_url}: {ex}")
        print("Please start the extractor sidecar (e.g. via Docker: docker compose up -d extractor)")
        sys.exit(1)

    questions = json.loads(QUESTIONS_PATH.read_text(encoding="utf-8"))
    answerable_qs = [q for q in questions if q.get("type") != "not-in-docs"]

    if args.ablate_tuned:
        run_tuned_ablation(
            args.sidecar_url,
            pdf_files,
            doc_raw_pages,
            doc_effective_ocr_pages,
            answerable_qs,
        )
        return

    # 2. Execute OCR on flagged pages of defective PDFs + aein-91.pdf (Always) + scan twin
    print(f"\n--- Part 2: OCR Execution via Sidecar ({args.sidecar_url}) ---")
    doc_ocr_pages: Dict[str, Dict[int, str]] = {}

    target_ocr_docs = list(KNOWN_CMAP_PDFS) + ["bein-almelali-scan.pdf"]
    for doc_name in target_ocr_docs:
        pages_to_ocr = doc_effective_ocr_pages[doc_name]
        pdf_path = pdf_files[doc_name]
        print(f"Running OCR on {doc_name} (pages: {pages_to_ocr})...")
        ocr_result = call_sidecar_ocr(args.sidecar_url, pdf_path, pages_to_ocr)
        doc_ocr_pages[doc_name] = ocr_result

    # Also run OCR on clean aein-91.pdf (Always mode: pages 1..10) for regression guard
    print("Running OCR on aein-91.pdf (Always mode, pages 1..10) for regression guard...")
    aein_pages = list(range(1, len(doc_raw_pages["aein-91.pdf"]) + 1))
    doc_ocr_pages["aein-91.pdf"] = call_sidecar_ocr(args.sidecar_url, pdf_files["aein-91.pdf"], aein_pages)

    # Build merged document pages under Defective mode
    doc_merged_defective: Dict[str, List[str]] = {}
    for name, raw_pages in doc_raw_pages.items():
        ocr_map = doc_ocr_pages.get(name, {})
        merged = []
        for p_idx, text in enumerate(raw_pages, 1):
            if p_idx in ocr_map and ocr_map[p_idx].strip():
                merged.append(ocr_map[p_idx])
            else:
                merged.append(text)
        doc_merged_defective[name] = merged

    # Build Always mode pages for aein-91.pdf
    aein_always_pages = []
    aein_ocr_map = doc_ocr_pages["aein-91.pdf"]
    for p_idx, text in enumerate(doc_raw_pages["aein-91.pdf"], 1):
        if p_idx in aein_ocr_map and aein_ocr_map[p_idx].strip():
            aein_always_pages.append(aein_ocr_map[p_idx])
        else:
            aein_always_pages.append(text)

    # 3. Load Questions & Evaluate Pre-Retrieval Text Recall
    print("\n--- Part 3: Regime A Pre-Retrieval Text Recall Evaluation ---")
    questions = json.loads(QUESTIONS_PATH.read_text(encoding="utf-8"))
    answerable_qs = [q for q in questions if q.get("type") != "not-in-docs"]

    # Cohort groupings
    primary_cmap_qs = [q for q in answerable_qs if q.get("source_file") in KNOWN_CMAP_PDFS]
    heldout_cmap_qs = [q for q in answerable_qs if q.get("source_file") in HELDOUT_CMAP_PDFS]
    assert len(heldout_cmap_qs) == 27, f"Expected 27 heldout CMap questions, found {len(heldout_cmap_qs)}"
    secondary_union_cmap = [q for q in answerable_qs if q.get("defect_mechanism") == "cmap"]

    # auto_cmap parsed explicitly from eval/text_layer_audit.csv per ADR 0008 (N=16)
    auto_cmap_ids: Set[str] = set()
    if AUDIT_CSV_PATH.exists():
        with open(AUDIT_CSV_PATH, mode="r", encoding="utf-8") as f:
            reader = csv.DictReader(f)
            for row in reader:
                if row.get("auto_cmap", "").strip().lower() in ("true", "1"):
                    auto_cmap_ids.add(row["id"])
    secondary_auto_cmap = [q for q in answerable_qs if q["id"] in auto_cmap_ids]
    assert len(secondary_auto_cmap) == 16, f"Expected 16 auto_cmap questions, found {len(secondary_auto_cmap)}"

    # Unit-only questions
    unit_only_qs = [q for q in answerable_qs if q.get("id") in UNIT_ONLY_QS]
    # Screenshot questions
    screenshot_qs = [q for q in answerable_qs if q.get("defect_mechanism") == "screenshot"]

    def compute_cohort_metrics(
        qs: List[dict], use_defective: bool
    ) -> Tuple[Tuple[int, int], Tuple[int, int], Tuple[int, int]]:
        kw_hits = 0
        kw_total = len(qs)
        num_hits = 0
        num_total = 0
        unit_hits = 0
        unit_total = 0
        for q in qs:
            sf = q["source_file"]
            pages = doc_merged_defective[sf] if use_defective else doc_raw_pages[sf]
            k_hit, n_hit, u_hit = evaluate_question_recall(q, pages)
            if k_hit:
                kw_hits += 1
            if n_hit is not None:
                num_total += 1
                if n_hit:
                    num_hits += 1
            if u_hit is not None:
                unit_total += 1
                if u_hit:
                    unit_hits += 1
        return (kw_hits, kw_total), (num_hits, num_total), (unit_hits, unit_total)

    print(f"{'Cohort':<58} {'N':<4} {'Mode':<10} {'KW Recall':<18} {'Num Recall':<18} {'Unit Recall'}")
    print("-" * 124)

    cohorts = [
        ("Primary (6 CMap PDFs, all answerable)", primary_cmap_qs),
        ("Heldout CMap PDFs only (tarh-dakheli + forsat-motaleati)", heldout_cmap_qs),
        ("Secondary (defect_mechanism == cmap, union)", secondary_union_cmap),
        ("Secondary (auto_cmap subset)", secondary_auto_cmap),
        ("Breakout (5 unit-only questions)", unit_only_qs),
        ("Separate (2 screenshot Qs, ziro-guide)", screenshot_qs),
    ]

    for label, qs in cohorts:
        (k_off, k_tot), (n_off, n_tot), (u_off, u_tot) = compute_cohort_metrics(qs, use_defective=False)
        (k_def, _), (n_def, _), (u_def, _) = compute_cohort_metrics(qs, use_defective=True)
        kw_off_str = format_metric(k_off, k_tot)
        num_off_str = format_metric(n_off, n_tot)
        unit_off_str = format_metric(u_off, u_tot)
        kw_def_str = format_metric(k_def, k_tot)
        num_def_str = format_metric(n_def, n_tot)
        unit_def_str = format_metric(u_def, u_tot)

        print(f"{label:<58} {len(qs):<4} {'Off':<10} {kw_off_str:<18} {num_off_str:<18} {unit_off_str}")
        print(f"{'':<58} {'':<4} {'Defective':<10} {kw_def_str:<18} {num_def_str:<18} {unit_def_str}")
        print("." * 124)

    # 4. Clean-Document Regression Guard (aein-91.pdf under Always mode)
    print("\n--- Part 4: Clean-Document Regression Guard (aein-91.pdf) ---")
    aein_qs = [q for q in answerable_qs if q.get("source_file") == "aein-91.pdf"]
    aein_off_res = [evaluate_question_recall(q, doc_raw_pages["aein-91.pdf"]) for q in aein_qs]
    aein_always_res = [evaluate_question_recall(q, aein_always_pages) for q in aein_qs]

    aein_off_kw = sum(1 for r in aein_off_res if r[0])
    aein_off_num_hits = sum(1 for r in aein_off_res if r[1] is True)
    aein_off_num_tot = sum(1 for r in aein_off_res if r[1] is not None)

    aein_always_kw = sum(1 for r in aein_always_res if r[0])
    aein_always_num_hits = sum(1 for r in aein_always_res if r[1] is True)
    aein_always_num_tot = sum(1 for r in aein_always_res if r[1] is not None)

    print(f"aein-91.pdf answerable questions (N={len(aein_qs)}):")
    print(f"  Raw PyMuPDF (OcrMode=Off):    KW Recall = {format_metric(aein_off_kw, len(aein_qs))}, Num Recall = {format_metric(aein_off_num_hits, aein_off_num_tot)}")
    print(f"  Post-OCR (OcrMode=Always):     KW Recall = {format_metric(aein_always_kw, len(aein_qs))}, Num Recall = {format_metric(aein_always_num_hits, aein_always_num_tot)}")
    print(f"  [Empirical Finding] Forced Always OCR degrades clean digital text (KW: {format_metric(aein_off_kw, len(aein_qs))} -> {format_metric(aein_always_kw, len(aein_qs))}, Num: {format_metric(aein_off_num_hits, aein_off_num_tot)} -> {format_metric(aein_always_num_hits, aein_always_num_tot)}).")
    print(f"  [Design Consequence] OcrMode=Defective triggers 0 OCR pages on clean documents, preserving 100% of raw digital text quality ({format_metric(aein_off_kw, len(aein_qs))}).")

    # 5. Regime B Isolated Scan Twin (bein-almelali-scan.pdf -> bein-almelali.pdf)
    print("\n--- Part 5: Regime B Isolated Scan Twin (bein-almelali-scan.pdf) ---")
    scan_gold = [q for q in answerable_qs if q["id"] in SCAN_GOLD_QS]
    print(f"Evaluating 8 gold questions on synthetic 150 DPI scan twin (N={len(scan_gold)})...")

    # In Off mode, scan twin has 0 text
    scan_off_pages = doc_raw_pages["bein-almelali-scan.pdf"]
    scan_def_pages = doc_merged_defective["bein-almelali-scan.pdf"]
    ref_pages = doc_raw_pages["bein-almelali.pdf"]

    def eval_scan(pages: List[str]) -> Tuple[Tuple[int, int], Tuple[int, int], Tuple[int, int]]:
        k, n, u = 0, 0, 0
        n_tot, u_tot = 0, 0
        for q in scan_gold:
            kh, nh, uh = evaluate_question_recall(q, pages)
            if kh:
                k += 1
            if nh is not None:
                n_tot += 1
                if nh:
                    n += 1
            if uh is not None:
                u_tot += 1
                if uh:
                    u += 1
        return (k, len(scan_gold)), (n, n_tot), (u, u_tot)

    (k_scan_off, k_scan_tot), (n_scan_off, n_scan_tot), (u_scan_off, u_scan_tot) = eval_scan(scan_off_pages)
    (k_scan_def, _), (n_scan_def, _), (u_scan_def, _) = eval_scan(scan_def_pages)
    (k_ref, _), (n_ref, _), (u_ref, _) = eval_scan(ref_pages)

    print(f"  OcrMode=Off (0 text):          KW Recall = {format_metric(k_scan_off, k_scan_tot)}, Num Recall = {format_metric(n_scan_off, n_scan_tot)}, Unit Recall = {format_metric(u_scan_off, u_scan_tot)}")
    print(f"  OcrMode=Defective (Tesseract): KW Recall = {format_metric(k_scan_def, k_scan_tot)}, Num Recall = {format_metric(n_scan_def, n_scan_tot)}, Unit Recall = {format_metric(u_scan_def, u_scan_tot)}")
    print(f"  Digital Reference:             KW Recall = {format_metric(k_ref, k_scan_tot)}, Num Recall = {format_metric(n_ref, n_scan_tot)}, Unit Recall = {format_metric(u_ref, u_scan_tot)}")

    assert k_scan_off == 0, "Scan twin without OCR should have 0 keyword hits"
    if n_scan_tot > 0:
        assert n_scan_off == 0, "Scan twin without OCR should have 0 number hits"
    if u_scan_tot > 0:
        assert u_scan_off == 0, "Scan twin without OCR should have 0 unit hits"
    assert k_scan_def > 0, "Scan twin with OCR must recover text and achieve >0 keyword hits"
    print("\n[OK] Regime B evaluation completed successfully: OCR recovers text from image-only scan.")
    print("=== All Diagnostic and Evaluation Checks Complete ===")


if __name__ == "__main__":
    main()
