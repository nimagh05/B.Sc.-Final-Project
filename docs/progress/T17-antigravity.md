# Task T17 Handoff Note — Antigravity Agent

**Task:** T17 — Web UI (React + Vite + TypeScript, RTL Persian)  
**Branch:** `feature/T17-web-ui`  
**Date:** 2026-11-25  
**Status:** Completed (Automated checks and live E2E browser verification complete; ready for PR/push review)

---

## 1. What Was Completed

1. **Web Application Scaffold & Self-Hosted Fonts (`web/`):**
   - Created React 19 + Vite 8 + TypeScript 7 (`~7.0.2`) + React Router 7 app in `web/` with `web/.gitignore` (`node_modules/`, `dist/`, `coverage/`, `.env.local`).
   - Added `<html lang="fa" dir="rtl">` in `web/index.html` and self-hosted standard (non-FD) `Vazirmatn-{Regular,Medium,SemiBold,Bold}.woff2` files in `web/public/fonts/` (verified `wOF2` header bytes and zero external CDN links).
   - Configured Vite dev proxy (`/api` and `/health` → `http://localhost:5000` or `VITE_API_PROXY_TARGET`) and optional `VITE_API_BASE_URL` override for direct cross-origin requests.

2. **Styling & Design System (`web/src/styles/`):**
   - `fonts.css`: `@font-face` declarations for weights `400`, `500`, `600`, `700`.
   - `tokens.css`: Minimalism & Swiss Style tokens (`#F8FAFC` canvas, `#FFFFFF` cards, `#1E3A5F` institutional navy, `#2563EB` citation blue, `#A16207` accent, accessible semantic status colors, `--density 5` spacing scale).
   - `global.css`: CSS logical properties (`margin-inline-*`, `padding-inline-*`, `inset-inline-*`, `text-align: start`), visible `:focus-visible` outlines, `bdi` (`unicode-bidi: plaintext`), responsive breakpoints for `1280×720` projector widths and `<768px` stacking, and `prefers-reduced-motion` support.

3. **Typed API Client, Error Handling & Auth (`web/src/api/`, `web/src/auth/`):**
   - `types.ts`: Mirrors backend camelCase DTOs, PascalCase `JsonStringEnumConverter` enums (`DocumentStatus`, `CollectionRole`), lowercase `ConversationMessageDto.role`, and `ProblemDetailsResponse`.
   - `errors.ts`: Parses `application/problem+json` + `429 Retry-After` and maps backend error codes to centralized Persian messages in `web/src/i18n/fa.ts`.
   - `client.ts` & `endpoints.ts`: Fetch client, authenticated PDF `Blob` downloader with 60s delayed `URL.revokeObjectURL(...)`, and `XMLHttpRequest` multipart upload (`file` field) with real progress events.
   - `tokenStorage.ts` & `AuthContext.tsx`: Stores JWT in module memory + `sessionStorage` (never `localStorage`), validates expiry, and triggers global `401` session-expiry logout **only** on requests where a Bearer token was attached (never on `/auth/login` or `/auth/register`).

4. **Pages & Components (`web/src/pages/`, `web/src/components/`, `web/src/i18n/fa.ts`):**
   - `LoginPage`: Login and registration tabs with password-policy validation, inline/alert ProblemDetails rendering, and session-expiry banner.
   - `CollectionsPage`: Paginated collection list, creation form, role badges, and direct links to management and `/chat?collectionId=...`.
   - `CollectionDetailPage`: Role-aware controls (`Viewer` / `Editor` / `Owner`), `FileDrop` drag-and-drop PDF upload + progress bar, resilient status polling (`Uploaded`/`Processing` → `Ready`/`Failed`) with consecutive-error cutoff and retry, expandable document details (`chunkCount`, `ocrPages`, `processedAt`, `errorMessage` from `GET /documents/{id}`), authenticated PDF download, reprocess, delete, and Owner member management.
   - `ChatPage`: Conversation history sidebar (select, new, delete), multi-select collections (respecting `?collectionId=` query param), question composer (`3–2000` chars), `DegradedBanner` (`degraded: true`), neutral empty-evidence notice (`notFound: true`), safe plain-text `CitationText` parsing (`[1]`, `[1][2]`, `[1, 2]`, `[1، 2]`, no `dangerouslySetInnerHTML`), and clickable `[k]` chips scrolling to/highlighting `SourceCard` with `<bdi>` + `dir="auto"` and authenticated PDF viewer button.

5. **Tests & CI (`web/src/test/`, `.github/workflows/ci.yml`):**
   - Added 5 Vitest + Testing Library test files (`14` tests total, all passing).
   - Added independent parallel `web` job to `.github/workflows/ci.yml` (`actions/setup-node@v4`, Node `24`, `npm ci`, `npx tsc --noEmit`, `npm test`, `npm run build`).

---

## 2. Live E2E Browser Verification & Remaining Follow-Ups

- **Live E2E Browser Verification (`http://localhost:5173` against `http://localhost:5000`, Tests 1–6):**
  - **0** browser console errors, **0** external CDN requests, and **0** unexpected `4xx`/`5xx` server responses.
  - Verified `<html lang="fa" dir="rtl">`, self-hosted `Vazirmatn-*.woff2` (`200`), `401` invalid-login handling without false session expiry, registration (`201`) + login (`200`) + reload persistence, collection creation, invalid PDF (`400`), valid Persian PDF (`202`) auto-polling `Uploaded` → `Processing` → `Ready`, duplicate PDF (`409`), document details (`pageCount: 1`, `chunkCount: 1`), authenticated PDF `Blob` download/open (`200`), `/chat?collectionId=...` pre-selection, `notFound` neutral banner, conversation reload/delete (`204`), `1280×720` & `390×844` responsive layouts, keyboard `Tab` focus rings, and Logout.
  - Post-E2E polish applied: right-aligned RTL placeholder on `input[type="password"]`, removed duplicate canned refusal paragraph below the `notFound` banner, forwarded `signal` in `conversationsApi.delete`, and added `overflow-x: clip` on `html, body` for mobile subpixel overflow. (Confirmed `web/src/i18n/fa.ts` already used `ندارد`, with zero first-person typos.)
- **`web/Dockerfile`:** Deferred to **Task T20** (`Packaging & documentation`) per `docs/TASKS.md`.
- **Recommended Next Task:** **T20 — Packaging & documentation** (or optional **T18 — Embeddable widget**; **T19** is already merged on `main`).

---

## 3. Verification Commands & How to Continue

```powershell
# Frontend checks (from web/)
Set-Location web
npm ci
npx tsc --noEmit
npm test
npm run build

# Backend sanity check (from repo root)
Set-Location ..
dotnet build -warnaserror
dotnet test --no-build --filter "Category!=Integration&Category!=RequiresOllama"

# Live manual E2E (two terminals from repo root)
docker compose up -d db extractor
. .\scripts\load-env.ps1
dotnet run --project src/DocSearch.Api
# Terminal 2:
Set-Location web
npm run dev
```
