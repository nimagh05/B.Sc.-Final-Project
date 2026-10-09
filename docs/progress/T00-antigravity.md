# Progress Note: T00 — Repository bootstrap

**Agent:** Antigravity  
**Task:** T00 — Repository bootstrap  
**Date:** 2026-10-09  
**Status:** Completed  

## What Was Done
- Created `.gitignore` ignoring `.NET`/`Python`/`Node` build and cache artifacts, runtime data, `.env`, and `eval/pdfs/`, while ensuring `eval/questions.json` is tracked.
- Created `.gitattributes` enforcing `* text=auto`.
- Created `.editorconfig` defining UTF-8 charset (no BOM), indentation rules, and C# naming / directive ordering standards.
- Created `.env.example` containing configuration template with non-sensitive defaults and placeholders per `docs/INFRA.md`.
- Created `global.json` pinning .NET SDK `10.0.401` with `latestFeature` roll-forward.
- Created `README.md` (40 lines, <= 60 lines limit) summarizing project goals, current status, roadmap links, and environment prerequisites without placeholders.
- Created `docs/adr/0001-record-architecture-decisions.md` establishing the ADR format and decision tracking process.
- Verified that `AGENTS.md`, `CLAUDE.md`, `GEMINI.md`, and all other specification documents in `docs/` were not modified or overwritten.

## What Was Not Done / Remaining
- None for T00. All repository skeleton bootstrap requirements are met.

## How to Continue
- Next task according to `docs/TASKS.md` is **T01 — Phase-0 experiments: PDF extraction + embedding benchmark** (`[PLAN-FIRST]`).
- Prerequisites for T01:
  - 10–15 real PDFs placed in `eval/pdfs/` (provided by human).
  - `eval/questions.json` created (>= 30 items, provided by human).
  - Candidate embedding models pulled in Ollama (`bge-m3`, `multilingual-e5-base`, `multilingual-e5-small`).
- Commands to run for inspection:
  ```powershell
  dotnet --version
  git status
  ```
