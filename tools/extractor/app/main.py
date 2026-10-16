import os
import threading
from typing import List

from fastapi import FastAPI, File, HTTPException, Request, UploadFile
from pydantic import BaseModel
import pymupdf

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
