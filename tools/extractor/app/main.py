import os
import re
import shutil
import threading
from typing import List

from fastapi import FastAPI, File, Form, HTTPException, Request, UploadFile
from PIL import Image
from pydantic import BaseModel
import pymupdf
import pytesseract

# Document behavior accurately:
# Starlette's UploadFile uses SpooledTemporaryFile which spools multipart
# uploads exceeding 1 MB to OS temporary files (e.g., /tmp in Linux).
# In production containers, /tmp is configured as tmpfs so spooled files remain in RAM.

app = FastAPI(title="DocSearch Extractor Sidecar", version="1.0.0")

MAX_UPLOAD_MB = int(os.environ.get("MAX_UPLOAD_MB", "50"))
_extraction_lock = threading.Lock()


class PageExtraction(BaseModel):
    page: int
    text: str


class ExtractionResponse(BaseModel):
    pages: List[PageExtraction]


@app.get("/health")
def health():
    return {"status": "Healthy"}


@app.post("/extract", response_model=ExtractionResponse)
def extract(request: Request, file: UploadFile = File(...)):
    max_bytes = MAX_UPLOAD_MB * 1024 * 1024

    # 1. Pre-read check on Content-Length header to reject oversized payloads immediately
    content_length_header = request.headers.get("content-length")
    if content_length_header:
        try:
            content_length = int(content_length_header)
            if content_length > max_bytes:
                raise HTTPException(
                    status_code=413,
                    detail=f"File exceeds maximum allowed upload size ({MAX_UPLOAD_MB} MB).",
                )
        except ValueError:
            pass

    # 2. Bounded chunked read to enforce size limit even when Content-Length is absent or chunked
    chunk_size = 1024 * 1024  # 1 MB
    total_bytes = 0
    buffer = bytearray()

    while True:
        chunk = file.file.read(chunk_size)
        if not chunk:
            break
        total_bytes += len(chunk)
        if total_bytes > max_bytes:
            raise HTTPException(
                status_code=413,
                detail=f"File exceeds maximum allowed upload size ({MAX_UPLOAD_MB} MB).",
            )
        buffer.extend(chunk)

    if not buffer.startswith(b"%PDF-"):
        raise HTTPException(
            status_code=400,
            detail="File is not a valid PDF (missing %PDF- header).",
        )

    # 3. Synchronous extraction guarded by lock for PyMuPDF thread safety
    with _extraction_lock:
        try:
            doc = pymupdf.open(stream=bytes(buffer), filetype="pdf")
        except Exception as ex:
            raise HTTPException(
                status_code=400,
                detail=f"Corrupt or unreadable PDF: {str(ex)}",
            )

        try:
            if doc.needs_pass:
                raise HTTPException(
                    status_code=422,
                    detail="Password-protected or encrypted PDFs are not supported.",
                )

            if doc.is_closed or doc.page_count < 1:
                raise HTTPException(
                    status_code=400,
                    detail="PDF has no readable pages or is empty.",
                )

            pages: List[PageExtraction] = []
            for i, page in enumerate(doc):
                # CRITICAL extraction call: page.get_text("text") directly.
                # Strictly no manual block sorting by (y, x).
                text = page.get_text("text")
                pages.append(PageExtraction(page=i + 1, text=text))

            return ExtractionResponse(pages=pages)
        finally:
            if not doc.is_closed:
                doc.close()


LANG_PATTERN = re.compile(r"^[a-zA-Z0-9_]+(\+[a-zA-Z0-9_]+)*$")
VALID_PSMS = {3, 4, 6, 11}


def is_tesseract_available() -> bool:
    cmd = getattr(pytesseract.pytesseract, "tesseract_cmd", "tesseract")
    if shutil.which(cmd):
        return True
    if os.path.isabs(cmd) and os.path.isfile(cmd) and os.access(cmd, os.X_OK):
        return True
    return False


def parse_page_numbers(pages_str: str, max_pages: int) -> List[int]:
    if not pages_str or not pages_str.strip():
        raise HTTPException(
            status_code=400,
            detail="Pages parameter cannot be empty.",
        )
    parts = [p.strip() for p in pages_str.split(",") if p.strip()]
    if not parts:
        raise HTTPException(
            status_code=400,
            detail="Pages parameter cannot be empty.",
        )
    result = set()
    for part in parts:
        try:
            num = int(part)
        except ValueError:
            raise HTTPException(
                status_code=400,
                detail=f"Invalid page number '{part}': must be an integer.",
            )
        if num < 1 or num > max_pages:
            raise HTTPException(
                status_code=400,
                detail=f"Page number {num} is out of range (1..{max_pages}).",
            )
        result.add(num)
    return sorted(result)


@app.post("/ocr", response_model=ExtractionResponse)
def ocr(
    request: Request,
    file: UploadFile = File(...),
    pages: str = Form(default=""),
    dpi: int = Form(300),
    lang: str = Form("fas+eng"),
    psm: int = Form(6),
):
    if not (72 <= dpi <= 600):
        raise HTTPException(
            status_code=400,
            detail=f"Invalid DPI: {dpi}. Must be between 72 and 600.",
        )

    if psm not in VALID_PSMS:
        raise HTTPException(
            status_code=400,
            detail=f"Invalid PSM: {psm}. Must be one of {sorted(VALID_PSMS)}.",
        )

    if not LANG_PATTERN.match(lang):
        raise HTTPException(
            status_code=400,
            detail=f"Invalid OCR language specification: '{lang}'.",
        )

    if not is_tesseract_available():
        raise HTTPException(
            status_code=503,
            detail="Tesseract OCR engine is not installed or not found on PATH.",
        )

    max_bytes = MAX_UPLOAD_MB * 1024 * 1024

    # 1. Pre-read check on Content-Length header to reject oversized payloads immediately
    content_length_header = request.headers.get("content-length")
    if content_length_header:
        try:
            content_length = int(content_length_header)
            if content_length > max_bytes:
                raise HTTPException(
                    status_code=413,
                    detail=f"File exceeds maximum allowed upload size ({MAX_UPLOAD_MB} MB).",
                )
        except ValueError:
            pass

    # 2. Bounded chunked read to enforce size limit even when Content-Length is absent or chunked
    chunk_size = 1024 * 1024  # 1 MB
    total_bytes = 0
    buffer = bytearray()

    while True:
        chunk = file.file.read(chunk_size)
        if not chunk:
            break
        total_bytes += len(chunk)
        if total_bytes > max_bytes:
            raise HTTPException(
                status_code=413,
                detail=f"File exceeds maximum allowed upload size ({MAX_UPLOAD_MB} MB).",
            )
        buffer.extend(chunk)

    if not buffer.startswith(b"%PDF-"):
        raise HTTPException(
            status_code=400,
            detail="File is not a valid PDF (missing %PDF- header).",
        )

    # 3. Synchronous rendering guarded by lock for PyMuPDF thread safety ONLY
    images_to_ocr: List[tuple[int, Image.Image]] = []
    with _extraction_lock:
        try:
            doc = pymupdf.open(stream=bytes(buffer), filetype="pdf")
        except Exception as ex:
            raise HTTPException(
                status_code=400,
                detail=f"Corrupt or unreadable PDF: {str(ex)}",
            )

        try:
            if doc.needs_pass:
                raise HTTPException(
                    status_code=422,
                    detail="Password-protected or encrypted PDFs are not supported.",
                )

            if doc.is_closed or doc.page_count < 1:
                raise HTTPException(
                    status_code=400,
                    detail="PDF has no readable pages or is empty.",
                )

            page_numbers = parse_page_numbers(pages, doc.page_count)

            for p in page_numbers:
                page = doc[p - 1]
                # CRITICAL: alpha=False prevents RGBA crash in Image.frombytes("RGB", ...)
                pix = page.get_pixmap(dpi=dpi, alpha=False)
                img = Image.frombytes("RGB", [pix.width, pix.height], pix.samples)
                images_to_ocr.append((p, img))
        finally:
            if not doc.is_closed:
                doc.close()

    # 4. OCR execution OUTSIDE the lock to prevent blocking health checks or extractions
    ocr_results: List[PageExtraction] = []
    for p, img in images_to_ocr:
        try:
            text = pytesseract.image_to_string(img, lang=lang, config=f"--psm {psm}")
        except Exception as ex:
            raise HTTPException(
                status_code=500,
                detail=f"Tesseract OCR failed on page {p}: {str(ex)}",
            )
        ocr_results.append(PageExtraction(page=p, text=text))

    return ExtractionResponse(pages=ocr_results)
