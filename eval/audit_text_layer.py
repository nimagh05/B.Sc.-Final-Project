#!/usr/bin/env python3
"""
Text-layer quality & CMap defect audit script (ADR 0008 / eval-dataset-v2.2).
Uses the exact per-page NFKC TextDefectDetector criterion (chars >= 300, al >= 10,
la == 0 or la/al < 0.04) plus full-number and number+unit phrase matching against
expected_answer and answer_keywords.

Reproducibility scope:
  - Programmatically reproducible from PDFs: document-level CMap flags (6 PDFs,
    36 Primary Qs), auto_cmap (16 Qs), and screenshot defects (2 Qs on ziro-guide).
  - Visually verified (NOT reproducible by regex alone): the 11 manual-only entries
    in MANUAL_CMAP_V2, confirmed by human inspection of rendered PDF page images.

Usage:
  python eval/audit_text_layer.py            # audit & regenerate eval/text_layer_audit.csv
  python eval/audit_text_layer.py --apply    # also write tags to eval/questions.json (LF)
"""

import argparse
import csv
import hashlib
import json
import re
import unicodedata
from pathlib import Path

import pymupdf

REPO_ROOT = Path(__file__).resolve().parent.parent
QUESTIONS_PATH = REPO_ROOT / "eval" / "questions.json"
PDFS_DIR = REPO_ROOT / "eval" / "pdfs"
AUDIT_CSV_PATH = REPO_ROOT / "eval" / "text_layer_audit.csv"

# Human visual verification set from v2 (27 CMap-document questions checked against page images).
# Note: 16 overlap with auto_cmap; the remaining 11 manual-only items cannot be reproduced by
# automated keyword/number regex alone and are kept hardcoded here for transparent audit logging.
MANUAL_CMAP_V2 = {
    "q011", "q012", "q014", "q022", "q023", "q028", "q032", "q038",
    "q079", "q080", "q081", "q082", "q083", "q084", "q086", "q087",
    "q088", "q091", "q093",
    "q096", "q097", "q099", "q100", "q101", "q102", "q103", "q105",
}

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


def normalize_text(s: str) -> str:
    s = unicodedata.normalize("NFKC", str(s)).lower()
    s = s.translate(DIGIT_MAP).translate(CHAR_MAP)
    s = DIACRITICS_RE.sub("", s)
    s = s.replace("\u200c", " ")
    s = THOUSANDS_RE.sub(lambda m: re.sub(r"[,٬/]", "", m.group(1)), s)
    s = re.sub(r"\s+", " ", s).strip()
    return s


def detect_cmap_flagged_pages(pages: list[str]) -> list[int]:
    """Matches DocSearch TextDefectDetector per-page CorruptLigatureLamAlef rule after NFKC."""
    flagged = []
    for idx, raw in enumerate(pages, 1):
        nfkc = unicodedata.normalize("NFKC", raw)
        chars = len(nfkc.strip())
        la = nfkc.count("لا")
        al = nfkc.count("ال")
        if chars >= 300 and al >= 10 and (la == 0 or (la / al) < 0.04):
            flagged.append(idx)
    return flagged


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--apply", action="store_true", help="Write updated tags to eval/questions.json (LF)")
    args = parser.parse_args()

    doc_pages_raw: dict[str, list[str]] = {}
    doc_cmap_defective: dict[str, bool] = {}

    for pdf_path in sorted(PDFS_DIR.glob("*.pdf")):
        doc = pymupdf.open(pdf_path)
        pages = [p.get_text("text") for p in doc]
        doc.close()
        doc_pages_raw[pdf_path.name] = pages
        flagged_pages = detect_cmap_flagged_pages(pages)
        doc_cmap_defective[pdf_path.name] = len(flagged_pages) > 0

    qs = json.loads(QUESTIONS_PATH.read_text(encoding="utf-8"))
    rows = []
    q_updates: dict[str, tuple[bool, str]] = {}

    for q in qs:
        if q["type"] == "not-in-docs":
            q_updates[q["id"]] = (False, "none")
            continue

        sf = q["source_file"]
        pages = doc_pages_raw[sf]
        raw_page_text = "\n".join(pages[p - 1] for p in q["source_pages"] if 1 <= p <= len(pages))
        ntext = normalize_text(raw_page_text)
        ntext_nospace = ntext.replace(" ", "")
        nans = normalize_text(q["expected_answer"] or "")

        missing_kw = []
        for kw in q["answer_keywords"]:
            nkw = normalize_text(kw)
            if nkw not in ntext and nkw.replace(" ", "") not in ntext_nospace:
                missing_kw.append(kw)

        ans_nums = sorted(set(MULTI_DIGIT_RE.findall(nans)))
        missing_nums = [num for num in ans_nums if not re.search(rf"(?<!\d){re.escape(num)}(?!\d)", ntext)]

        unit_phrases = [f"{m[0]} {m[1]}" for m in UNIT_PHRASE_RE.findall(nans)]
        missing_phrases = []
        for ph in unit_phrases:
            num_part, unit_part = ph.split(" ", 1)
            if not re.search(rf"(?<!\d){re.escape(num_part)}\s*{re.escape(unit_part)}", ntext):
                alt = None
                if unit_part == "درصد":
                    alt = rf"(?<!\d){re.escape(num_part)}\s*%"
                elif unit_part == "%":
                    alt = rf"(?<!\d){re.escape(num_part)}\s*درصد"
                if not (alt and re.search(alt, ntext)):
                    missing_phrases.append(ph)

        is_doc_cmap = doc_cmap_defective.get(sf, False)
        is_screenshot_doc = (sf == "ziro-guide.pdf")

        auto_signal = bool(missing_kw or missing_nums or missing_phrases)
        auto_cmap = is_doc_cmap and auto_signal
        manual_cmap = q["id"] in MANUAL_CMAP_V2
        union_cmap = manual_cmap or auto_cmap
        screenshot_flag = is_screenshot_doc and bool(missing_kw)

        if screenshot_flag:
            mechanism = "screenshot"
            final_defect = True
        elif union_cmap:
            mechanism = "cmap"
            final_defect = True
        else:
            mechanism = "none"
            final_defect = False

        q_updates[q["id"]] = (final_defect, mechanism)

        rows.append({
            "id": q["id"],
            "docset": q["docset"],
            "split": q["split"],
            "type": q["type"],
            "source_file": sf,
            "source_pages": ",".join(str(p) for p in q["source_pages"]),
            "doc_cmap_defective": is_doc_cmap,
            "manual_cmap_v2": manual_cmap,
            "auto_cmap": auto_cmap,
            "union_cmap": union_cmap,
            "screenshot_defect": screenshot_flag,
            "defect_mechanism": mechanism,
            "missing_keywords": "; ".join(missing_kw),
            "missing_full_numbers": "; ".join(missing_nums),
            "missing_num_units": "; ".join(missing_phrases),
        })

    with open(AUDIT_CSV_PATH, "w", encoding="utf-8", newline="\n") as f:
        writer = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        writer.writeheader()
        writer.writerows(rows)

    print(f"Regenerated {AUDIT_CSV_PATH} ({len(rows)} answerable rows, partial_ratio removed).")

    if args.apply:
        for q in qs:
            defect_val, mech_val = q_updates[q["id"]]
            q["text_layer_defect"] = defect_val
            q["defect_mechanism"] = mech_val
        json_lf = (json.dumps(qs, ensure_ascii=False, indent=2) + "\n").replace("\r\n", "\n")
        QUESTIONS_PATH.write_bytes(json_lf.encode("utf-8"))
        sha = hashlib.sha256(QUESTIONS_PATH.read_bytes()).hexdigest()
        print(f"Applied tags to {QUESTIONS_PATH} | LF SHA-256: {sha}")


if __name__ == "__main__":
    main()
