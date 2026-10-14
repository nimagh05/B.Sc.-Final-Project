"""Deterministic generator for synthetic conformance test PDF files.

Generates:
  - conformance/data/persian_sample.pdf (1-page valid Persian PDF with ZWNJ, digits, legal terms)
  - conformance/data/english_sample.pdf (1-page valid English PDF)
  - conformance/data/empty.pdf (0-byte file)
  - conformance/data/not_a_pdf.pdf (plain text file renamed to .pdf)
  - conformance/data/corrupt.pdf (corrupt PDF with valid header but garbage body)
"""

from pathlib import Path
import unicodedata
import pymupdf


def generate_all(data_dir: Path) -> None:
    data_dir.mkdir(parents=True, exist_ok=True)

    # 1. persian_sample.pdf
    persian_path = data_dir / "persian_sample.pdf"
    doc_fa = pymupdf.open()
    page_fa = doc_fa.new_page(width=595, height=842)  # A4
    rect_fa = pymupdf.Rect(50, 50, 545, 792)
    html_fa = (
        "<div dir='rtl' style='font-family: sans-serif; font-size: 16pt; line-height: 2.0; text-align: right;'>"
        "<h2>آیین‌نامه آموزشی دانشگاه</h2>"
        "<p>ماده ۱: حداکثر تعداد واحد مجاز در هر نیم‌سال تحصیلی ۲۰ واحد می‌باشد.</p>"
        "<p>تبصره ۱: در صورت احراز معدل الف، دانشجو می‌تواند تا ۲۴ واحد اخذ نماید.</p>"
        "</div>"
    )
    page_fa.insert_htmlbox(rect_fa, html_fa)
    doc_fa.save(persian_path)
    doc_fa.close()

    # Verify extraction for persian_sample.pdf
    verify_fa = pymupdf.open(persian_path)
    extracted_fa = verify_fa[0].get_text("text")
    verify_fa.close()

    # Normalize with NFKC (matching PersianTextNormalizer step 1 in SPEC-PERSIAN-AND-RAG.md §1.1)
    norm_fa = unicodedata.normalize("NFKC", extracted_fa)
    for expected_word in ("آیین", "ماده", "واحد"):
        assert (expected_word in norm_fa) or (expected_word in extracted_fa), (
            f"Expected word '{expected_word}' not found in extracted Persian text: {extracted_fa!r}"
        )
    print(f"[OK] Generated and verified {persian_path.name}")

    # 2. english_sample.pdf
    english_path = data_dir / "english_sample.pdf"
    doc_en = pymupdf.open()
    page_en = doc_en.new_page(width=595, height=842)
    rect_en = pymupdf.Rect(50, 50, 545, 792)
    html_en = (
        "<div style='font-family: sans-serif; font-size: 14pt; line-height: 1.8;'>"
        "<h2>University Academic Regulations</h2>"
        "<p>Article 1: The maximum number of course credits allowed per semester is 20 credits.</p>"
        "<p>Note 1: Students with high honors may register for up to 24 credits.</p>"
        "</div>"
    )
    page_en.insert_htmlbox(rect_en, html_en)
    doc_en.save(english_path)
    doc_en.close()

    # Verify extraction for english_sample.pdf
    verify_en = pymupdf.open(english_path)
    extracted_en = verify_en[0].get_text("text")
    verify_en.close()

    for expected_word in ("Article", "credits"):
        assert expected_word in extracted_en, (
            f"Expected word '{expected_word}' not found in extracted English text: {extracted_en!r}"
        )
    print(f"[OK] Generated and verified {english_path.name}")

    # 3. empty.pdf (0 bytes)
    empty_path = data_dir / "empty.pdf"
    empty_path.write_bytes(b"")
    assert empty_path.stat().st_size == 0
    print(f"[OK] Generated {empty_path.name} (0 bytes)")

    # 4. not_a_pdf.pdf (plain text renamed to .pdf)
    not_pdf_path = data_dir / "not_a_pdf.pdf"
    not_pdf_path.write_text(
        "This is a plain text file without PDF magic bytes.\n"
        "It should be rejected as an invalid PDF file.\n",
        encoding="utf-8",
    )
    assert not_pdf_path.stat().st_size > 0
    print(f"[OK] Generated {not_pdf_path.name} (plain text)")

    # 5. corrupt.pdf (valid header but corrupt body)
    corrupt_path = data_dir / "corrupt.pdf"
    corrupt_path.write_bytes(
        b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n"
        b"1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n"
        b"CORRUPTED_BINARY_DATA_TRUNCATED_OFFSET_EOF\x00\xff\xfe\x01\x02\x03\x04"
    )
    assert corrupt_path.stat().st_size > 0
    print(f"[OK] Generated {corrupt_path.name} (corrupt PDF)")


if __name__ == "__main__":
    target_dir = Path(__file__).resolve().parent / "data"
    generate_all(target_dir)
    print("\nAll synthetic test files successfully generated and verified.")
