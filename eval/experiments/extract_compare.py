#!/usr/bin/env python3
"""
Compares PDF extraction across PyMuPDF, pdfplumber, and PdfPig.
Computes text quality metrics per page according to SPEC-PERSIAN-AND-RAG.md §2.
Outputs:
  - eval/experiments/out/extract/<lib>/<pdf>/<page>.txt
  - eval/experiments/out/extract_summary.csv
  - eval/experiments/out/extract_samples.md
"""

import csv
import random
import re
import subprocess
import sys
import time
from pathlib import Path

import pdfplumber
import pymupdf

FORWARD_WORDS = ["که", "را", "در", "از", "این", "با", "است"]
REVERSED_WORDS = ["هک", "ار", "رد", "زا", "نیا", "اب", "تسا"]


def compute_metrics(text: str, seconds: float):
    chars = 0
    letters = 0
    presentation = 0
    replacements = 0
    persian = 0

    for c in text:
        if c.isspace():
            continue
        chars += 1
        code = ord(c)
        if c.isalpha():
            letters += 1
            if (
                (0x0600 <= code <= 0x06FF)
                or (0x0750 <= code <= 0x077F)
                or (0xFB50 <= code <= 0xFDFF)
                or (0xFE70 <= code <= 0xFEFF)
            ):
                persian += 1

        if (0xFB50 <= code <= 0xFDFF) or (0xFE70 <= code <= 0xFEFF):
            presentation += 1
        if c == "\uFFFD":
            replacements += 1

    letter_ratio = (letters / chars) if chars > 0 else 0.0
    persian_ratio = (persian / letters) if letters > 0 else 0.0
    presentation_ratio = (presentation / chars) if chars > 0 else 0.0
    replacement_ratio = (replacements / chars) if chars > 0 else 0.0

    tokens = re.split(r"\s+", text)
    forward_count = 0
    reversed_count = 0
    for token in tokens:
        clean = re.sub(r"[^\w]", "", token)
        if not clean:
            continue
        if clean in FORWARD_WORDS:
            forward_count += 1
        if clean in REVERSED_WORDS:
            reversed_count += 1

    looks_reversed = (reversed_count > 1.5 * forward_count) and (reversed_count >= 5)

    quality = 1.0
    if chars < 20:
        quality = 0.0
    else:
        if letter_ratio < 0.5:
            quality -= 0.4
        if replacement_ratio > 0.01:
            quality -= 0.4
        if presentation_ratio > 0.2:
            quality -= 0.2
        if looks_reversed:
            quality -= 0.5
        quality = max(0.0, min(1.0, quality))

    empty_page = chars < 20

    return {
        "chars": chars,
        "letterRatio": round(letter_ratio, 4),
        "persianRatio": round(persian_ratio, 4),
        "presentationRatio": round(presentation_ratio, 4),
        "replacementRatio": round(replacement_ratio, 4),
        "looksReversed": looks_reversed,
        "quality": round(quality, 4),
        "empty_page": empty_page,
        "seconds": round(seconds, 4),
    }


def run_pdfpig(repo_root: Path):
    print("--- Step 1: Running PdfPig Probe (C#) ---")
    probe_proj = repo_root / "eval" / "experiments" / "pdfpig_probe" / "PdfPigProbe.csproj"
    cmd = ["dotnet", "run", "--project", str(probe_proj), "-c", "Release"]
    res = subprocess.run(cmd, cwd=str(repo_root), capture_output=True, text=True)
    if res.returncode != 0:
        print(f"Warning: PdfPigProbe returned non-zero exit code: {res.stderr}")
    else:
        print("PdfPigProbe completed successfully.")


def run_extraction(repo_root: Path):
    pdfs_dir = repo_root / "eval" / "pdfs"
    out_dir = repo_root / "eval" / "experiments" / "out"
    extract_dir = out_dir / "extract"
    extract_dir.mkdir(parents=True, exist_ok=True)

    # 1. Run PdfPig first per refinement 1
    run_pdfpig(repo_root)

    pdf_files = sorted(list(pdfs_dir.glob("**/*.pdf")))
    print(f"\n--- Step 2: Running PyMuPDF & pdfplumber on {len(pdf_files)} PDFs ---")

    all_records = []

    # Read pdfpig records if available
    pdfpig_csv = extract_dir / "pdfpig_summary.csv"
    if pdfpig_csv.exists():
        with open(pdfpig_csv, "r", encoding="utf-8-sig") as f:
            reader = csv.DictReader(f)
            for r in reader:
                all_records.append(
                    {
                        "pdf": r["pdf"],
                        "lib": r["lib"],
                        "page": int(r["page"]),
                        "chars": int(r["chars"]),
                        "letterRatio": float(r["letterRatio"]),
                        "persianRatio": float(r["persianRatio"]),
                        "presentationRatio": float(r["presentationRatio"]),
                        "replacementRatio": float(r["replacementRatio"]),
                        "looksReversed": r["looksReversed"].lower() == "true",
                        "quality": float(r["quality"]),
                        "empty_page": r["empty_page"].lower() == "true",
                        "seconds": float(r["seconds"]),
                    }
                )

    pymupdf_dir = extract_dir / "pymupdf"
    pdfplumber_dir = extract_dir / "pdfplumber"

    for pdf_path in pdf_files:
        pdf_name = pdf_path.name
        print(f"Processing {pdf_name}...")

        # PyMuPDF
        (pymupdf_dir / pdf_name).mkdir(parents=True, exist_ok=True)
        try:
            doc = pymupdf.open(pdf_path)
            for page_idx in range(len(doc)):
                page_num = page_idx + 1
                t0 = time.perf_counter()
                page = doc[page_idx]
                text = page.get_text("text")
                dt = time.perf_counter() - t0

                txt_path = pymupdf_dir / pdf_name / f"{page_num}.txt"
                txt_path.write_text(text, encoding="utf-8")

                m = compute_metrics(text, dt)
                all_records.append(
                    {
                        "pdf": pdf_name,
                        "lib": "pymupdf",
                        "page": page_num,
                        **m,
                    }
                )
            doc.close()
        except Exception as e:
            print(f"  PyMuPDF error on {pdf_name}: {e}")

        # pdfplumber
        (pdfplumber_dir / pdf_name).mkdir(parents=True, exist_ok=True)
        try:
            with pdfplumber.open(pdf_path) as pdf:
                for page_idx, page in enumerate(pdf.pages):
                    page_num = page_idx + 1
                    t0 = time.perf_counter()
                    text = page.extract_text() or ""
                    dt = time.perf_counter() - t0

                    txt_path = pdfplumber_dir / pdf_name / f"{page_num}.txt"
                    txt_path.write_text(text, encoding="utf-8")

                    m = compute_metrics(text, dt)
                    all_records.append(
                        {
                            "pdf": pdf_name,
                            "lib": "pdfplumber",
                            "page": page_num,
                            **m,
                        }
                    )
        except Exception as e:
            print(f"  pdfplumber error on {pdf_name}: {e}")

    # Write unified CSV
    summary_csv = out_dir / "extract_summary.csv"
    fieldnames = [
        "pdf",
        "lib",
        "page",
        "chars",
        "letterRatio",
        "persianRatio",
        "presentationRatio",
        "replacementRatio",
        "looksReversed",
        "quality",
        "empty_page",
        "seconds",
    ]
    with open(summary_csv, "w", encoding="utf-8", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        for r in all_records:
            writer.writerow(r)
    print(f"\nUnified extract summary written to {summary_csv} ({len(all_records)} page records).")

    # Generate samples markdown (3 random pages per PDF)
    generate_samples_md(repo_root, pdf_files, all_records)


def generate_samples_md(repo_root: Path, pdf_files: list[Path], all_records: list[dict]):
    out_dir = repo_root / "eval" / "experiments" / "out"
    samples_md = out_dir / "extract_samples.md"
    extract_dir = out_dir / "extract"

    random.seed(42)  # Deterministic seed for reproducible samples
    records_by_key = {(r["pdf"], r["lib"], r["page"]): r for r in all_records}

    md_lines = [
        "# PDF Extractor Comparison Samples",
        "",
        "This document contains side-by-side extracted text samples (3 sampled pages per document)",
        "from **PyMuPDF**, **pdfplumber**, and **PdfPig**.",
        "",
    ]

    for pdf_path in pdf_files:
        pdf_name = pdf_path.name
        # Find available pages for this PDF
        pages = sorted(list({r["page"] for r in all_records if r["pdf"] == pdf_name}))
        if not pages:
            continue

        sample_pages = sorted(random.sample(pages, min(3, len(pages))))
        md_lines.append(f"## Document: `{pdf_name}` (Total Pages: {len(pages)})")
        md_lines.append("")

        for page in sample_pages:
            md_lines.append(f"### Page {page}")
            md_lines.append("")

            for lib in ["pymupdf", "pdfplumber", "pdfpig"]:
                txt_file = extract_dir / lib / pdf_name / f"{page}.txt"
                txt_snippet = txt_file.read_text(encoding="utf-8").strip() if txt_file.exists() else "(File not found)"
                # Truncate snippet to 500 chars for readability
                display_snippet = txt_snippet[:500] + ("..." if len(txt_snippet) > 500 else "")
                display_snippet = display_snippet.replace("```", "'''")

                meta = records_by_key.get((pdf_name, lib, page), {})
                chars = meta.get("chars", 0)
                quality = meta.get("quality", 0.0)
                reversed_flag = meta.get("looksReversed", False)
                sec = meta.get("seconds", 0.0)

                md_lines.append(
                    f"#### Extractor: **{lib}** | Chars: {chars} | Quality: {quality} | Reversed: {reversed_flag} | Time: {sec:.3f}s"
                )
                md_lines.append("```text")
                md_lines.append(display_snippet if display_snippet else "(empty page / no text)")
                md_lines.append("```")
                md_lines.append("")

    samples_md.write_text("\n".join(md_lines), encoding="utf-8")
    print(f"Extraction samples written to {samples_md}")


if __name__ == "__main__":
    root = Path(__file__).resolve().parent.parent.parent
    run_extraction(root)
