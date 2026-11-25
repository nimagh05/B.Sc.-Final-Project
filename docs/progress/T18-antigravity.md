# Task T18 Handoff Note — Antigravity Agent

**Task:** T18 — Embeddable Web Component Widget (`<docsearch-widget>`)  
**Branch:** `feature/T18-embeddable-widget`  
**Date:** 2026-11-26  
**Status:** Completed (Automated tests, builds, container verification, and live cross-origin browser verification complete; committed locally on branch, ready for push/PR)

---

## 1. What Was Completed

1. **Custom Element & Stateless Client (`web/src/widget/docsearch-widget.tsx`, `web/src/widget/widgetClient.ts`):**
   - Registered `<docsearch-widget>` safely (`if (typeof customElements !== 'undefined' && !customElements.get('docsearch-widget'))`) with open Shadow DOM (`this.attachShadow({ mode: 'open' })`, `<div dir="rtl" lang="fa" class="ds-widget-root">`).
   - Supported attributes & JS properties: `api-url` (`apiUrl`), `token` (`token`), `collection-ids` (`collectionIds`), `widget-title` (`widgetTitle`, avoiding native `HTMLElement.title` tooltip collision), `placeholder` (`placeholder`), and optional `font-base-url` (`fontBaseUrl`).
   - Implemented `upgradeProperty` in `connectedCallback()` for pre-upgrade JS property assignments, `this.shadowRoot ?? this.attachShadow(...)` guard for safe disconnect/reconnect in host SPAs, and clean `root.unmount()` + abort cleanup in `disconnectedCallback()`.
   - Stored dynamic `el.token` assignments in private JS memory (`#token`) without serializing the JWT into static DOM attributes; preserved active conversation/messages across token rotation while aborting in-flight requests and resetting `conversationId` when `api-url` or `collection-ids` changes.
   - Validated `font-base-url` strictly as `http:`/`https:` and loaded `Vazirmatn` weights via `new FontFace(...)` + `document.fonts.add(...)` (feature-guarded for `jsdom`).
   - Kept widget HTTP calls (`POST /api/v1/ask`, `GET /api/v1/documents/{id}/file`) completely stateless in `widgetClient.ts` (zero `localStorage`, zero `sessionStorage`, 60s delayed `URL.revokeObjectURL` + popup-blocked `<a download>` fallback, safe `Retry-After: null` handling on cross-origin `429`).

2. **Shadow DOM Styling & Compact Persian UI (`web/src/widget/widget.css`, `web/src/widget/DocSearchWidgetApp.tsx`, `web/src/i18n/fa.ts`):**
   - Mapped design tokens from `web/src/styles/tokens.css` directly onto `:host` with `:host { all: initial; display: block; }` and explicit typography/direction/color rules on `.ds-widget-root` (`font-family: var(--font-sans)`, `direction: rtl`, `text-align: right`) plus low-specificity resets so component classes (`.btn-primary`, `.citation-chip`) retain exact colors.
   - Reused `CitationText`, `SourceCard`, `DegradedBanner`, `ErrorAlert`, `Icons`, `errors.ts`, and `fa.ts` (zero `dangerouslySetInnerHTML`, zero external CDNs).
   - Scoped citation `[1]` highlight/scroll strictly inside `shadowRoot.getElementById(...)` (with `scrollIntoView?.({ block: 'nearest', behavior: 'smooth' })`) and kept `.ds-widget-messages` internally scrollable (`max-block-size: 420px`).

3. **Single-File Vite Library Build, Container Serving, Demo & Tests:**
   - Added `web/vite.widget.config.ts` (`formats: ['iife']`, `copyPublicDir: false`, `cssCodeSplit: false`, `process.env.NODE_ENV: "production"`) and `"build:widget": "vite build --config vite.widget.config.ts"` in `web/package.json`, emitting a single self-contained file: `web/dist-widget/docsearch-widget.js` (`277.34 kB`, gzip `84.94 kB`).
   - Updated `web/Dockerfile` and `web/nginx.conf` (`location = /widget/docsearch-widget.js` with `X-Content-Type-Options`, `Referrer-Policy`, `Cache-Control`) to build and serve `/widget/docsearch-widget.js` cleanly alongside the SPA.
   - Created `web/widget-demo.html` (Persian mock portal with conflicting host CSS + in-memory config/login panel) and `web/src/test/DocSearchWidget.test.tsx` (6 test cases).
   - Updated `docs/INTEGRATION.md` § 5 and `README.md`.

---

## 2. Verification Summary

- **Frontend Checks (`web/`):**
  - `npx tsc --noEmit` → exit `0`
  - `npx tsc -p tsconfig.node.json --noEmit` → exit `0`
  - `npm test` → `Test Files  10 passed (10)`, `Tests  41 passed (41)` (includes 6 tests in `DocSearchWidget.test.tsx`)
  - `npm run build` → clean (`dist/index.html`, `dist/assets/*`)
  - `npm run build:widget` → single file `dist-widget/docsearch-widget.js` (`277,340` bytes / `277.34 kB`), zero `localStorage`, zero `sessionStorage`, zero CDN links
- **Docker Web Image:**
  - `docker compose --profile full build web` → built `docsearch-web:latest` cleanly, packaging `/usr/share/nginx/html/widget/docsearch-widget.js`
- **.NET Sanity Check:**
  - `dotnet build -warnaserror` (`0 Warning(s), 0 Error(s)`) and `dotnet test --no-build --filter "Category!=Integration&Category!=RequiresOllama"` (`Passed! - Failed: 0, Passed: 199 + 20 + 85 = 304`)
- **Live Cross-Origin Browser Verification (`http://localhost:8088/widget-demo.html` → `http://localhost:5000`):**
  - Verified against temporary isolated Postgres container + host `DocSearch.Api` (`Fake` embedding/chat providers, `Cors__AllowedOrigins__1=http://localhost:8088`).
  - Verified `OPTIONS /api/v1/ask` (`204`), `POST /api/v1/ask` (`200`), `el.token` in-memory injection (`hasAttribute('token') === false`), `[1]` citation highlight inside Shadow DOM, and visual Shadow DOM isolation (navy `#1e3a5f` white-text `.btn-primary` vs host crimson `#be123c` Georgia-serif buttons).
  - All temporary servers and `docsearch_t18_verify_db` stopped/cleaned up cleanly; `docker compose --profile "*" down` run (no `-v`).

---

## 3. Useful Commands

```powershell
# Frontend typecheck, unit tests, SPA build, and widget build
Set-Location web
npx tsc --noEmit
npx tsc -p tsconfig.node.json --noEmit
npm test
npm run build
npm run build:widget
Set-Location ..

# Live cross-origin test (host API + http://localhost:8088 demo server)
. .\scripts\load-env.ps1
$env:Cors__AllowedOrigins__1 = "http://localhost:8088"
dotnet run --project src/DocSearch.Api
# Terminal 2:
.\.venv\Scripts\python.exe -m http.server 8088 --bind 127.0.0.1 --directory web
```
