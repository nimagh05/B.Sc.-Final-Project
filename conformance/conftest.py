"""Pytest fixtures and configuration for the black-box conformance suite."""

import io
import os
import time
from pathlib import Path
from typing import Any, Dict, Optional

import pytest
import requests


class ApiClient:
    """Helper HTTP client wrapping requests.Session with API routing and auth."""

    def __init__(self, base_url: str):
        self.base_url = base_url.rstrip("/")
        self.session = requests.Session()
        self.token: Optional[str] = None

    def set_token(self, token: Optional[str]) -> None:
        self.token = token
        if token:
            self.session.headers["Authorization"] = f"Bearer {token}"
        else:
            self.session.headers.pop("Authorization", None)

    def _resolve_url(self, path: str) -> str:
        if path.startswith("http://") or path.startswith("https://"):
            return path
        # Endpoints mapped directly at root
        if path.startswith("/health") or path.startswith("/openapi") or path.startswith("/scalar"):
            return f"{self.base_url}{path}"
        # Standard v1 API endpoints
        if path.startswith("/api/v1"):
            return f"{self.base_url}{path}"
        normalized = "/" + path.lstrip("/")
        return f"{self.base_url}/api/v1{normalized}"

    def request(self, method: str, path: str, **kwargs: Any) -> requests.Response:
        url = self._resolve_url(path)
        kwargs.setdefault("timeout", 30.0)
        return self.session.request(method, url, **kwargs)

    def get(self, path: str, **kwargs: Any) -> requests.Response:
        return self.request("GET", path, **kwargs)

    def post(self, path: str, **kwargs: Any) -> requests.Response:
        return self.request("POST", path, **kwargs)

    def put(self, path: str, **kwargs: Any) -> requests.Response:
        return self.request("PUT", path, **kwargs)

    def delete(self, path: str, **kwargs: Any) -> requests.Response:
        return self.request("DELETE", path, **kwargs)

    def wait_until_ready(
        self,
        document_id: str,
        timeout: float = 120.0,
        poll_interval: float = 1.0,
    ) -> Dict[str, Any]:
        """Polls GET /documents/{id} until status is Ready or Failed."""
        start_time = time.monotonic()
        interval = poll_interval
        while time.monotonic() - start_time < timeout:
            resp = self.get(f"/documents/{document_id}")
            if resp.status_code == 200:
                doc = resp.json()
                status = doc.get("status")
                if status == "Ready":
                    return doc
                if status == "Failed":
                    raise AssertionError(
                        f"Document {document_id} ended in status 'Failed': {doc.get('errorMessage')}"
                    )
            time.sleep(interval)
            interval = min(interval * 1.3, 5.0)

        raise TimeoutError(
            f"Document {document_id} did not reach 'Ready' status within {timeout} seconds."
        )


@pytest.fixture(scope="session")
def base_url() -> str:
    return os.environ.get("BASE_URL", "http://localhost:5000").rstrip("/")


@pytest.fixture(scope="session")
def admin_credentials() -> Dict[str, str]:
    return {
        "email": os.environ.get("ADMIN_EMAIL", "admin@ci.test"),
        "password": os.environ.get("ADMIN_PASSWORD", "Ci_Admin_123!"),
    }


@pytest.fixture(scope="session", autouse=True)
def ensure_api_reachable(base_url: str) -> None:
    """Pre-flight check: skip entire suite cleanly if BASE_URL is offline."""
    try:
        resp = requests.get(f"{base_url}/health/live", timeout=2.0)
        if resp.status_code != 200:
            pytest.skip(
                f"API at {base_url} returned HTTP {resp.status_code} on /health/live. Conformance tests skipped."
            )
    except requests.exceptions.RequestException as exc:
        pytest.skip(
            f"API at {base_url} is unreachable ({exc.__class__.__name__}). Conformance tests skipped cleanly."
        )


@pytest.fixture(scope="session")
def admin_client(base_url: str, admin_credentials: Dict[str, str]) -> ApiClient:
    """Session-scoped client authenticated as Admin (reuses token across tests)."""
    client = ApiClient(base_url)
    resp = client.post("/auth/login", json=admin_credentials)
    if resp.status_code == 200:
        token = resp.json().get("accessToken")
        client.set_token(token)
    return client


@pytest.fixture(scope="session")
def user_client(base_url: str) -> ApiClient:
    """Session-scoped primary test user client to conserve /auth/* rate limits."""
    client = ApiClient(base_url)
    email = "user_primary@conformance.local"
    password = "User_Strong_Pass123!"
    # Ensure registered then login
    client.post(
        "/auth/register",
        json={"email": email, "password": password, "displayName": "Primary User"},
    )
    login_resp = client.post("/auth/login", json={"email": email, "password": password})
    if login_resp.status_code == 200:
        client.set_token(login_resp.json().get("accessToken"))
    return client


@pytest.fixture(scope="session")
def second_user_client(base_url: str) -> ApiClient:
    """Session-scoped secondary test user client for isolation and ACL tests."""
    client = ApiClient(base_url)
    email = "user_secondary@conformance.local"
    password = "User_Strong_Pass123!"
    client.post(
        "/auth/register",
        json={"email": email, "password": password, "displayName": "Secondary User"},
    )
    login_resp = client.post("/auth/login", json={"email": email, "password": password})
    if login_resp.status_code == 200:
        client.set_token(login_resp.json().get("accessToken"))
    return client


@pytest.fixture(scope="session")
def data_dir() -> Path:
    return Path(__file__).resolve().parent / "data"


@pytest.fixture(scope="session")
def persian_pdf_path(data_dir: Path) -> Path:
    path = data_dir / "persian_sample.pdf"
    if not path.exists():
        pytest.fail(f"Required test fixture missing: {path}")
    return path


@pytest.fixture(scope="session")
def english_pdf_path(data_dir: Path) -> Path:
    path = data_dir / "english_sample.pdf"
    if not path.exists():
        pytest.fail(f"Required test fixture missing: {path}")
    return path


@pytest.fixture(scope="session")
def empty_pdf_path(data_dir: Path) -> Path:
    path = data_dir / "empty.pdf"
    if not path.exists():
        pytest.fail(f"Required test fixture missing: {path}")
    return path


@pytest.fixture(scope="session")
def not_pdf_path(data_dir: Path) -> Path:
    path = data_dir / "not_a_pdf.pdf"
    if not path.exists():
        pytest.fail(f"Required test fixture missing: {path}")
    return path


@pytest.fixture(scope="session")
def corrupt_pdf_path(data_dir: Path) -> Path:
    path = data_dir / "corrupt.pdf"
    if not path.exists():
        pytest.fail(f"Required test fixture missing: {path}")
    return path


@pytest.fixture
def oversized_bytes_stream() -> io.BytesIO:
    """Generates an in-memory stream exceeding Storage:MaxUploadMb without disk persistence."""
    max_mb = int(os.environ.get("MAX_UPLOAD_MB", "50"))
    oversized_size = (max_mb + 2) * 1024 * 1024  # e.g., 52 MB
    # Chunked in-memory pseudo stream to conserve physical allocation
    # Using bytearray of size 1MB repeated or BytesIO with seek
    stream = io.BytesIO()
    stream.write(b"%PDF-1.4\n")
    stream.seek(oversized_size - 1)
    stream.write(b"\x00")
    stream.seek(0)
    return stream
