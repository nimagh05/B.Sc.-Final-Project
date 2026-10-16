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
