# GEMINI.md — Antigravity-specific operating rules

`AGENTS.md` is the project source of truth. These extra rules apply to Antigravity agents only (they take priority over `AGENTS.md` if they ever conflict, except for safety rules, which always win).

1. Rules files can be truncated by the tool. Do NOT assume you have seen everything: before any work, open and read `AGENTS.md` and `docs/CONVENTIONS.md` in full, then the task section in `docs/TASKS.md` and the spec files it names.
2. For every task marked `[PLAN-FIRST]`, use planning mode (if available), produce the implementation plan, and STOP until the human approves. For other tasks, still write a short plan first.
3. Terminal: show the exact command and why before running it; never run destructive commands (`git reset --hard`, `git push --force`, `docker system prune`, `docker volume rm`, deleting folders, dropping databases) without explicit approval. Do not leave servers/containers running.
4. Work only inside this workspace folder. Other agents may be working in sibling folders — never read, edit, or run anything there.
5. Use the integrated browser only when a task explicitly needs it (e.g., checking the API docs UI or the web app).
6. One task per conversation/thread. Start a new conversation for the next task.
7. Commit small and often. Before you stop for any reason (finished, blocked, out of quota or context), commit and write the handoff note required by AGENTS.md.
8. Finish with the 6-heading report defined in AGENTS.md §3. Do not claim anything you did not run.
