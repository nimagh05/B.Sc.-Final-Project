"""Conformance test suite for DocSearch API contract and safety verification.

Covers all 24 tests from docs/EVAL.md against the contract in docs/SPEC-DATA-AND-API.md.
"""

import io
import re
import time
import uuid
from typing import Any, Dict, List

import pytest
import requests
from conftest import ApiClient


# Helper functions
def unique_id() -> str:
    return uuid.uuid4().hex[:8]


def create_collection(client: ApiClient, name_prefix: str = "col") -> str:
    name = f"{name_prefix}_{unique_id()}"
    resp = client.post("/collections", json={"name": name, "description": "Conformance Test"})
    assert resp.status_code == 201, f"Failed to create collection: {resp.text}"
    return resp.json()["id"]


def upload_document(client: ApiClient, collection_id: str, file_path_or_bytes: Any, file_name: str) -> requests.Response:
    if isinstance(file_path_or_bytes, (str, bytes, io.BytesIO)):
        if isinstance(file_path_or_bytes, str):
            with open(file_path_or_bytes, "rb") as f:
                content = f.read()
        elif isinstance(file_path_or_bytes, io.BytesIO):
            content = file_path_or_bytes.getvalue()
        else:
            content = file_path_or_bytes
    else:
        # pathlib.Path
        content = file_path_or_bytes.read_bytes()

    files = {"file": (file_name, content, "application/pdf")}
    return client.post(f"/collections/{collection_id}/documents", files=files)


# ---------------------------------------------------------------------------
# Test #1: Health endpoints
# ---------------------------------------------------------------------------
@pytest.mark.contract
def test_01_health_live_and_ready(user_client: ApiClient):
    """GET /health/live and GET /health/ready return 200 with JSON, leaking no secrets."""
    # Live check
    resp_live = user_client.get("/health/live")
    assert resp_live.status_code == 200, f"/health/live failed: {resp_live.text}"
    data_live = resp_live.json()
    assert "status" in data_live

    # Ready check
    resp_ready = user_client.get("/health/ready")
    assert resp_ready.status_code == 200, f"/health/ready failed: {resp_ready.text}"
    data_ready = resp_ready.json()
    assert data_ready.get("status") in ("Healthy", "healthy")
    assert "checks" in data_ready
    assert isinstance(data_ready["checks"], list)

    # Secret leakage check across response bodies
    for text in (resp_live.text.lower(), resp_ready.text.lower()):
        assert "password" not in text
        assert "secret" not in text
        assert "connectionstring" not in text
        assert "postgres://" not in text


# ---------------------------------------------------------------------------
# Test #2: Register + login
# ---------------------------------------------------------------------------
@pytest.mark.contract
def test_02_register_and_login(base_url: str):
    """Register new user, login to obtain token, and verify wrong credentials rejection."""
    client = ApiClient(base_url)
    email = f"test_{unique_id()}@conformance.local"
    password = "User_Strong_Pass123!"

    # 1. Register
    reg_resp = client.post(
        "/auth/register",
        json={"email": email, "password": password, "displayName": "Conformance User"},
    )
    assert reg_resp.status_code == 201, f"Register failed: {reg_resp.text}"
    reg_data = reg_resp.json()
    assert "id" in reg_data
    assert reg_data.get("email") == email

    # 2. Login valid
    login_resp = client.post("/auth/login", json={"email": email, "password": password})
    assert login_resp.status_code == 200, f"Login failed: {login_resp.text}"
    login_data = login_resp.json()
    assert "accessToken" in login_data
    assert "expiresAtUtc" in login_data

    # 3. Login wrong password -> 401 problem+json auth.invalid_credentials
    bad_login = client.post("/auth/login", json={"email": email, "password": "WrongPassword123!"})
    assert bad_login.status_code == 401, f"Expected 401 on bad password: {bad_login.text}"
    bad_data = bad_login.json()
    assert bad_data.get("code") == "auth.invalid_credentials"


# ---------------------------------------------------------------------------
# Test #3: Protected endpoint without token
# ---------------------------------------------------------------------------
@pytest.mark.contract
def test_03_protected_endpoint_without_token(base_url: str):
    """Calling protected endpoints without token returns 401 problem+json."""
    anon_client = ApiClient(base_url)
    resp = anon_client.get("/collections")
    assert resp.status_code == 401, f"Expected 401, got {resp.status_code}: {resp.text}"
    data = resp.json()
    assert data.get("code") == "auth.unauthorized"


# ---------------------------------------------------------------------------
# Test #4: Collection CRUD
# ---------------------------------------------------------------------------
@pytest.mark.contract
def test_04_collection_crud(user_client: ApiClient):
    """Collection creation, retrieval, update, duplicate name rejection, and deletion."""
    col_name = f"col_crud_{unique_id()}"

    # Create
    create_resp = user_client.post(
        "/collections",
        json={"name": col_name, "description": "Test Collection"},
    )
    assert create_resp.status_code == 201, f"Create collection failed: {create_resp.text}"
    assert "Location" in create_resp.headers
    col_id = create_resp.json()["id"]

    # Read
    get_resp = user_client.get(f"/collections/{col_id}")
    assert get_resp.status_code == 200
    assert get_resp.json()["name"] == col_name

    # Update
    updated_name = f"{col_name}_updated"
    put_resp = user_client.put(f"/collections/{col_id}", json={"name": updated_name})
    assert put_resp.status_code == 200
    assert put_resp.json()["name"] == updated_name

    # Duplicate name under same owner -> 409 collection.name_taken
    dup_resp = user_client.post("/collections", json={"name": updated_name})
    assert dup_resp.status_code == 409, f"Expected 409, got {dup_resp.status_code}: {dup_resp.text}"
    assert dup_resp.json().get("code") == "collection.name_taken"

    # Delete
    del_resp = user_client.delete(f"/collections/{col_id}")
    assert del_resp.status_code == 204

    # Read after delete -> 404
    after_resp = user_client.get(f"/collections/{col_id}")
    assert after_resp.status_code == 404


# ---------------------------------------------------------------------------
# Test #5: Upload valid PDF
# ---------------------------------------------------------------------------
@pytest.mark.contract
def test_05_upload_valid_pdf(user_client: ApiClient, english_pdf_path: Any):
    """Uploading a valid PDF returns 202 Accepted with status Uploaded/Processing."""
    col_id = create_collection(user_client, "upload")
    resp = upload_document(user_client, col_id, english_pdf_path, "regulations.pdf")
    assert resp.status_code == 202, f"Upload failed: {resp.text}"
    assert "Location" in resp.headers

    data = resp.json()
    assert "id" in data
    assert data.get("collectionId") == col_id
    assert data.get("fileName") == "regulations.pdf"
    assert data.get("status") in ("Uploaded", "Processing")


# ---------------------------------------------------------------------------
# Test #6: Document becomes Ready
# ---------------------------------------------------------------------------
@pytest.mark.contract
def test_06_document_becomes_ready(user_client: ApiClient, persian_pdf_path: Any):
    """Uploaded PDF reaches Ready status within timeout with pageCount >= 1 and chunkCount >= 1."""
    col_id = create_collection(user_client, "ready")
    resp = upload_document(user_client, col_id, persian_pdf_path, "persian.pdf")
    assert resp.status_code == 202
    doc_id = resp.json()["id"]

    doc_data = user_client.wait_until_ready(doc_id, timeout=120.0)
    assert doc_data.get("status") == "Ready"
    assert doc_data.get("pageCount") is not None and doc_data["pageCount"] >= 1
    assert doc_data.get("chunkCount") is not None and doc_data["chunkCount"] >= 1


# ---------------------------------------------------------------------------
# Test #7: Upload text file named .pdf
# ---------------------------------------------------------------------------
@pytest.mark.contract
def test_07_upload_text_file_named_pdf(user_client: ApiClient, not_pdf_path: Any):
    """Uploading non-PDF file renamed as .pdf is rejected with 400 document.invalid_file."""
    col_id = create_collection(user_client, "invalid_ext")
    resp = upload_document(user_client, col_id, not_pdf_path, "fake.pdf")
    assert resp.status_code == 400, f"Expected 400, got {resp.status_code}: {resp.text}"
    data = resp.json()
    assert data.get("code") == "document.invalid_file"


# ---------------------------------------------------------------------------
# Test #8: Upload empty file / corrupt PDF
# ---------------------------------------------------------------------------
@pytest.mark.contract
def test_08_upload_empty_or_corrupt_file(user_client: ApiClient, empty_pdf_path: Any, corrupt_pdf_path: Any):
    """Empty or corrupt PDFs return 400 or end in status Failed with errorMessage."""
    col_id = create_collection(user_client, "corrupt_empty")

    # 1. Empty file
    resp_empty = upload_document(user_client, col_id, empty_pdf_path, "empty.pdf")
    if resp_empty.status_code == 400:
        assert resp_empty.json().get("code") == "document.invalid_file"
    elif resp_empty.status_code == 202:
        doc_id = resp_empty.json()["id"]
        # Poll until Failed or Ready
        failed_or_ready = False
        for _ in range(30):
            d = user_client.get(f"/documents/{doc_id}").json()
            if d.get("status") == "Failed":
                assert d.get("errorMessage"), "Failed document must include non-empty errorMessage"
                failed_or_ready = True
                break
            time.sleep(1.0)
        assert failed_or_ready, "Empty file document remained stuck in processing"
    else:
        pytest.fail(f"Unexpected status for empty file upload: {resp_empty.status_code}")

    # 2. Corrupt file
    resp_corrupt = upload_document(user_client, col_id, corrupt_pdf_path, "corrupt.pdf")
    if resp_corrupt.status_code == 400:
        assert resp_corrupt.json().get("code") == "document.invalid_file"
    elif resp_corrupt.status_code == 202:
        doc_id = resp_corrupt.json()["id"]
        failed_or_ready = False
        for _ in range(30):
            d = user_client.get(f"/documents/{doc_id}").json()
            if d.get("status") == "Failed":
                assert d.get("errorMessage"), "Corrupt document must include non-empty errorMessage"
                failed_or_ready = True
                break
            time.sleep(1.0)
        assert failed_or_ready, "Corrupt file document remained stuck in processing"
    else:
        pytest.fail(f"Unexpected status for corrupt file upload: {resp_corrupt.status_code}")


# ---------------------------------------------------------------------------
# Test #9: Upload > limit
# ---------------------------------------------------------------------------
@pytest.mark.contract
def test_09_upload_too_large(user_client: ApiClient, oversized_bytes_stream: io.BytesIO):
    """Uploading files larger than configured storage limit returns 413 document.too_large."""
    col_id = create_collection(user_client, "large")
    resp = upload_document(user_client, col_id, oversized_bytes_stream, "oversized.pdf")
    assert resp.status_code == 413, f"Expected 413 Payload Too Large, got {resp.status_code}: {resp.text}"
    data = resp.json()
    assert data.get("code") == "document.too_large"


# ---------------------------------------------------------------------------
# Test #10: Upload duplicate file
# ---------------------------------------------------------------------------
@pytest.mark.contract
def test_10_upload_duplicate_file(user_client: ApiClient, english_pdf_path: Any):
    """Uploading the same file twice to the same collection returns 409 document.duplicate."""
    col_id = create_collection(user_client, "dup")

    # First upload -> 202
    resp1 = upload_document(user_client, col_id, english_pdf_path, "reg1.pdf")
    assert resp1.status_code == 202

    # Second upload of same content -> 409
    resp2 = upload_document(user_client, col_id, english_pdf_path, "reg2.pdf")
    assert resp2.status_code == 409, f"Expected 409 Duplicate, got {resp2.status_code}: {resp2.text}"
    data = resp2.json()
    assert data.get("code") == "document.duplicate"


# ---------------------------------------------------------------------------
# Test #11: Ask matching question
# ---------------------------------------------------------------------------
@pytest.mark.contract
def test_11_ask_matching_question(user_client: ApiClient, persian_pdf_path: Any):
    """Question matching sample PDF words returns 200, valid answer, and accurate source citations."""
    col_id = create_collection(user_client, "ask_match")
    upload_resp = upload_document(user_client, col_id, persian_pdf_path, "amozesh.pdf")
    assert upload_resp.status_code == 202
    user_client.wait_until_ready(upload_resp.json()["id"])

    ask_payload = {
        "question": "حداکثر تعداد واحد مجاز در هر نیم‌سال چند واحد است؟",
        "collectionIds": [col_id],
        "topK": 5,
    }
    ask_resp = user_client.post("/ask", json=ask_payload)
    assert ask_resp.status_code == 200, f"Ask failed: {ask_resp.text}"

    data = ask_resp.json()
    assert data.get("notFound") is False
    assert data.get("degraded") is False
    assert data.get("answer") and len(data["answer"].strip()) > 0

    sources = data.get("sources", [])
    assert len(sources) >= 1
    assert sources[0].get("fileName") == "amozesh.pdf"
    assert sources[0].get("page", 0) >= 1
    assert sources[0].get("snippet")

    # Citation mapping check: every [n] in answer corresponds to sources[n-1]
    citations = re.findall(r"\[(\d+)\]", data["answer"])
    for cite_num in citations:
        idx = int(cite_num)
        assert 1 <= idx <= len(sources), f"Citation [{idx}] out of range of sources (length {len(sources)})"


# ---------------------------------------------------------------------------
# Test #12: Ask nonsense question
# ---------------------------------------------------------------------------
@pytest.mark.contract
def test_12_ask_nonsense_question(user_client: ApiClient, persian_pdf_path: Any):
    """Unrelated question returns 200 with notFound=true and refusal sentence."""
    col_id = create_collection(user_client, "ask_nonsense")
    upload_resp = upload_document(user_client, col_id, persian_pdf_path, "amozesh.pdf")
    assert upload_resp.status_code == 202
    user_client.wait_until_ready(upload_resp.json()["id"])

    ask_payload = {
        "question": "طرز تهیه قورمه سبزی با لوبیا چیتی چگونه است؟",
        "collectionIds": [col_id],
    }
    ask_resp = user_client.post("/ask", json=ask_payload)
    assert ask_resp.status_code == 200, f"Ask failed: {ask_resp.text}"

    data = ask_resp.json()
    assert data.get("notFound") is True
    # Refusal sentence per SPEC-PERSIAN-AND-RAG.md §7.2
    expected_refusal = "در اسناد موجود پاسخی برای این پرسش پیدا نشد"
    assert expected_refusal in (data.get("answer") or "")


# ---------------------------------------------------------------------------
# Test #13: ACL cross-user isolation
# ---------------------------------------------------------------------------
@pytest.mark.contract
def test_13_acl_cross_user_isolation(
    user_client: ApiClient,
    second_user_client: ApiClient,
    persian_pdf_path: Any,
):
    """User B cannot read, download, delete, or query User A's collection (returns 404)."""
    # User A creates collection and document
    col_A = create_collection(user_client, "userA_priv")
    up_resp = upload_document(user_client, col_A, persian_pdf_path, "secret.pdf")
    assert up_resp.status_code == 202
    doc_A_id = up_resp.json()["id"]

    # User B attempts operations on User A's collection -> 404 (never 200 or 403)
    resp_get_col = second_user_client.get(f"/collections/{col_A}")
    assert resp_get_col.status_code == 404, "Leaked collection existence to non-member"

    resp_get_docs = second_user_client.get(f"/collections/{col_A}/documents")
    assert resp_get_docs.status_code == 404

    resp_get_doc = second_user_client.get(f"/documents/{doc_A_id}")
    assert resp_get_doc.status_code == 404

    resp_download = second_user_client.get(f"/documents/{doc_A_id}/file")
    assert resp_download.status_code == 404

    resp_del_doc = second_user_client.delete(f"/documents/{doc_A_id}")
    assert resp_del_doc.status_code == 404

    resp_ask = second_user_client.post(
        "/ask",
        json={"question": "تعداد واحد مجاز؟", "collectionIds": [col_A]},
    )
    assert resp_ask.status_code == 404


# ---------------------------------------------------------------------------
# Test #14: ACL role permissions
# ---------------------------------------------------------------------------
@pytest.mark.contract
def test_14_acl_role_permissions(
    user_client: ApiClient,
    second_user_client: ApiClient,
    english_pdf_path: Any,
):
    """Viewer cannot upload/delete; Editor can upload; Owner manages members."""
    # User A creates collection
    col_id = create_collection(user_client, "roles_col")
    user2_email = "user_secondary@conformance.local"

    # Owner adds User B as Viewer
    add_viewer = user_client.put(f"/collections/{col_id}/members", json={"email": user2_email, "role": "Viewer"})
    assert add_viewer.status_code == 200

    # User B (Viewer) cannot upload -> 403
    viewer_upload = upload_document(second_user_client, col_id, english_pdf_path, "v_upload.pdf")
    assert viewer_upload.status_code == 403

    # Owner promotes User B to Editor
    promote_editor = user_client.put(f"/collections/{col_id}/members", json={"email": user2_email, "role": "Editor"})
    assert promote_editor.status_code == 200

    # User B (Editor) can upload -> 202
    editor_upload = upload_document(second_user_client, col_id, english_pdf_path, "ed_upload.pdf")
    assert editor_upload.status_code == 202

    # User B (Editor) cannot add members -> 403
    editor_add_member = second_user_client.put(
        f"/collections/{col_id}/members",
        json={"email": "intruder@conformance.local", "role": "Viewer"},
    )
    assert editor_add_member.status_code == 403

    # Owner lists members
    members_resp = user_client.get(f"/collections/{col_id}/members")
    assert members_resp.status_code == 200
    members = members_resp.json()
    user2_record = next((m for m in members if m.get("email") == user2_email), None)
    assert user2_record is not None

    # Owner removes User B
    remove_resp = user_client.delete(f"/collections/{col_id}/members/{user2_record['userId']}")
    assert remove_resp.status_code == 204


# ---------------------------------------------------------------------------
# Test #15: Ask with mixed collection scope
# ---------------------------------------------------------------------------
@pytest.mark.contract
def test_15_ask_mixed_collection_scope(
    user_client: ApiClient,
    second_user_client: ApiClient,
    persian_pdf_path: Any,
    english_pdf_path: Any,
):
    """Asking with mixed readable and unreadable collections only returns hits from readable ones."""
    # User A collection with Persian PDF
    col_A = create_collection(user_client, "colA_mixed")
    upA = upload_document(user_client, col_A, persian_pdf_path, "amozesh_A.pdf")
    user_client.wait_until_ready(upA.json()["id"])

    # User B collection with English PDF
    col_B = create_collection(second_user_client, "colB_mixed")
    upB = upload_document(second_user_client, col_B, english_pdf_path, "regulations_B.pdf")
    second_user_client.wait_until_ready(upB.json()["id"])

    # User B asks with both [col_A, col_B]
    mixed_ask = second_user_client.post(
        "/ask",
        json={
            "question": "What is the maximum number of credits allowed?",
            "collectionIds": [col_A, col_B],
        },
    )
    assert mixed_ask.status_code == 200, f"Mixed ask failed: {mixed_ask.text}"
    data = mixed_ask.json()

    # Sources MUST NOT leak document from col_A
    for src in data.get("sources", []):
        assert src.get("fileName") != "amozesh_A.pdf", "Data leakage from unreadable collection col_A!"


# ---------------------------------------------------------------------------
# Test #16: Delete document cascades and purges
# ---------------------------------------------------------------------------
@pytest.mark.contract
def test_16_delete_document_cascades(user_client: ApiClient, english_pdf_path: Any):
    """Deleting document returns 204, purges metadata, file download, and vector chunks."""
    col_id = create_collection(user_client, "del_cascade")
    up_resp = upload_document(user_client, col_id, english_pdf_path, "to_delete.pdf")
    doc_id = up_resp.json()["id"]
    user_client.wait_until_ready(doc_id)

    # Delete
    del_resp = user_client.delete(f"/documents/{doc_id}")
    assert del_resp.status_code == 204

    # Subsequent GET -> 404
    assert user_client.get(f"/documents/{doc_id}").status_code == 404
    assert user_client.get(f"/documents/{doc_id}/file").status_code == 404

    # Ask no longer returns the deleted document
    ask_resp = user_client.post(
        "/ask",
        json={"question": "credits per semester", "collectionIds": [col_id]},
    )
    assert ask_resp.status_code == 200
    for src in ask_resp.json().get("sources", []):
        assert src.get("documentId") != doc_id


# ---------------------------------------------------------------------------
# Test #17: Degraded mode
# ---------------------------------------------------------------------------
@pytest.mark.degraded
def test_17_degraded_mode(user_client: ApiClient, persian_pdf_path: Any):
    """When LLM provider fails, /ask returns 200 with degraded=true, answer=null, and sources."""
    col_id = create_collection(user_client, "degraded")
    up_resp = upload_document(user_client, col_id, persian_pdf_path, "degraded_doc.pdf")
    user_client.wait_until_ready(up_resp.json()["id"])

    resp = user_client.post(
        "/ask",
        json={"question": "تعداد واحد مجاز در ترم؟", "collectionIds": [col_id]},
    )
    assert resp.status_code == 200
    data = resp.json()
    assert data.get("degraded") is True
    assert data.get("answer") is None
    assert len(data.get("sources", [])) >= 1


# ---------------------------------------------------------------------------
# Test #18: Input validation errors
# ---------------------------------------------------------------------------
@pytest.mark.contract
def test_18_validation_errors(user_client: ApiClient):
    """Model validation errors return 400 problem+json with code validation.failed."""
    col_id = create_collection(user_client, "val_col")

    # 1. Empty question
    resp_empty_q = user_client.post("/ask", json={"question": "   ", "collectionIds": [col_id]})
    assert resp_empty_q.status_code == 400
    assert resp_empty_q.json().get("code") == "validation.failed"

    # 2. topK = 0
    resp_zero_k = user_client.post(
        "/ask",
        json={"question": "معتبر است؟", "collectionIds": [col_id], "topK": 0},
    )
    assert resp_zero_k.status_code == 400
    assert resp_zero_k.json().get("code") == "validation.failed"

    # 3. 21 collection IDs (over limit of 20)
    cols_21 = [str(uuid.uuid4()) for _ in range(21)]
    resp_21 = user_client.post("/ask", json={"question": "معتبر است؟", "collectionIds": cols_21})
    assert resp_21.status_code == 400
    assert resp_21.json().get("code") == "validation.failed"


# ---------------------------------------------------------------------------
# Test #19: Rate limiting on /auth/login
# ---------------------------------------------------------------------------
@pytest.mark.contract
def test_19_rate_limiting(base_url: str):
    """Rapid burst of login requests eventually triggers 429 rate.limited with Retry-After header."""
    client = ApiClient(base_url)
    got_429 = False

    # Rapid burst of 15 requests (limit is 10 req/min/IP)
    for _ in range(15):
        resp = client.post("/auth/login", json={"email": "spam@test.local", "password": "AnyPassword123!"})
        if resp.status_code == 429:
            got_429 = True
            data = resp.json()
            assert data.get("code") == "rate.limited"
            assert "Retry-After" in resp.headers or "retry-after" in resp.headers
            break

    assert got_429, "Rate limit 429 was not triggered during login request burst"


# ---------------------------------------------------------------------------
# Test #20: Persian normalization round-trip
# ---------------------------------------------------------------------------
@pytest.mark.contract
def test_20_persian_normalization_roundtrip(user_client: ApiClient, persian_pdf_path: Any):
    """Queries with ASCII digits, Arabic Kaf/Yeh, and space instead of ZWNJ still retrieve target chunks."""
    col_id = create_collection(user_client, "persian_norm")
    up_resp = upload_document(user_client, col_id, persian_pdf_path, "amozesh_norm.pdf")
    user_client.wait_until_ready(up_resp.json()["id"])

    # Query uses:
    # - Arabic Yeh: ك, ي
    # - ASCII digits: 20
    # - Space instead of ZWNJ: نيم سال (instead of نیم‌سال)
    norm_query = "حداكثر تعداد واحد مجاز در هر نيم سال تحصيلي 20 واحد است؟"
    ask_resp = user_client.post(
        "/ask",
        json={"question": norm_query, "collectionIds": [col_id]},
    )
    assert ask_resp.status_code == 200, f"Persian norm query failed: {ask_resp.text}"
    data = ask_resp.json()
    assert data.get("notFound") is False, "Normalization failed to match document chunk"
    assert len(data.get("sources", [])) >= 1


# ---------------------------------------------------------------------------
# Test #21: Admin authorization & reindex
# ---------------------------------------------------------------------------
@pytest.mark.contract
def test_21_admin_endpoints(user_client: ApiClient, admin_client: ApiClient):
    """Non-admin gets 403 on admin routes; admin reindex creates job that reaches Completed."""
    col_id = create_collection(user_client, "admin_test")

    # Regular user calls admin reindex -> 403 auth.forbidden
    user_attempt = user_client.post(f"/admin/collections/{col_id}/reindex")
    assert user_attempt.status_code == 403
    assert user_attempt.json().get("code") == "auth.forbidden"

    # Admin calls reindex -> 202 Accepted { jobId: ... }
    admin_resp = admin_client.post(f"/admin/collections/{col_id}/reindex")
    assert admin_resp.status_code == 202, f"Admin reindex failed: {admin_resp.text}"
    job_id = admin_resp.json().get("jobId")
    assert job_id

    # Poll reindex job status
    completed = False
    for _ in range(30):
        job_status = admin_client.get(f"/admin/reindex-jobs/{job_id}").json()
        if job_status.get("status") == "Completed":
            completed = True
            break
        time.sleep(1.0)
    assert completed, "Admin reindex job did not reach Completed status"


# ---------------------------------------------------------------------------
# Test #22: Error hygiene
# ---------------------------------------------------------------------------
@pytest.mark.contract
def test_22_error_hygiene(user_client: ApiClient):
    """Error responses adhere to problem+json and leak no stack traces or filesystem paths."""
    resp = user_client.get(f"/collections/{uuid.uuid4()}")
    assert resp.status_code == 404

    # Headers
    content_type = resp.headers.get("Content-Type", "")
    assert "application/problem+json" in content_type or "application/json" in content_type

    # Body checks
    text = resp.text
    assert "at System." not in text, "Leaked .NET stack trace in error response"
    assert "at DocSearch." not in text, "Leaked application stack trace in error response"
    assert "C:\\" not in text, "Leaked Windows file path in error response"
    assert "/app/" not in text and "/src/" not in text, "Leaked Linux path in error response"


# ---------------------------------------------------------------------------
# Test #23: Idempotent reprocess
# ---------------------------------------------------------------------------
@pytest.mark.contract
def test_23_idempotent_reprocess(user_client: ApiClient, persian_pdf_path: Any):
    """POST /documents/{id}/reprocess leaves document Ready with identical chunk count."""
    col_id = create_collection(user_client, "reprocess")
    up_resp = upload_document(user_client, col_id, persian_pdf_path, "doc_rep.pdf")
    doc_id = up_resp.json()["id"]

    initial_doc = user_client.wait_until_ready(doc_id)
    initial_chunks = initial_doc.get("chunkCount")
    assert initial_chunks and initial_chunks >= 1

    # Reprocess
    rep_resp = user_client.post(f"/documents/{doc_id}/reprocess")
    assert rep_resp.status_code == 202

    # Wait until Ready again
    reprocessed_doc = user_client.wait_until_ready(doc_id)
    assert reprocessed_doc.get("status") == "Ready"
    assert reprocessed_doc.get("chunkCount") == initial_chunks, (
        f"Chunk count changed after reprocess: was {initial_chunks}, now {reprocessed_doc.get('chunkCount')}"
    )


# ---------------------------------------------------------------------------
# Test #24: Multi-turn conversation
# ---------------------------------------------------------------------------
@pytest.mark.contract
def test_24_multi_turn_conversation(user_client: ApiClient, persian_pdf_path: Any):
    """Two /ask calls with conversationId produce 4 ordered messages in conversation history."""
    col_id = create_collection(user_client, "conv")
    up_resp = upload_document(user_client, col_id, persian_pdf_path, "amozesh_conv.pdf")
    user_client.wait_until_ready(up_resp.json()["id"])

    # Turn 1
    resp1 = user_client.post(
        "/ask",
        json={"question": "حداکثر واحد مجاز در هر ترم چند است؟", "collectionIds": [col_id]},
    )
    assert resp1.status_code == 200
    conv_id = resp1.json().get("conversationId")
    assert conv_id

    # Turn 2
    resp2 = user_client.post(
        "/ask",
        json={
            "question": "در صورت احراز معدل الف چطور؟",
            "collectionIds": [col_id],
            "conversationId": conv_id,
        },
    )
    assert resp2.status_code == 200
    assert resp2.json().get("conversationId") == conv_id

    # Retrieve conversation messages
    msgs_resp = user_client.get(f"/conversations/{conv_id}/messages")
    assert msgs_resp.status_code == 200
    messages = msgs_resp.json()
    if isinstance(messages, dict) and "items" in messages:
        messages = messages["items"]

    assert len(messages) == 4, f"Expected 4 messages in conversation history, got {len(messages)}"
    expected_roles = ["user", "assistant", "user", "assistant"]
    for i, expected_role in enumerate(expected_roles):
        assert messages[i].get("role") == expected_role, f"Message {i} role mismatch: {messages[i]}"
