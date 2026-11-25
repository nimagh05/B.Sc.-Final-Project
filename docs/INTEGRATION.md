# University Website Integration Guide

This guide explains how the university website / portal engineering team can integrate **DocSearch** into existing portals via its versioned REST API, configure authentication and CORS, and plan for an optional embeddable widget or university SSO.

---

## 1. Service Endpoints, OpenAPI & Reverse Proxy Setup

- **Base API Prefix:** `/api/v1` (camelCase JSON, UTC ISO-8601 timestamps, GUID identifiers).
- **API Discovery:**
  - OpenAPI 3.x schema: `GET /openapi/v1.json`
  - Interactive Scalar reference UI: `GET /scalar/v1`
  - Controlled by `Docs:Enabled` (default `true`; set `Docs__Enabled=false` in production if public API docs should be hidden).
- **Health Probes:**
  - `GET /health/live` → `200 {"status":"Healthy"}` (process liveness for container/orchestrator probes).
  - `GET /health/ready` → `200` or `503` with per-dependency status (`database`, `extractor`, `embedding`). Chat LLM status is intentionally excluded so external LLM outages do not trigger false container restarts.
- **Reverse Proxy & `Network:TrustedProxyCidrs` ([ADR 0006](adr/0006-two-layer-auth-rate-limiting-and-upload-headroom.md)):**
  - When deploying DocSearch behind Nginx, Caddy, Traefik, or a campus reverse proxy, `DocSearch.Api` preserves ASP.NET Core's default loopback-only proxy trust (`ForwardLimit=1`). Spoofed `X-Forwarded-For` headers from untrusted clients are ignored.
  - Add your reverse proxy's IP or subnet to `Network:TrustedProxyCidrs` (e.g. `Network__TrustedProxyCidrs__0=172.18.0.0/16` or the exact proxy host IP `10.0.1.10/32`) so `X-Forwarded-For` and `X-Forwarded-Proto` accurately populate client IPs for auth rate limiting and audit logging.
  - Ensure the proxy allows at least `client_max_body_size 64m;` (matching DocSearch's `Storage:MaxUploadMb=50` + multipart overhead) and `proxy_read_timeout 180s;` for OCR/LLM calls.

---

## 2. Authentication, Service Accounts & ACL Scoping

### JWT Bearer Flow

1. Authenticate via `POST /api/v1/auth/login`:
   ```json
   { "email": "portal-service@university.ac.ir", "password": "..." }
   ```
   Returns:
   ```json
   { "accessToken": "<jwt>", "expiresAtUtc": "2026-11-25T14:00:00Z", "roles": ["User"] }
   ```
2. Include `Authorization: Bearer <jwt>` on every protected call (`Jwt:ExpiryMinutes` defaults to `60`).

### Integrating a Public or Department-Scoped University Portal

- **Lock down self-registration:** In production university deployments where users should not create arbitrary accounts, set `Auth__AllowSelfRegistration=false`.
- **Service-account pattern (BFF / backend proxy):**
  1. Bootstrap the `Admin` account via `Auth__BootstrapAdmin__Email` / `Password`, then create collections (e.g., `آموزش کارشناسی`, `پژوهش و فناوری`, `دانشجویان بین‌الملل`).
  2. Create a dedicated portal service user and assign it the **`Viewer`** role (`PUT /api/v1/collections/{id}/members`) only on collections intended for public/student search.
  3. The university portal backend logs in, caches the JWT until shortly before `expiresAtUtc`, and proxies student questions to `POST /api/v1/ask` with explicit `collectionIds` (`1..20` GUIDs).
- **Security Guarantee:** Authorization is enforced inside PostgreSQL queries (`collection_id = ANY(@allowed)`). Requests targeting unreadable collections return `404 resource.not_found` rather than `403`, preventing collection ID enumeration.

---

## 3. Calling `/api/v1/ask` & Handling Responses

### Request (`POST /api/v1/ask`)

```json
{
  "question": "حداکثر واحد مجاز انتخابی در دوره تابستانی چند واحد است؟",
  "collectionIds": ["0196f8c0-0000-7000-8000-000000000001"],
  "conversationId": null,
  "topK": 6
}
```

- `question`: `3–2000` trimmed characters.
- `collectionIds`: `1–20` collection GUIDs.
- `conversationId`: optional; omit/`null` to start a new conversation thread, or pass the returned `conversationId` to group follow-up questions in history.

### Response Cases (`200 OK`)

| State | `notFound` | `degraded` | `answer` | `sources` | Recommended Portal UI Behavior |
|---|---|---|---|---|---|
| **Normal Answer** | `false` | `false` | Persian/English text with `[1]`, `[2]` citations | Cited excerpts (`fileName`, `page`, `sectionTitle`, `snippet`, `score`) | Render answer as **plain text** (never `innerHTML`) and link `[n]` badges to `sources`. |
| **Not Found** | `true` | `false` | Fixed Persian refusal string | `[]` (by default) | Show a neutral "no evidence found in selected regulations" notice. |
| **Degraded Mode** | `false` | `true` | `null` | Top retrieved excerpts | Display a non-fatal warning banner ("answer generator unavailable") and show the retrieved source snippets + PDF links so users still get answers directly from the regulations. |

### Error Handling (`application/problem+json`) & Rate Limits

All errors follow RFC 7807 `ProblemDetails` with a machine-readable `code` and `traceId`:

```json
{
  "type": "https://docsearch.local/errors/rate.limited",
  "title": "Too Many Requests",
  "status": 429,
  "detail": "Rate limit exceeded. Please retry later.",
  "instance": "/api/v1/ask",
  "code": "rate.limited",
  "traceId": "00-..."
}
```

Key codes: `validation.failed` (400), `auth.invalid_credentials` / `auth.unauthorized` (401), `auth.forbidden` (403), `resource.not_found` (404), `document.invalid_file` (400), `document.duplicate` / `collection.name_taken` (409), `document.too_large` (413), `rate.limited` (429 with `Retry-After` seconds header).

- **Defaults:** `/auth/*` (`10/min` per `IP:account` and `100/min` per IP), `/ask` (`30/min` per user), document upload (`20/hr` per user).
- **Important for portal BFF / Nginx:** Because `Program.cs` does not call `WithExposedHeaders("Retry-After")` on CORS, cross-origin browser `fetch` calls cannot read `Retry-After` directly. Serving DocSearch behind a **same-origin reverse proxy** (like `web/nginx.conf` mounting `/api/` on the portal origin) or proxying via the portal backend ensures `Retry-After` is always readable.

---

## 4. CORS Configuration (Cross-Origin Portals)

If a separate portal frontend (e.g. `https://portal.university.ac.ir`) calls the DocSearch API directly in the browser without a same-origin proxy:

1. Configure strict origin allow-listing (wildcards / `AllowAnyOrigin` are never permitted alongside credentials):
   ```dotenv
   Cors__AllowedOrigins__0=https://portal.university.ac.ir
   Cors__AllowedOrigins__1=https://edu.university.ac.ir
   ```
2. Use the same-origin proxy pattern (`location /api/` → `api:8080`) whenever possible so cookies/headers (`Retry-After`), CSP rules, and TLS termination remain unified on one origin.

---

## 5. Optional Future Embeddable Widget (T18 Architecture)

> [!NOTE]
> **Status:** Task **T18** (`Embeddable widget`) is an **optional unimplemented design** documented in `docs/TASKS.md` and is **not built** in the current repository.

If the university team chooses to embed a drop-in search box on static CMS pages without writing custom React code, the planned T18 pattern is:

- **Custom Web Component (`<docsearch-widget>`):** Built via Vite library mode as a single standalone ES/IIFE JS file using **Shadow DOM** (isolating RTL Persian styles and self-hosted `Vazirmatn` font tokens from the host university CSS):
  ```html
  <script type="module" src="https://docs.university.ac.ir/widget/docsearch-widget.js"></script>
  <docsearch-widget
    api-url="https://portal.university.ac.ir"
    collection-ids="0196f8c0-0000-7000-8000-000000000001"
  ></docsearch-widget>
  ```
- **Security Caution on Tokens:** Avoid embedding a static privileged JWT in public HTML attributes (`token="..."`). Instead, either have the widget request a short-lived, `Viewer`-scoped token from your portal backend (`/portal-api/docsearch-token`) or point `api-url` at a same-origin BFF proxy that attaches the service JWT server-side.

---

## 6. Future University SSO / OIDC Integration Path (S6)

DocSearch currently uses ASP.NET Core Identity + symmetric JWT Bearer tokens (`AuthController`). To integrate the university's central Identity Provider (Keycloak, CAS-to-OIDC, or Shibboleth/SAML via an OIDC broker — planned as Stretch Task **S6**):

1. **OIDC JWT Validation:** Add `Microsoft.AspNetCore.Authentication.JwtBearer` authority/metadata configuration pointing to the campus OIDC discovery endpoint (`Authority`, `Audience`), or support dual schemes (`PolicyScheme` / `AddAuthentication().AddJwtBearer("Oidc", ...)`).
2. **Claim Mapping (`ICurrentUser` & Roles):**
   - Map the IdP stable subject (`sub` or campus `employee_id`/`student_id`) to `ClaimTypes.NameIdentifier` (ensuring `AspNetUsers` / `CollectionMembers` rows are provisioned idempotently on first login).
   - Map IdP realm/group claims (e.g., `docsearch_admin`, `faculty_staff`, `students`) to global `Admin` / `User` roles and default `CollectionMember` (`Viewer` / `Editor` / `Owner`) memberships in `IAccessService`.
3. **Disable Local Password Auth:** Once SSO is verified, disable `Auth:AllowSelfRegistration=false` and route web login redirects to the IdP Authorization Code + PKCE flow while keeping the same `/api/v1/*` contract unchanged.
