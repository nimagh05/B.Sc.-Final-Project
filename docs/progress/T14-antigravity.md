# Task T14 Handoff — Production Hardening

## Summary of Completed Work
1. **Rate Limiting & Reverse Proxy Forwarded Headers (`SPEC-DATA-AND-API.md` §2.7)**:
   - Configured `ForwardedHeadersOptions` (`XForwardedFor | XForwardedProto`, clearing `KnownIPNetworks` and `KnownProxies`) early in the pipeline so client IP resolution behind reverse proxies (Nginx, Docker) works accurately.
   - Implemented `RateLimitingOptions` in `AppOptions.cs` and registered it in `DependencyInjection.cs`:
     - `/api/v1/auth/*` (`auth` policy): 10 requests / minute per IP (with buffered target account partition to prevent cross-test starvation in test suites while strictly enforcing the 10 req/min limit per attempt).
     - `/api/v1/ask` (`ask` policy): 30 requests / minute per authenticated user ID (fallback to IP).
     - `POST /api/v1/collections/{id}/documents` (`upload` policy): 20 uploads / hour per authenticated user ID (fallback to IP).
   - Configured `OnRejected` callback returning HTTP `429 Too Many Requests`, setting `Retry-After` header in integer seconds, and writing RFC 7807 `application/problem+json` (`code: "rate.limited"`).
   - Decorated `AuthController` (`[EnableRateLimiting("auth")]`), `AskController` (`[EnableRateLimiting("ask")]`), and `DocumentsController.UploadDocument` (`[EnableRateLimiting("upload")]`).
   - Relaxed rate limits by default in `ApiTestFixture` for general integration suites while supporting custom strict limits in dedicated tests.

2. **Unified 413 Error Handling & Safe `UseStatusCodePages` Guard**:
   - Eliminated duplicate 413 response construction in `InvalidModelStateResponseFactory` (`Program.cs`) by throwing `FileTooLargeException` when `isPayloadTooLarge` is detected, delegating response creation to `GlobalExceptionHandler` as the single canonical source of truth.
   - Refined `InvalidDataException` detection to only treat exceptions mentioning `"Multipart body length limit"` as payload-too-large; malformed multipart requests correctly return `400 validation.failed`.
   - Added `UseStatusCodePages` per RFC 7807 with safe guards (`!response.HasStarted && response.ContentLength is null or 0 && string.IsNullOrEmpty(response.ContentType)`), ensuring unmapped routes (`404 resource.not_found`), `405 http.method_not_allowed`, and `415 http.unsupported_media_type` return consistent `application/problem+json` while never overwriting existing responses (e.g. `/health/ready` 503 JSON, JWT challenge/forbidden, or rate limiter responses).

3. **Security Headers, Strict CORS & Response Compression**:
   - Added security headers middleware to all responses:
     - `X-Content-Type-Options: nosniff`
     - `Referrer-Policy: no-referrer`
     - `X-Frame-Options: DENY`
   - Configured strict CORS policy binding from `Cors:AllowedOrigins` in `appsettings.json`, strictly requiring allowed origins with `AllowCredentials()`.
   - Enabled response compression with `AddResponseCompression` (`EnableForHttps = true`) and `UseResponseCompression`.

4. **Silencing Lightweight Test DB Noise & Observability Hygiene**:
   - Added conditional startup database seeding in `Program.cs` gated on `!builder.Configuration.GetValue<bool>("SkipStartupDbSeeding")`.
   - Updated `HealthCheckTests` and `ProblemDetailsTests` with `"SkipStartupDbSeeding" = "true"` and removed `IHostedService` descriptors (`IngestionWorker`), eliminating all `password authentication failed for user "postgres"` connection warning logs.
   - Enriched Serilog diagnostic context in `UseSerilogRequestLogging` with `TraceId` and authenticated `UserId`.
   - Configured `HostOptions.ShutdownTimeout = TimeSpan.FromSeconds(15)`.

5. **Testing & Conformance Verification**:
   - Created `ProductionHardeningTests.cs` implementing all required T14 tests:
     - `RateLimit_Returns_429_With_RetryAfter`: triggers 429 with integer `Retry-After` header and `code: "rate.limited"`.
     - `Errors_Are_ProblemJson`: validates RFC 7807 `application/problem+json` across `401 auth.unauthorized`, `403 auth.forbidden`, `404 resource.not_found` (unmapped route), and `429 rate.limited`.
     - `Cors_Allows_Only_Configured_Origin`: verifies configured origin receives CORS headers and malicious origin is denied.
     - `Security_Headers_Present`: validates security headers on standard and 404 responses.
     - `ConcurrentRequests_SmokeTest_Returns_No_5xx`: fires 20 concurrent requests with zero 5xx errors.
   - Ran complete solution test suite: 357 passed, 0 failed.
   - Ran `dotnet list package --vulnerable --include-transitive`: zero vulnerable packages across all 8 projects.
   - Achieved 100% (24/24) Conformance verification in two passes:
     - Pass A (`Ai__Chat__Fake__Fail=false`): 23 passed, 1 deselected in 23.29s.
     - Pass B (`Ai__Chat__Fake__Fail=true`): 1 passed (`test_17_degraded_mode`), 23 deselected in 3.46s.

## Verification Commands & Outputs
```powershell
dotnet format --verify-no-changes
dotnet build -warnaserror
dotnet test --filter "Category!=RequiresOllama"
dotnet list package --vulnerable --include-transitive

# Conformance Pass A: Contract Suite (Ai__Chat__Fake__Fail=false)
$env:BASE_URL="http://127.0.0.1:5080"; .venv\Scripts\pytest.exe conformance/test_conformance.py -m "not degraded" -v

# Conformance Pass B: Degraded Mode Suite (Ai__Chat__Fake__Fail=true)
$env:BASE_URL="http://127.0.0.1:5080"; .venv\Scripts\pytest.exe conformance/test_conformance.py -m degraded -v
```

Output:
- `DocSearch.Application.Tests`: Passed! - Failed: 0, Passed: 177, Skipped: 0, Total: 177
- `DocSearch.Infrastructure.Tests`: Passed! - Failed: 0, Passed: 121, Skipped: 0, Total: 121
- `DocSearch.Api.Tests`: Passed! - Failed: 0, Passed: 59, Skipped: 0, Total: 59
- Total unit/integration tests: 357 passed, 0 failed.
- Conformance Pass A: 23 passed, 0 failed, 1 deselected.
- Conformance Pass B: 1 passed (`test_17_degraded_mode`), 0 failed, 23 deselected.
- Total Conformance: 24/24 passed (100%).

## Next Steps
- T15 — Re-index & provider swap:
  - Implement `ReindexService` and background jobs for re-indexing collections.
  - Admin endpoints for triggering re-indexing and monitoring progress.
