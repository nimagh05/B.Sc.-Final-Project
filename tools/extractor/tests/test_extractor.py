import io
from pathlib import Path
import sys

# Ensure tools/extractor is in sys.path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import pymupdf
import pytest
from fastapi.testclient import TestClient

from app.main import app, MAX_UPLOAD_MB

client = TestClient(app)

TEST_DATA_DIR = Path(__file__).resolve().parents[3] / "tests" / "TestData"


def create_in_memory_pdf(pages_text: list[str]) -> bytes:
    doc = pymupdf.open()
    for text in pages_text:
        page = doc.new_page()
        page.insert_text((50, 50), text)
    pdf_bytes = doc.tobytes()
    doc.close()
    return pdf_bytes


def test_health_endpoint():
    response = client.get("/health")
    assert response.status_code == 200
    assert response.json() == {"status": "Healthy"}


def test_extract_valid_in_memory_pdf():
    pdf_bytes = create_in_memory_pdf(["First page text", "Second page text"])
    response = client.post(
        "/extract",
        files={"file": ("test.pdf", io.BytesIO(pdf_bytes), "application/pdf")},
    )
    assert response.status_code == 200
    data = response.json()
    assert "pages" in data
    assert len(data["pages"]) == 2
    assert data["pages"][0]["page"] == 1
    assert "First page text" in data["pages"][0]["text"]
    assert data["pages"][1]["page"] == 2
    assert "Second page text" in data["pages"][1]["text"]


def test_extract_not_a_pdf():
    not_pdf_bytes = b"This is a plain text file, not a valid PDF."
    response = client.post(
        "/extract",
        files={"file": ("not_a_pdf.txt", io.BytesIO(not_pdf_bytes), "application/pdf")},
    )
    assert response.status_code == 400
    assert "missing %PDF- header" in response.json()["detail"]


def test_extract_corrupt_pdf():
    corrupt_bytes = b"%PDF-1.4\nSome completely invalid corrupt garbage bytes here"
    response = client.post(
        "/extract",
        files={"file": ("corrupt.pdf", io.BytesIO(corrupt_bytes), "application/pdf")},
    )
    assert response.status_code == 400
    assert "Corrupt or unreadable PDF" in response.json()["detail"]


def test_extract_oversized_file(monkeypatch):
    # Set MAX_UPLOAD_MB to 1 MB for testing
    import app.main as app_main

    monkeypatch.setattr(app_main, "MAX_UPLOAD_MB", 1)
    oversized_bytes = b"%PDF-1.4\n" + b"X" * (1024 * 1024 + 100)
    response = client.post(
        "/extract",
        files={"file": ("oversized.pdf", io.BytesIO(oversized_bytes), "application/pdf")},
    )
    assert response.status_code == 413
    assert "exceeds maximum allowed upload size" in response.json()["detail"]


def test_extract_oversized_content_length_header(monkeypatch):
    import app.main as app_main

    monkeypatch.setattr(app_main, "MAX_UPLOAD_MB", 1)
    response = client.post(
        "/extract",
        headers={"content-length": str(2 * 1024 * 1024)},
        files={"file": ("oversized.pdf", io.BytesIO(b"%PDF-1.4\nshort"), "application/pdf")},
    )
    assert response.status_code == 413
    assert "exceeds maximum allowed upload size" in response.json()["detail"]


def test_extract_encrypted_pdf_returns_422():
    doc = pymupdf.open()
    page = doc.new_page()
    page.insert_text((50, 50), "Confidential encrypted text")
    enc_bytes = doc.tobytes(
        encryption=pymupdf.PDF_ENCRYPT_AES_256,
        owner_pw="secret",
        user_pw="secret",
    )
    doc.close()

    response = client.post(
        "/extract",
        files={"file": ("encrypted.pdf", io.BytesIO(enc_bytes), "application/pdf")},
    )
    assert response.status_code == 422
    assert "Password-protected or encrypted PDFs are not supported" in response.json()["detail"]


def test_extract_persian_sample_pdf():
    persian_pdf_path = TEST_DATA_DIR / "persian-sample.pdf"
    assert persian_pdf_path.exists(), f"Sample file missing: {persian_pdf_path}"

    with open(persian_pdf_path, "rb") as f:
        response = client.post(
            "/extract",
            files={"file": ("persian-sample.pdf", f, "application/pdf")},
        )

    assert response.status_code == 200
    data = response.json()
    assert len(data["pages"]) == 2
    for page in data["pages"]:
        assert len(page["text"].strip()) > 100


def test_extract_english_sample_pdf():
    english_pdf_path = TEST_DATA_DIR / "english-sample.pdf"
    assert english_pdf_path.exists(), f"Sample file missing: {english_pdf_path}"

    with open(english_pdf_path, "rb") as f:
        response = client.post(
            "/extract",
            files={"file": ("english-sample.pdf", f, "application/pdf")},
        )

    assert response.status_code == 200
    data = response.json()
    assert len(data["pages"]) == 1
    assert "Retrieval-Augmented Generation for" in data["pages"][0]["text"]


def test_extract_scanned_sample_pdf():
    scanned_pdf_path = TEST_DATA_DIR / "scanned-sample.pdf"
    assert scanned_pdf_path.exists(), f"Sample file missing: {scanned_pdf_path}"

    with open(scanned_pdf_path, "rb") as f:
        response = client.post(
            "/extract",
            files={"file": ("scanned-sample.pdf", f, "application/pdf")},
        )

    assert response.status_code == 200
    data = response.json()
    assert len(data["pages"]) == 2
    for page in data["pages"]:
        # Scanned sample has no embedded selectable text
        assert len(page["text"].strip()) < 20


def test_ocr_endpoint_valid_pdf_mocked(monkeypatch):
    import app.main as app_main
    import pytesseract

    monkeypatch.setattr(app_main, "is_tesseract_available", lambda: True)

    def mock_image_to_string(img, lang=None, config=None):
        return "متن آزمایشی استخراج شده با او سی آر"

    monkeypatch.setattr(pytesseract, "image_to_string", mock_image_to_string)

    pdf_bytes = create_in_memory_pdf(["Page 1", "Page 2", "Page 3"])
    response = client.post(
        "/ocr",
        files={"file": ("test.pdf", io.BytesIO(pdf_bytes), "application/pdf")},
        data={"pages": "1,3", "dpi": "300", "lang": "fas+eng"},
    )
    assert response.status_code == 200
    data = response.json()
    assert "pages" in data
    assert len(data["pages"]) == 2
    assert data["pages"][0]["page"] == 1
    assert "او سی آر" in data["pages"][0]["text"]
    assert data["pages"][1]["page"] == 3
    assert "او سی آر" in data["pages"][1]["text"]


def test_ocr_endpoint_missing_tesseract_returns_503(monkeypatch):
    import app.main as app_main

    monkeypatch.setattr(app_main, "is_tesseract_available", lambda: False)

    pdf_bytes = create_in_memory_pdf(["Page 1"])
    response = client.post(
        "/ocr",
        files={"file": ("test.pdf", io.BytesIO(pdf_bytes), "application/pdf")},
        data={"pages": "1", "dpi": "300", "lang": "fas+eng"},
    )
    assert response.status_code == 503
    assert "Tesseract OCR engine is not installed or not found on PATH" in response.json()["detail"]


def test_ocr_endpoint_invalid_pages(monkeypatch):
    import app.main as app_main

    monkeypatch.setattr(app_main, "is_tesseract_available", lambda: True)
    pdf_bytes = create_in_memory_pdf(["Page 1", "Page 2"])

    # Empty pages
    res1 = client.post(
        "/ocr",
        files={"file": ("test.pdf", io.BytesIO(pdf_bytes), "application/pdf")},
        data={"pages": "", "dpi": "300", "lang": "fas+eng"},
    )
    assert res1.status_code == 400
    assert "empty" in res1.json()["detail"].lower()

    # Non-integer pages
    res2 = client.post(
        "/ocr",
        files={"file": ("test.pdf", io.BytesIO(pdf_bytes), "application/pdf")},
        data={"pages": "1,abc", "dpi": "300", "lang": "fas+eng"},
    )
    assert res2.status_code == 400
    assert "must be an integer" in res2.json()["detail"].lower()

    # Out of range pages
    res3 = client.post(
        "/ocr",
        files={"file": ("test.pdf", io.BytesIO(pdf_bytes), "application/pdf")},
        data={"pages": "1,99", "dpi": "300", "lang": "fas+eng"},
    )
    assert res3.status_code == 400
    assert "out of range" in res3.json()["detail"].lower()


def test_ocr_endpoint_invalid_dpi_or_lang(monkeypatch):
    import app.main as app_main

    monkeypatch.setattr(app_main, "is_tesseract_available", lambda: True)
    pdf_bytes = create_in_memory_pdf(["Page 1"])

    # DPI too low
    res1 = client.post(
        "/ocr",
        files={"file": ("test.pdf", io.BytesIO(pdf_bytes), "application/pdf")},
        data={"pages": "1", "dpi": "50", "lang": "fas+eng"},
    )
    assert res1.status_code == 400
    assert "Invalid DPI" in res1.json()["detail"]

    # DPI too high
    res2 = client.post(
        "/ocr",
        files={"file": ("test.pdf", io.BytesIO(pdf_bytes), "application/pdf")},
        data={"pages": "1", "dpi": "700", "lang": "fas+eng"},
    )
    assert res2.status_code == 400
    assert "Invalid DPI" in res2.json()["detail"]

    # Invalid language format
    res3 = client.post(
        "/ocr",
        files={"file": ("test.pdf", io.BytesIO(pdf_bytes), "application/pdf")},
        data={"pages": "1", "dpi": "300", "lang": "fas; rm -rf /"},
    )
    assert res3.status_code == 400
    assert "Invalid OCR language specification" in res3.json()["detail"]


def test_ocr_endpoint_encrypted_pdf_returns_422(monkeypatch):
    import app.main as app_main

    monkeypatch.setattr(app_main, "is_tesseract_available", lambda: True)

    doc = pymupdf.open()
    page = doc.new_page()
    page.insert_text((50, 50), "Confidential encrypted text")
    enc_bytes = doc.tobytes(
        encryption=pymupdf.PDF_ENCRYPT_AES_256,
        owner_pw="secret",
        user_pw="secret",
    )
    doc.close()

    response = client.post(
        "/ocr",
        files={"file": ("encrypted.pdf", io.BytesIO(enc_bytes), "application/pdf")},
        data={"pages": "1", "dpi": "300", "lang": "fas+eng"},
    )
    assert response.status_code == 422
    assert "Password-protected or encrypted PDFs are not supported" in response.json()["detail"]


def test_ocr_endpoint_not_a_pdf(monkeypatch):
    import app.main as app_main

    monkeypatch.setattr(app_main, "is_tesseract_available", lambda: True)
    not_pdf_bytes = b"This is a plain text file, not a valid PDF."
    response = client.post(
        "/ocr",
        files={"file": ("not_a_pdf.txt", io.BytesIO(not_pdf_bytes), "application/pdf")},
        data={"pages": "1", "dpi": "300", "lang": "fas+eng"},
    )
    assert response.status_code == 400
    assert "missing %PDF- header" in response.json()["detail"]


def test_ocr_endpoint_oversized_file(monkeypatch):
    import app.main as app_main

    monkeypatch.setattr(app_main, "is_tesseract_available", lambda: True)
    monkeypatch.setattr(app_main, "MAX_UPLOAD_MB", 1)
    oversized_bytes = b"%PDF-1.4\n" + b"X" * (1024 * 1024 + 100)
    response = client.post(
        "/ocr",
        files={"file": ("oversized.pdf", io.BytesIO(oversized_bytes), "application/pdf")},
        data={"pages": "1", "dpi": "300", "lang": "fas+eng"},
    )
    assert response.status_code == 413
    assert "exceeds maximum allowed upload size" in response.json()["detail"]


def test_ocr_endpoint_scanned_sample_real_tesseract():
    from app.main import is_tesseract_available

    if not is_tesseract_available():
        pytest.skip("Tesseract binary not installed on host/PATH")

    scanned_pdf_path = TEST_DATA_DIR / "scanned-sample.pdf"
    assert scanned_pdf_path.exists(), f"Sample file missing: {scanned_pdf_path}"

    with open(scanned_pdf_path, "rb") as f:
        response = client.post(
            "/ocr",
            files={"file": ("scanned-sample.pdf", f, "application/pdf")},
            data={"pages": "1", "dpi": "250", "lang": "fas+eng"},
        )

    assert response.status_code == 200
    data = response.json()
    assert len(data["pages"]) == 1
    assert data["pages"][0]["page"] == 1
    assert len(data["pages"][0]["text"].strip()) > 0
