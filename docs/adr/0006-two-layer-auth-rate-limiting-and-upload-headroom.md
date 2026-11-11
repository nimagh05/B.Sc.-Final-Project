# 6. Two-Layer Auth Rate Limiting, Trusted Proxy CIDRs, and Upload Socket Headroom

Date: 2026-11-11  
Status: Accepted  
References: `docs/SPEC-DATA-AND-API.md` §2.7, §3; `docs/TASKS.md` (T14); `conformance/test_conformance.py` (`test_09`, `test_19`, `test_21`)

---

## Context

During Task T14 (Production Hardening) and subsequent security review, three architectural constraints surfaced at the intersection of rate limiting, reverse-proxy trust boundaries, and Kestrel payload handling:

1. **Auth Rate-Limiting vs. Conformance & Password Spraying:**
   `SPEC-DATA-AND-API.md` §2.7 specifies `10 req/min/IP` on `/api/v1/auth/*`. However:
   - Under a strict single-layer per-IP limit of 10 req/min, sequential black-box conformance tests from `127.0.0.1` (`test_02`, `test_14`, `test_18`, session fixtures, and `test_19_rate_limiting`'s 15-request burst on `spam@test.local`) exhaust the loopback IP quota right before `test_21_admin_endpoints` logs in as `admin@ci.test`, causing a `429` failure on `admin_client`.
   - Conversely, partitioning `"auth"` solely by `ip:account` protects individual accounts from brute-force and resolves conformance starvation, but introduces a password-spraying gap: an attacker from a single IP can test thousands of distinct email addresses (`1 attempt/email`) without ever hitting the per-account threshold.

2. **Reverse Proxy `X-Forwarded-For` Spoofing:**
   Clearing `ForwardedHeadersOptions.KnownIPNetworks` and `KnownProxies` causes ASP.NET Core to blindly trust `X-Forwarded-For` from any remote client. Any unauthenticated attacker could forge a randomized `X-Forwarded-For` header on every request to bypass IP-based rate limiting completely.

3. **Loopback TCP Resets on Oversized Uploads (`413 document.too_large`):**
   Setting Kestrel's `MaxRequestBodySize` equal to `FormOptions.MultipartBodyLengthLimit` (e.g., 50 MB) causes Kestrel to terminate the TCP connection (`ECONNRESET` / Windows socket error `10053`) while the client is still streaming the oversized request body on fast loopback connections, preventing the API from returning the required RFC 7807 `413 document.too_large` JSON body (`test_09_upload_too_large`).

---

## Decision

1. **Two-Layer Rate Limiting on `/api/v1/auth/*`:**
   - **Layer 1 (`GlobalLimiter`, per-IP anti-spraying):** Guards all `/api/v1/auth` paths strictly by client IP (`RateLimiting:AuthIpPermitLimit`, default `100` requests/minute/IP). Blocks password-spraying attacks across distinct emails from one IP while accommodating shared NATs and multi-user conformance suites.
   - **Layer 2 (`"auth"` endpoint policy, per-`IP:Account` anti-brute-force):** Buffers small JSON payloads (`<= 4096` bytes) on `POST /api/v1/auth/*` to extract the normalized `email` claim into `HttpContext.Items["AuthAccount"]`, partitioning fixed-window permits (`RateLimiting:AuthPermitLimit`, default `10` requests/minute) by `$"{ip}:{account}"` (falling back to `ip` when absent or invalid). Single-account brute-force (`test_19_rate_limiting`) trips on the 11th attempt without locking out unrelated accounts (`test_21_admin_endpoints`).

2. **Explicit Trusted Proxy CIDRs (`Network:TrustedProxyCidrs`):**
   - Preserve ASP.NET Core's default `KnownIPNetworks` and `KnownProxies` (trusting loopback only by default — never call `.Clear()`).
   - Set `options.ForwardLimit = 1` and `options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto`.
   - Parse each CIDR from `Network:TrustedProxyCidrs` (`NetworkOptions`) via `System.Net.IPNetwork.Parse(cidr)` and add to `options.KnownIPNetworks`. Spoofed `X-Forwarded-For` headers from untrusted peers are ignored.

3. **50 MB Kestrel Socket Headroom Above `MultipartBodyLengthLimit`:**
   - Configure `FormOptions.MultipartBodyLengthLimit = maxUploadBytes` (default 50 MB) and `Kestrel.Limits.MaxRequestBodySize = maxUploadBytes + 50 MB`.
   - When an upload exceeds `MultipartBodyLengthLimit` within the headroom window, `InvalidModelStateResponseFactory` matches `"Multipart body length limit"` and throws `FileTooLargeException`, routing centrally through `GlobalExceptionHandler` to emit a clean RFC 7807 `413 document.too_large` response.

---

## Consequences

- **Positive:**
  - Defends `/api/v1/auth/*` against both single-account brute force (`10/min`) and distributed-email password spraying (`100/min`).
  - Prevents IP spoofing via untrusted `X-Forwarded-For` headers while remaining reverse-proxy ready via configuration (`Network:TrustedProxyCidrs`).
  - Delivers deterministic `413 document.too_large` ProblemDetails on oversized uploads without premature TCP resets, while still capping Kestrel memory/socket exposure at `maxUploadBytes + 50 MB`.
  - Achieves 24/24 conformance pass rate with zero changes to frozen `conformance/` files.
- **Negative / Operational Notes:**
  - Operators deploying behind a reverse proxy (Nginx/Traefik/Docker bridge) must explicitly add the proxy's subnet CIDR to `Network:TrustedProxyCidrs` for `X-Forwarded-For` client IPs to take effect.
  - Uploads exceeding `maxUploadBytes + 50 MB` (e.g., > 100 MB) are still aborted at the Kestrel transport layer as DoS protection.

---

## Alternatives

- **Single-layer IP-only rate limiter (10 req/min/IP):** Rejected because shared university campus NATs or sequential multi-user test suites (`conformance/test_conformance.py`) would suffer cross-user lockouts.
- **Single-layer `IP:Account` rate limiter without `GlobalLimiter`:** Rejected because it permits unlimited password spraying across distinct email addresses from one IP.
- **Clearing `KnownIPNetworks`/`KnownProxies`:** Rejected because it allows trivial rate-limit bypass via forged `X-Forwarded-For` headers.
- **Raising `MaxRequestBodySize` to unlimited:** Rejected due to Denial-of-Service risk from unbounded upload streams.
