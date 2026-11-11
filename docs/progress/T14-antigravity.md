# Task T14 Handoff — Production Hardening (incl. Post-Review Security & Hygiene Fixes)

## Summary of Completed Work
1. **Trusted Proxy Forwarded Headers (`NetworkOptions` & Anti-Spoofing)**:
   - Added `NetworkOptions` (`Network:TrustedProxyCidrs`) in `src/DocSearch.Application/Options/AppOptions.cs`, bound & validated on startup via `IValidatableObject` (`System.Net.IPNetwork.TryParse`) in `DependencyInjection.cs`, and configured `"Network": { "TrustedProxyCidrs": [] }` in `appsettings.json`.
   - Configured `ForwardedHeadersOptions` in `Program.cs` with `ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto` and `ForwardLimit = 1`, preserving ASP.NET Core's loopback-only default (`KnownIPNetworks` / `KnownProxies` are NOT cleared) and appending configured `TrustedProxyCidrs` via `System.Net.IPNetwork.Parse`.

2. **Two-Layer Rate Limiting (`SPEC-DATA-AND-API.md` §2.7 & ADR 0006)**:
   - Implemented `RateLimitingOptions` in `AppOptions.cs` (`AuthPermitLimit = 10`, `AuthIpPermitLimit = 100`, `AuthWindowMinutes = 1`, `AskPermitLimit = 30`, `AskWindowMinutes = 1`, `UploadPermitLimit = 20`, `UploadWindowHours = 1`).
   - **Layer 1 (`GlobalLimiter`)**: Guards `/api/v1/auth` paths strictly by client IP (`AuthIpPermitLimit = 100/min`) against distributed-email password spraying.
   - **Layer 2 (`"auth"` endpoint policy)**: Guards `/api/v1/auth/*` by `$"{ip}:{account}"` (buffered JSON `email` inspection up to 4096 bytes, fallback to `ip`) at `AuthPermitLimit = 10/min` against single-account brute force without starving sequential multi-user conformance tests.
   - Configured `"ask"` (`30/min/user`, fallback IP) and `"upload"` (`20/hr/user`, fallback IP) policies on `AskController` and `DocumentsController.UploadDocument`.
   - `OnRejected` returns HTTP `429 Too Many Requests` with integer `Retry-After` header and RFC 7807 `application/problem+json` (`code = "rate.limited"`).
   - Relaxed all rate-limit ceilings (`AuthPermitLimit = 1000`, `AuthIpPermitLimit = 10000`, `AskPermitLimit = 1000`, `UploadPermitLimit = 1000`) in `ApiTestFixture` for non-rate-limit integration suites.

3. **Delete Atomicity & Config-Driven Reindex Model**:
   - Updated `DocumentsController.DeleteDocument` and `CollectionsController.DeleteCollection` to commit DB deletion (`SaveChangesAsync(ct)`) **before** deleting physical storage files in best-effort `try/catch` blocks (`CancellationToken.None`), preventing dangling DB rows pointing to missing files.
   - Injected `IOptions<AiOptions>` into `AdminController` to use `aiOptions.Value.Embedding.Model` instead of hardcoded `"bge-m3"`.

4. **Unified 413 Error Handling & Safe `UseStatusCodePages` Guard**:
   - Eliminated duplicate 413 response construction in `InvalidModelStateResponseFactory` (`Program.cs`) by tightening `InvalidDataException` checks to `"Multipart body length limit"` and throwing `FileTooLargeException`, routing centrally through `GlobalExceptionHandler`.
   - Added `UseStatusCodePages` per RFC 7807 guarded by `!response.HasStarted && (response.ContentLength is null or 0) && string.IsNullOrEmpty(response.ContentType)`, ensuring unmapped routes (`404 resource.not_found`), `405 http.method_not_allowed`, and `415 http.unsupported_media_type` return `application/problem+json` without overwriting existing bodies.

5. **Security Headers, Strict CORS, Compression & Observability**:
   - Added security headers (`X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY`), strict CORS from `Cors:AllowedOrigins` with `AllowCredentials()`, response compression (`EnableForHttps = true`), Serilog `TraceId`/`UserId` diagnostic enrichment, and 15s `HostOptions.ShutdownTimeout`.
   - Silenced unhosted DB connection warnings in `HealthCheckTests` and `ProblemDetailsTests` via `SkipStartupDbSeeding = true` and removing `IHostedService` registrations.
   - Created `docs/adr/0006-two-layer-auth-rate-limiting-and-upload-headroom.md` and updated `README.md` (fixed code fences and `"daily structured logs"`, keeping total length at 45 lines).

6. **Tests & Fresh Verification (`fix/T14-review`)**:
   - Added `Auth_RateLimit_Blocks_PasswordSpraying_Across_Different_Emails_From_Same_Ip` alongside `RateLimit_Returns_429_With_RetryAfter`, `Errors_Are_ProblemJson`, `Cors_Allows_Only_Configured_Origin`, `Security_Headers_Present`, and `ConcurrentRequests_SmokeTest_Returns_No_5xx` in `ProductionHardeningTests.cs`.

## Verification Commands & Real Outputs
```powershell
dotnet format --verify-no-changes
dotnet build -warnaserror
dotnet test --filter "Category!=RequiresOllama" --no-build

# Conformance Pass A: Contract Suite (Ai__Chat__Fake__Fail=false)
$env:BASE_URL="http://127.0.0.1:5080"; .venv\Scripts\pytest.exe conformance/test_conformance.py -m "not degraded" -v

# Conformance Pass B: Degraded Mode Suite (Ai__Chat__Fake__Fail=true)
$env:BASE_URL="http://127.0.0.1:5080"; .venv\Scripts\pytest.exe conformance/test_conformance.py -m degraded -v
```

Measured Results (`fix/T14-review`):
- `dotnet format --verify-no-changes`: exited with code 0.
- `dotnet build -warnaserror`: `Build succeeded. 0 Warning(s), 0 Error(s)` in `00:00:39.76`.
- `DocSearch.Application.Tests`: `Passed! - Failed: 0, Passed: 177, Skipped: 0, Total: 177, Duration: 828 ms`
- `DocSearch.Infrastructure.Tests`: `Passed! - Failed: 0, Passed: 121, Skipped: 0, Total: 121, Duration: 39 s`
- `DocSearch.Api.Tests`: `Passed! - Failed: 0, Passed: 60, Skipped: 0, Total: 60, Duration: 49 s`
- Total unit/integration tests: **358 passed, 0 failed**.
- Conformance Pass A (`-m "not degraded"`): **23 passed, 1 deselected in 21.30s**.
- Conformance Pass B (`-m degraded`): **1 passed (`test_17_degraded_mode`), 23 deselected in 4.04s**.
- Total Conformance: **24/24 passed (100%)**.

## Next Steps
- **T15 — Re-index & provider swap**:
  - Implement `ReindexService` and background jobs for re-indexing collections across active chunks.
  - Ensure search filters out stale-model chunks during/after model swaps.
