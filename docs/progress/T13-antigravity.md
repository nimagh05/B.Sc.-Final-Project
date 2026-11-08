# Task T13 Handoff — Auth, Collections, and ACL

## Summary of Completed Work
1. **Role Model Separation**:
   - ASP.NET Core Identity system roles (`Admin`, `User`) are explicitly separated from collection roles (`CollectionRole`: `Viewer`, `Editor`, `Owner`).
   - Created `AdminBootstrapSeeder` to bootstrap roles `Admin` and `User` and create/promote the configured admin user on startup idempotently (`Auth:BootstrapAdmin:Email` / `Auth:AdminEmail`).
2. **JWT Configuration & Middleware ProblemDetails**:
   - Implemented `JwtOptions` with startup validation ensuring `SigningKey` >= 32 bytes in Production, binding from `Jwt:SigningKey` and `Auth:Jwt:SigningKey`.
   - Added JWT Bearer authentication and authorization with custom `JwtBearerEvents` (`OnChallenge` returning 401 `auth.unauthorized` and `OnForbidden` returning 403 `auth.forbidden` in RFC 7807 `application/problem+json` format).
   - Removed `DevCurrentUser.cs` and replaced it with claims-backed `CurrentUser : ICurrentUser`.
   - Created `JwtTokenGenerator : IJwtTokenGenerator`.
3. **Centralized ACL Logic in `IAccessService`**:
   - Extended `IAccessService` with `GetCollectionRoleAsync` and `EnsureAccessAsync`.
   - Implemented `EnsureAccessAsync` in `AccessService`:
     - Returns `404 resource.not_found` if collection does not exist (even for `Admin`).
     - Treats `Admin` effective role as `CollectionRole.Owner` across all existing collections.
     - Enforces non-enumeration rule: non-members receive `404 Not Found` (never `403 Forbidden`).
     - Members with insufficient role receive `403 auth.forbidden`.
   - Removed private `EnsureUserHasCollectionRoleAsync` from `DocumentsController` and refactored all collection and document endpoints to use `_accessService.EnsureAccessAsync`.
4. **Controllers & Endpoints Implemented**:
   - `AuthController`: `POST /api/v1/auth/register`, `POST /api/v1/auth/login`, `GET /api/v1/auth/me`.
   - `CollectionsController`: CRUD + member management (`GET`, `POST`, `GET {id}`, `PUT {id}`, `DELETE {id}` cascading physical file deletion, `GET {id}/members`, `PUT {id}/members`, `DELETE {id}/members/{userId}`).
   - `AdminController`: `POST /api/v1/admin/collections/{id}/reindex`, `GET /api/v1/admin/reindex-jobs/{jobId}`.
   - `DocumentsController`, `AskController`, `ConversationsController` secured with `[Authorize]`.
5. **Testing & Verification**:
   - `ApiTestFixture` now mints test JWT tokens and configures default Bearer auth in `ApiWebApplicationFactory`, ensuring backward compatibility for existing integration tests. Added `SeedUserAsync` helper to populate users in the PostgreSQL container for foreign key integrity.
   - Added `RouteAuthorizationReflectionTests` verifying that all endpoints in `DocSearch.Api` require authorization except the explicit allow-list (`/health/live`, `/health/ready`, `/api/v1/auth/login`, `/api/v1/auth/register`).
   - Added `OptionsValidationTests` verifying `JwtOptions.SigningKey` validation.
   - Added `AuthControllerIntegrationTests` and `AclAndSecurityIntegrationTests`.
   - Annotated Domain enums with `[JsonConverter(typeof(JsonStringEnumConverter))]` for string serialization/deserialization.
   - 100% of integration and unit tests pass across all projects (352 tests passed, 0 failed).
   - Verified live conformance suite against the running API: 23 passed, 1 failed (only `test_19_rate_limiting` which is in T14 scope). 100% of tests in T13 scope passed.

## Verification Commands & Outputs
```powershell
dotnet format --verify-no-changes
dotnet build -warnaserror
dotnet test --filter "Category!=RequiresOllama"

# Conformance suite
$env:BASE_URL="http://127.0.0.1:5080"; .venv\Scripts\pytest.exe conformance/test_conformance.py -v
```

Output:
- `DocSearch.Application.Tests`: Passed! - Failed: 0, Passed: 177, Skipped: 0, Total: 177
- `DocSearch.Infrastructure.Tests`: Passed! - Failed: 0, Passed: 121, Skipped: 0, Total: 121
- `DocSearch.Api.Tests`: Passed! - Failed: 0, Passed: 54, Skipped: 0, Total: 54
- Total tests executed: 352 passed, 0 failed.
- Conformance Suite: 23 passed, 1 failed (`test_19_rate_limiting` scheduled for T14).
- Testcontainers cleanup verified: no orphaned containers remaining in `docker ps`.
