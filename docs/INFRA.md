# INFRA — Docker, environment, CI, repo hygiene, run commands

All ports/names are configurable through `.env` so that several copies of the project can run side by side (needed for the multi-agent bake-off, see `PARALLEL-BAKEOFF.md`).

---

## 1. `.env.example` (commit this; real `.env` is git-ignored)

```dotenv
# --- Docker Compose identity (change per copy of the repo to avoid clashes) ---
COMPOSE_PROJECT_NAME=docsearch

# --- Ports on the host ---
DB_PORT=5432
API_PORT=5000
WEB_PORT=5173
EXTRACTOR_PORT=8000

# --- Database ---
POSTGRES_DB=docsearch
POSTGRES_USER=postgres
POSTGRES_PASSWORD=change_me_please

# --- API (when running in Docker; for `dotnet run` use user-secrets/appsettings) ---
ASPNETCORE_ENVIRONMENT=Production
Jwt__SigningKey=change_me_to_a_random_string_of_at_least_32_chars
Auth__BootstrapAdmin__Email=admin@example.com
Auth__BootstrapAdmin__Password=Change_me_123!
Ai__Embedding__Provider=Ollama
Ai__Embedding__Model=bge-m3
Ai__Embedding__Endpoint=http://host.docker.internal:11434
Ai__Chat__Provider=OpenAICompatible
Ai__Chat__Endpoint=https://generativelanguage.googleapis.com/v1beta/openai/
Ai__Chat__Model=gemini-3.8-flash
Ai__Chat__ApiKey=
```
Rules: never commit `.env`; never put real keys in `.env.example`; the API reads `Ai__Chat__ApiKey` from the environment.

---

## 2. `docker-compose.yml`

```yaml
name: ${COMPOSE_PROJECT_NAME:-docsearch}

services:
  db:
    image: pgvector/pgvector:pg16
    container_name: ${COMPOSE_PROJECT_NAME:-docsearch}_db
    restart: unless-stopped
    environment:
      POSTGRES_DB: ${POSTGRES_DB:-docsearch}
      POSTGRES_USER: ${POSTGRES_USER:-postgres}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:?Set POSTGRES_PASSWORD in .env}
    ports:
      - "${DB_PORT:-5432}:5432"
    volumes:
      - dbdata:/var/lib/postgresql/data
    shm_size: 256mb
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U ${POSTGRES_USER:-postgres} -d ${POSTGRES_DB:-docsearch}"]
      interval: 5s
      timeout: 3s
      retries: 20

  extractor:
    profiles: ["full"]
    build: ./tools/extractor
    container_name: ${COMPOSE_PROJECT_NAME:-docsearch}_extractor
    restart: unless-stopped
    ports:
      - "${EXTRACTOR_PORT:-8000}:8000"

  api:
    profiles: ["full"]
    build:
      context: .
      dockerfile: src/DocSearch.Api/Dockerfile
    container_name: ${COMPOSE_PROJECT_NAME:-docsearch}_api
    restart: unless-stopped
    depends_on:
      db:
        condition: service_healthy
    environment:
      ASPNETCORE_ENVIRONMENT: ${ASPNETCORE_ENVIRONMENT:-Production}
      ConnectionStrings__Default: Host=db;Port=5432;Database=${POSTGRES_DB:-docsearch};Username=${POSTGRES_USER:-postgres};Password=${POSTGRES_PASSWORD}
      Extraction__ExtractorBaseUrl: http://extractor:8000
      Storage__RootPath: /data/files
      Jwt__SigningKey: ${Jwt__SigningKey}
      Auth__BootstrapAdmin__Email: ${Auth__BootstrapAdmin__Email:-}
      Auth__BootstrapAdmin__Password: ${Auth__BootstrapAdmin__Password:-}
      Ai__Embedding__Provider: ${Ai__Embedding__Provider:-Ollama}
      Ai__Embedding__Model: ${Ai__Embedding__Model:-bge-m3}
      Ai__Embedding__Endpoint: ${Ai__Embedding__Endpoint:-http://host.docker.internal:11434}
      Ai__Chat__Provider: ${Ai__Chat__Provider:-OpenAICompatible}
      Ai__Chat__Endpoint: ${Ai__Chat__Endpoint:-}
      Ai__Chat__Model: ${Ai__Chat__Model:-}
      Ai__Chat__ApiKey: ${Ai__Chat__ApiKey:-}
    extra_hosts:
      - "host.docker.internal:host-gateway"
    ports:
      - "${API_PORT:-5000}:8080"
    volumes:
      - filedata:/data/files

  web:
    profiles: ["full"]
    build: ./web
    container_name: ${COMPOSE_PROJECT_NAME:-docsearch}_web
    restart: unless-stopped
    ports:
      - "${WEB_PORT:-5173}:80"

volumes:
  dbdata:
  filedata:
```
- Day-to-day development: `docker compose up -d db` only (API runs with `dotnet run`, saves RAM).
- Full stack: `docker compose --profile full up -d --build`.
- Ollama is **not** in compose (host process, CPU-only).
- Different copy of the repo → different `COMPOSE_PROJECT_NAME`, `DB_PORT`, `API_PORT`, … in its own `.env`.

## 3. API Dockerfile (`src/DocSearch.Api/Dockerfile`, build context = repo root)

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet restore src/DocSearch.Api/DocSearch.Api.csproj
RUN dotnet publish src/DocSearch.Api/DocSearch.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "DocSearch.Api.dll"]
```
Add a repo-root `.dockerignore` (bin, obj, .git, .venv, node_modules, data, eval/pdfs, logs, .env).
If the image cannot be pulled from Iran, the human must enable VPN.
Mounted volume permissions: the app user must be able to write `/data/files` (create the dir in the image with proper ownership if needed).

## 4. Extractor Dockerfile (`tools/extractor/Dockerfile`, only if the sidecar exists)

```dockerfile
FROM python:3.11-slim
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends tesseract-ocr tesseract-ocr-fas tesseract-ocr-eng && rm -rf /var/lib/apt/lists/*
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY app/ app/
EXPOSE 8000
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
```

## 5. `.gitignore` (minimum)

```
# .NET
bin/
obj/
.vs/
*.user
TestResults/
# Python
.venv/
__pycache__/
*.pyc
.pytest_cache/
# Node
node_modules/
web/dist/
# Runtime data and secrets
.env
data/
logs/
*.log
# Evaluation (documents stay local)
eval/pdfs/
eval/experiments/out/
eval/results/*/*.png
# IDEs
.idea/
.vscode/*
!.vscode/extensions.json
# Misc
.DS_Store
Thumbs.db
```

## 6. `Directory.Build.props` and `global.json`

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <Deterministic>true</Deterministic>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  </PropertyGroup>
</Project>
```
```json
{ "sdk": { "version": "10.0.401", "rollForward": "latestFeature" } }
```
`Directory.Packages.props`: `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>`; package versions live there (added by `dotnet add package`).
`.editorconfig`: UTF-8 (no BOM) via `charset = utf-8`, `indent_style = space`, `indent_size = 4` (2 for json/yaml/ts/tsx/css), `insert_final_newline = true`, `trim_trailing_whitespace = true`, C# naming rules (`_camelCase` private fields) and `dotnet_sort_system_directives_first = true`.

## 7. GitHub Actions (`.github/workflows/ci.yml`)

```yaml
name: ci
on:
  push: { branches: [main] }
  pull_request:
jobs:
  dotnet:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - run: dotnet restore
      - run: dotnet build --no-restore -warnaserror -c Release
      - run: dotnet format --verify-no-changes --no-restore
      - run: dotnet test --no-build -c Release --filter "Category!=RequiresOllama" --logger "trx"
      - run: dotnet list package --vulnerable --include-transitive
  conformance:
    runs-on: ubuntu-latest
    needs: dotnet
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - uses: actions/setup-python@v5
        with: { python-version: "3.11" }
      - name: Start database
        run: docker compose up -d db
        env: { POSTGRES_PASSWORD: ci_password }
      - name: Run API with fake providers
        run: |
          dotnet run --project src/DocSearch.Api -c Release --urls http://localhost:5000 &
          # wait for health, then run conformance
        env:
          ConnectionStrings__Default: Host=localhost;Port=5432;Database=docsearch;Username=postgres;Password=ci_password
          Ai__Embedding__Provider: Fake
          Ai__Chat__Provider: Fake
          Rag__MinSimilarity: "0.2"
          Jwt__SigningKey: ci_signing_key_ci_signing_key_ci_signing
          Auth__BootstrapAdmin__Email: admin@ci.test
          Auth__BootstrapAdmin__Password: Ci_Admin_123!
      - run: pip install -r conformance/requirements.txt
      - run: pytest conformance -m contract -v
        env: { BASE_URL: http://localhost:5000, ADMIN_EMAIL: admin@ci.test, ADMIN_PASSWORD: Ci_Admin_123! }
```
(The agent finalizing T16 must make the "wait for health" step real — a polling loop with a timeout — and verify the workflow syntax.) Python sidecar tests and web tests get their own jobs when those parts exist.

## 8. Daily commands (PowerShell)

```powershell
# start database
docker compose up -d db
docker compose ps
# migrations
dotnet ef database update -p src/DocSearch.Infrastructure -s src/DocSearch.Api
# run API (secrets once)
dotnet user-secrets set "Ai:Chat:ApiKey" "<key>" --project src/DocSearch.Api
dotnet run --project src/DocSearch.Api
# tests
dotnet test --filter "Category!=Integration&Category!=RequiresOllama"   # fast
dotnet test --filter "Category!=RequiresOllama"                          # + integration (needs Docker)
dotnet test --filter "Category=RequiresOllama"                           # needs Ollama running
# formatting
dotnet format
# psql shell
docker compose exec db psql -U postgres -d docsearch
# stop everything
docker compose down            # keeps data volume
docker compose down -v         # DELETES the database volume (ask the human first)
```

## 9. Resource budget (12 GB RAM machine)
Docker/WSL ≈ 4 GB cap; Postgres ≈ 0.3–0.6 GB; API ≈ 0.3 GB; Ollama `bge-m3` ≈ 1.5–2.5 GB while loaded (unloads after idle); IDE + browser ≈ 3–4 GB. Rules: only run what the current task needs; run Testcontainers-based tests when no other heavy process is active; never run two full stacks at once unless ports/names are separated and RAM allows.

## 10. Troubleshooting
| Symptom | Cause / action |
|---|---|
| `error during connect … docker daemon not running` | start Docker Desktop, wait for "Engine running" |
| `403`/timeout pulling images or restoring packages | VPN required (ask the human) |
| Ollama `0xc0000005` crash | GPU path crashing; ensure `CUDA_VISIBLE_DEVICES=-1` and restart Ollama |
| `ollama pull` → "redirect target not allowed … 198.18.x.x" | VPN fake-IP mode; use proxy mode and set `HTTPS_PROXY` for Ollama |
| `/health/ready` unhealthy: embedding | Ollama not running or model not pulled (`ollama list`) |
| Vector dimension errors | `Ai:Embedding:Dimensions` ≠ column dimension → follow reindex ADR |
| Persian text garbled in PowerShell | send UTF-8 bytes; use `charset=utf-8`; view files with a UTF-8 editor |
| Port already in use | change ports in `.env`; check other copies of the project |
