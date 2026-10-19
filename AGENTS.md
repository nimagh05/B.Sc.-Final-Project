# AGENTS.md — DocSearch (Persian RAG document search)

**Audience:** any AI coding agent (Claude Code, OpenCode, Antigravity, or others), including weaker/cheaper models.
Be literal. Follow every rule. When something is unclear or conflicts with reality → **stop and ask the human**. Never guess silently.

**Product in one sentence:** a web service where users upload PDF documents (university regulations, papers, internal documents) and ask questions in natural language (mainly **Persian**); the system answers **only from the documents** and **cites file name + page**.

**Why quality matters:** this is a B.Sc. final project that the university may reuse in its new website. It must be correct, secure, testable, documented, and integrable (standalone versioned REST API + optional embeddable widget).

---

## 0. Reading map (which document to read when)

| Situation | Read |
|---|---|
| Always, every session | this file (`AGENTS.md`) **and** `docs/CONVENTIONS.md` (core contracts/interfaces, coding conventions, security checklist, Definition of Done, pitfalls, **technology decisions and repository layout**) |
| Anything touching DB, API, config, auth, errors | `docs/SPEC-DATA-AND-API.md` |
| Persian text, PDF text quality, chunking, retrieval, prompt, citations | `docs/SPEC-PERSIAN-AND-RAG.md` |
| Executing a task (T00…T20, S1…S9) | `docs/TASKS.md` → "Global task rules" + **only the current task section** |
| Docker, compose, CI, run commands, `.gitignore`, packages | `docs/INFRA.md` |
| Evaluation harness, conformance suite, metrics | `docs/EVAL.md` |
| Running several agents in parallel (human-only) | `docs/PARALLEL-BAKEOFF.md` (agents ignore unless told) |

If a document is missing or contradicts another, **this file wins for rules**, the spec files win for details; report the contradiction.

---

## 1. Golden rules (non-negotiable)

1. **One task at a time.** Read the whole task section before writing code. Do not start the next task unless told.
2. **Never invent APIs.** Before using any NuGet/npm/pip API, check the installed package version and the real signatures (IntelliSense, compiler errors, package docs/source). If something does not compile after two honest attempts, stop and report; do not thrash.
3. **No placeholders.** No `TODO`, `throw new NotImplementedException()`, empty `catch`, fake data in production paths, commented-out code. If you cannot finish, state exactly what is missing.
4. **Tests are mandatory.** Never delete, skip, or weaken a test to make it pass. Never claim tests pass without running them; paste the summary line (`Passed! … Failed: 0`).
5. **Frozen files:** do not modify `conformance/**`, `eval/questions.json`, `eval/pdfs/**`, `AGENTS.md`, or anything in `docs/` (except adding `docs/adr/*.md`, adding your own handoff notes in `docs/progress/<task>-<agent>.md`, and ticking task checkboxes when asked) unless the human explicitly asks.
6. **Spec conflicts → stop, explain, propose, write an ADR** (`docs/adr/NNNN-title.md`). Never silently deviate.
7. **Small commits** (Conventional Commits). One branch per task. Never commit to `main` directly.
8. **Secrets:** never commit or log keys/passwords. Prefer environment variables loaded from `.env` (git-ignored) via `scripts/load-env.ps1`; `dotnet user-secrets` is acceptable only when the human runs a single copy of the repo. Only `.env.example` is committed.
9. **No heavy new dependencies** (Kafka, Elasticsearch, Redis, Kubernetes, ORMs other than EF Core, etc.) without an ADR + human approval. Every new package: state why, license OK, maintained.
10. **Windows + PowerShell** is the dev environment. Give commands in PowerShell syntax. Code must also run in Linux containers (use `Path.Combine`, no hardcoded `\`).
11. **UTF-8 without BOM** for every file. Never re-encode, transliterate, or "fix" Persian text. Keep Persian user-facing strings in one resource/constants location, not scattered.
12. **No destructive commands** without human approval: `git reset --hard`, `git push --force`, `docker system prune`, `docker volume rm`, deleting folders outside the repo, dropping databases.
13. **Ports, names, hosts come from configuration** (`.env` / appsettings). Never hardcode 5432, 5000, 11434, etc. in code (defaults in config only).
14. **Tests must not call real AI services.** Use the Fake providers (see `docs/SPEC-DATA-AND-API.md` §4 Fake providers). Only tests with `[Trait("Category","RequiresOllama")]` may call the local Ollama; none may call Gemini/OpenAI/etc.
15. **Determinism:** inject `TimeProvider`, `IGuidGenerator`-style abstractions where needed; no `DateTime.Now`, no unseeded randomness in logic under test.
16. **Stop what you start:** do not leave dev servers/containers running; do not leave background processes.
17. **Leave a handoff note.** Before you stop (finished, blocked, or running out of quota/context), commit your work-in-progress and write `docs/progress/<task>-<agent>.md` (what is done, what is not, how to continue, commands to run). A different agent or session must be able to continue from it.
18. **Be honest in reports.** If something was not run or not verified, say so. If you are unsure, say so.
19. **Ask when ambiguous.** One short, specific question is better than a wrong implementation.
20. **Do not expand scope.** No unrequested features, refactors, renames, or formatting-only churn.
21. **Leave the repo green:** at the end of every task `dotnet build` and `dotnet test` must succeed.
22. **Secret Hygiene (`.env`)**: NEVER read, view, cat, or edit the local `.env` file under any circumstances. Only read or edit `.env.example`.

---


## 2. Environment facts (assume these)

- OS: Windows 10/11, PowerShell. Repo lives on drive **F:** (`F:\projects\<repo>`); C: is almost full — never install large things on C:.
- .NET SDK **10.0.x** installed → target **`net10.0`**. (`dotnet new sln` may create `.slnx` in .NET 10 — that is fine; never hardcode `.sln` in scripts, detect the file.)
- Docker Desktop with WSL2 (memory limited to ~4 GB). Docker images/volumes are on F:.
- **Ollama runs on the host** (not in Docker) at `http://localhost:11434`, **CPU-only** (`CUDA_VISIBLE_DEVICES=-1`), model `bge-m3` (1024 dims). Models stored in `F:\ollama\models`. From inside a container the host is `http://host.docker.internal:11434`.
- Python 3.11 with a venv at `<repo>/.venv` (PyMuPDF, pdfplumber, requests, numpy; later pytest, matplotlib).
- Machine has **12 GB RAM, no usable GPU**: run only the services you need; do not run heavy things in parallel.
- Chat LLM for manual demos: **Gemini via an OpenAI-compatible endpoint**; requires VPN from Iran and may be unavailable at any moment → the system must degrade gracefully (see RAG spec).
- Network: package restore, Docker Hub, Ollama registry may fail from Iran without VPN. If a download fails with 403/timeout, **tell the human** ("turn on VPN") instead of working around it.

---

## 3. Per-task workflow and report format

1. Read `docs/TASKS.md` → "Global task rules" and the current task.
2. If the task is `[PLAN-FIRST]`: output a plan (files, approach, risks, tests) and **wait for approval**.
3. `git checkout -b feature/Txx-name`.
4. Implement in small steps; build + test after each step.
5. Run the verification commands listed in the task; capture real output.
6. Self-review against `docs/CONVENTIONS.md` §4 (Definition of Done) and §3 (security checklist).
7. **Final report (exactly these 6 headings):**
   1. *What changed* (files, behavior)
   2. *How I verified* (commands + key output)
   3. *Tests added/changed* (names)
   4. *What the human must check manually* (concrete steps)
   5. *Known limitations / deviations / ADRs*
   6. *Recommended next task*

---

## 4. Session start prompt (paste if your tool does not auto-load this file)

```
Read AGENTS.md and docs/CONVENTIONS.md completely, then docs/TASKS.md "Global task rules" and the section of the CURRENT TASK only.
Current task: <ID — title>.
Obey the golden rules: one task, no invented APIs, no placeholders, tests mandatory, don't touch frozen files,
stop and ask on conflicts. If the task is [PLAN-FIRST], give me a plan and wait.
Dev environment: Windows PowerShell, repo on F:, Ollama on host (CPU-only), Docker Desktop.
End with the 6-heading report from AGENTS.md §3.
```
