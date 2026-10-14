#!/usr/bin/env python3
"""
Validates eval/questions.json against the schema and eval/pdfs/ directory
per docs/EVAL.md §1.3.
"""

import json
import sys
from pathlib import Path
from collections import Counter

try:
    import fitz  # PyMuPDF
except ImportError:
    fitz = None

VALID_TYPES = {
    "factual",
    "numeric",
    "legal-reference",
    "multi-chunk",
    "paraphrase",
    "not-in-docs",
    "english",
}
VALID_SPLITS = {"dev", "test"}


def validate_questions(questions_path: Path, pdfs_dir: Path) -> bool:
    if not questions_path.exists():
        print(f"ERROR: {questions_path} does not exist.")
        return False

    with open(questions_path, "r", encoding="utf-8") as f:
        try:
            questions = json.load(f)
        except Exception as e:
            print(f"ERROR: Failed to parse {questions_path}: {e}")
            return False

    if not isinstance(questions, list):
        print(f"ERROR: Root of {questions_path} must be a JSON array.")
        return False

    print(f"Loaded {len(questions)} questions from {questions_path.name}.")

    pdf_page_counts = {}
    if fitz:
        for pdf_file in pdfs_dir.glob("**/*.pdf"):
            try:
                doc = fitz.open(pdf_file)
                # Store relative to pdfs_dir and also filename
                rel_path = pdf_file.relative_to(pdfs_dir).as_posix()
                pdf_page_counts[rel_path] = len(doc)
                pdf_page_counts[pdf_file.name] = len(doc)
                doc.close()
            except Exception as e:
                print(f"WARNING: Could not open {pdf_file}: {e}")

    seen_ids = set()
    errors = []
    type_counts = Counter()
    split_counts = Counter()
    lang_counts = Counter()

    for idx, q in enumerate(questions):
        qid = q.get("id")
        if not qid:
            errors.append(f"Item #{idx}: missing 'id'")
            continue

        if qid in seen_ids:
            errors.append(f"Question '{qid}': duplicate ID")
        seen_ids.add(qid)

        q_type = q.get("type")
        if q_type not in VALID_TYPES:
            errors.append(f"Question '{qid}': invalid type '{q_type}'. Must be one of {VALID_TYPES}")
        type_counts[q_type] += 1

        split = q.get("split")
        if split not in VALID_SPLITS:
            errors.append(f"Question '{qid}': invalid split '{split}'. Must be one of {VALID_SPLITS}")
        split_counts[split] += 1

        lang = q.get("language", "unknown")
        lang_counts[lang] += 1

        q_text = q.get("question")
        if not q_text or not str(q_text).strip():
            errors.append(f"Question '{qid}': empty 'question'")

        source_file = q.get("source_file")
        source_pages = q.get("source_pages")
        keywords = q.get("answer_keywords")

        if q_type == "not-in-docs":
            if source_file is not None:
                errors.append(f"Question '{qid}': type is 'not-in-docs' but 'source_file' is not null ({source_file})")
            if source_pages and len(source_pages) > 0:
                errors.append(f"Question '{qid}': type is 'not-in-docs' but 'source_pages' is not empty ({source_pages})")
        else:
            if not source_file:
                errors.append(f"Question '{qid}': answerable question has empty 'source_file'")
            else:
                target_pdf = pdfs_dir / source_file
                if not target_pdf.exists():
                    errors.append(f"Question '{qid}': source file '{source_file}' not found in {pdfs_dir}")
                elif fitz and source_file in pdf_page_counts:
                    max_pages = pdf_page_counts[source_file]
                    if not source_pages or not isinstance(source_pages, list):
                        errors.append(f"Question '{qid}': 'source_pages' must be a non-empty list of page numbers")
                    else:
                        for p in source_pages:
                            if not isinstance(p, int) or p < 1 or p > max_pages:
                                errors.append(
                                    f"Question '{qid}': page {p} out of range [1, {max_pages}] for file '{source_file}'"
                                )

            if not keywords or not isinstance(keywords, list) or len(keywords) == 0:
                errors.append(f"Question '{qid}': answerable question must have non-empty 'answer_keywords'")

    print("\n--- Question Dataset Statistics ---")
    print(f"Total questions: {len(questions)}")
    print("\nCounts by Type:")
    for t, c in sorted(type_counts.items()):
        print(f"  {t:18s}: {c}")

    print("\nCounts by Split:")
    for s, c in sorted(split_counts.items()):
        print(f"  {s:18s}: {c}")

    print("\nCounts by Language:")
    for l, c in sorted(lang_counts.items()):
        print(f"  {l:18s}: {c}")

    if errors:
        print(f"\nVALIDATION FAILED with {len(errors)} error(s):")
        for err in errors[:30]:
            print(f"  - {err}")
        if len(errors) > 30:
            print(f"  ... and {len(errors) - 30} more errors.")
        return False

    print("\nAll quality checks PASSED successfully!")
    return True


if __name__ == "__main__":
    repo_root = Path(__file__).resolve().parent.parent
    q_file = repo_root / "eval" / "questions.json"
    p_dir = repo_root / "eval" / "pdfs"

    success = validate_questions(q_file, p_dir)
    sys.exit(0 if success else 1)
