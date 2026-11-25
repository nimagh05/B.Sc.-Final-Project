# Task T20 Progress Note — Packaging & Documentation

**Task:** T20 — Packaging & documentation  
**Agent:** Antigravity  
**Branch:** `feature/T20-packaging-docs`  
**Date:** 2026-11-25  
**Status:** Completed (Docker full-stack build + HTTP smoke tests + `.NET` / `web` / hygiene checks verified; ready for human fresh-clone verification & PR review)

---

## 1. What Was Completed

1. **Dockerfiles, Nginx & Full-Stack Compose:**
   - **`src/DocSearch.Api/Dockerfile`:** Installed `curl` (`apt-get install -y --no-install-recommends curl`), pre-created `/data/files` and `/app/logs` with `chown -R $APP_UID:$APP_UID` before `USER $APP_UID` (ensuring `filedata` volume and Serilog file sink are writable by non-root UID `1654`), and added `HEALTHCHECK` on `http://127.0.0.1:8080/health/live`.
   - **`web/Dockerfile`, `web/nginx.conf`, `web/.dockerignore`:** Added multi-stage `node:24-alpine` → `nginx:alpine` image with BusyBox `wget` `HEALTHCHECK`, SPA fallback (`try_files $uri $uri/ /index.html;`), `client_max_body_size 64m;`, `proxy_read_timeout 180s;`, `proxy_request_buffering off;` for `/api/` and `/health/` → `http://api:8080`, and `always` security headers inside `location /` (avoiding duplicate headers on proxied API responses).
   - **`docker-compose.yml`:** Preserved unprofiled `db` and `profiles: ["full"]` on `extractor`, `api`, and `web` (preserving `.github/workflows/ci.yml` and `docker compose up -d db` / `db extractor`); added `depends_on` health conditions (`api` → `db`, `extractor`; `web` → `api`), plus `Extraction__OcrMode`, `Cors__AllowedOrigins__0`, and `Rag__MinSimilarity`.
2. **Configuration & Licensing:**
   - **`.env.example`:** Completed with every variable used by `docker-compose.yml` and `scripts/load-env.ps1` (`Extraction__OcrMode=Defective`, `Cors__AllowedOrigins__0`, `Rag__MinSimilarity=0.55`, ports, DB, JWT, bootstrap admin, Ollama/Chat placeholders; empty `Ai__Chat__ApiKey=`).
   - **`LICENSE`:** Added MIT License (Copyright 2026 Nima).
3. **Documentation:**
   - **`README.md`:** Preserved CI badge; added verified thesis highlights (`docs/EVAL-RESULTS.md`), Architecture Mermaid diagram, Windows PowerShell setup from zero (both full Docker stack with explicit EF migration ordering and low-RAM daily dev), grouped Configuration Reference Table (including `Network:TrustedProxyCidrs`), API Summary Table, ADR Index (`0001`–`0011`), Backup & Restore commands, Troubleshooting table, and third-party PyMuPDF `AGPL-3.0` notice.
   - **`docs/DEMO.md`:** 10 grounded demo questions from `eval/questions.json`, step-by-step Degraded Mode demo (`DegradedBanner`), and defense-day pre-flight + fallback plan (including `[H]` backup video).
   - **`docs/INTEGRATION.md`:** Versioned REST API guide for the university website team covering JWT/service-account scoping, `Network:TrustedProxyCidrs`, RFC 7807 `ProblemDetails` & `429 Retry-After` (same-origin proxy caveat), CORS, optional unimplemented T18 Web Component summary, and Stretch S6 OIDC/SSO integration path.

---

## 2. How Verified

- **Compose & YAML Validation:** `docker compose config --services` (`db`), `docker compose --profile full config --services` (`db`, `extractor`, `api`, `web`), `docker compose --profile full config -q` (exit `0`), `docker compose --dry-run up -d db extractor` (exit `0`), YAML parse of `.github/workflows/ci.yml` and `docker-compose.yml` (`yaml_ok`), `git diff --exit-code .github/workflows/ci.yml` (exit `0`).
- **Full-Stack Docker Build & Smoke Tests:** `docker compose --profile full up -d --build --wait` brought all 4 containers (`docsearch_db`, `docsearch_extractor`, `docsearch_api`, `docsearch_web`) to `healthy`. Verified `uid=1654(app)` + `/usr/bin/curl` + writable `/data/files` & `/app/logs` inside `api`, `/usr/bin/wget` + 4 `Vazirmatn-*.woff2` files inside `web`, `200` on `/`, `/chat`, `/collections/<uuid>`, `/fonts/Vazirmatn-Regular.woff2` (`font/woff2`), `/health/live`, and `http://localhost:8000/health`, security headers on `/`, `401 auth.invalid_credentials` `application/problem+json` via `/api/v1/auth/login`, and `/health/ready` (`database: Healthy`, `extractor: Healthy`, `embedding: Unhealthy` while host Ollama was stopped). Safely torn down via `docker compose --profile "*" down` (without `-v`).
- **.NET Checks:** `dotnet format --verify-no-changes` (exit `0`), `dotnet build -warnaserror` (`0 Warning(s) 0 Error(s)`), `dotnet test --no-build --filter "Category!=RequiresOllama"` (`199` Application + `144` Infrastructure + `69` API = `412` passed, `0` failed; `DocSearch.Domain.Tests` has no tests).
- **Web Checks:** `npx tsc --noEmit` (exit `0`), `npx vitest run --pool=threads` (`7` files, `19` tests passed; note default `forks` pool hit 60s worker startup timeout under ~1.29 GB free host RAM), `npm run build` (exit `0`).
- **Hygiene:** CI secret scan clean; UTF-8 no-BOM check clean across all modified/created files.

---

## 3. How to Continue / Manual Checks

- Verify `README.md` setup from a fresh clone on a clean machine (or with host Ollama running so `/health/ready` returns `200 Healthy`).
- Record the `[H]` backup demo video per `docs/DEMO.md`.
- Merge `feature/T20-packaging-docs` into `main` via PR after review.
