# 1. Record architecture decisions

Date: 2026-10-09
Status: Accepted

## Context

We need to record architectural decisions made on this project so that team members, autonomous AI agents, and future maintainers understand the context, rationale, and consequences of significant choices.

## Decision

We will use Architecture Decision Records (ADRs) as described by Michael Nygard. Each significant architectural decision will be captured in `docs/adr/NNNN-title.md` with the following structure:
- **Context:** The problem, constraints, and forces driving the decision.
- **Decision:** The chosen architecture, library, or pattern.
- **Consequences:** The positive and negative implications of the decision.
- **Alternatives:** Alternative options considered and reasons for rejection.

## Consequences

- Architectural decisions are version-controlled alongside the codebase.
- Autonomous agents and human developers can consult established decisions before introducing architectural changes or deviating from standards.
- Deviations from existing specifications require drafting an ADR prior to implementation.

## Alternatives

- Scattered documentation or wiki pages: Rejected because they easily become out of date and are disconnected from git branches and code reviews.
- Relying on code comments or commit messages alone: Rejected because commit messages do not provide a structured view of context and evaluated trade-offs.
