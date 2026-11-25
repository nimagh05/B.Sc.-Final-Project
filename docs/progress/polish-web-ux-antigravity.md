# Handoff Note: Web UX E2E Audit & Polish (`polish/web-ux-e2e-audit`)

- **Branch:** `polish/web-ux-e2e-audit` (not pushed)
- **Scope:** Frontend-only polish strictly under `web/**` plus this handoff note (`src/**`, `tests/**`, `eval/**`, `conformance/**`, `.env`, and `docs/adr/**` untouched).

## Summary of Completed Work

1. **Context-Aware Persian Errors (`web/src/i18n/fa.ts`, `web/src/api/errors.ts`, `web/src/components/ErrorAlert.tsx`)**
   - Added `ErrorContext` (`auth`, `register`, `member`, `upload`, `collection`, `document`, `chat`, `general`) to disambiguate overloaded backend error codes (`resource.not_found`, `auth.forbidden`, `collection.owner_protected`, `503` `server.error` on `ExternalServiceUnavailableException`).
   - Mapped missing error codes (`rag.invalid_scope`, `reindex.already_running`, `http.method_not_allowed`, `http.unsupported_media_type`, `502`/`504`, encrypted PDF ingestion errors) and translated common English backend validation messages into natural Persian.
   - Styled `traceId` inside a subtle collapsible `<details>` toggle for expected 4xx user errors while keeping full trace footers on 5xx/network failures.

2. **Accessible UI Components (`CustomSelect.tsx`, `ConfirmDialog.tsx`, `Toast.tsx`)**
   - Built `CustomSelect.tsx` (keyboard `ArrowUp`/`ArrowDown`/`Home`/`End`/`Enter`/`Escape`, click-outside close, viewport flip-up + `alignEnd` support, option subtitles and status dots) for the document status filter and role selectors.
   - Built `ConfirmDialog.tsx` (`role="dialog"`, `aria-modal`, `Escape` close, focus restoration, destructive state) and replaced all `window.confirm` calls plus added confirmation for member and conversation deletion.
   - Built `ToastProvider` / `useToast` (`Toast.tsx`) for non-intrusive Persian feedback on mutations and copy actions.

3. **Page & Layout Polish (`Auth`, `Collections`, `CollectionDetail`, `Chat`, `Layout`, `global.css`)**
   - **Auth:** Surfaced `sessionExpiredNotice` on reload when `sessionStorage` contains an expired session (`tokenStorage.consumeExpiredNotice()`), prevented explicit logout from retaining another user's `/collections/:id` return URL, and hid duplicate password policy hint when field error is active.
   - **Collections:** Added creation toast, suppressed duplicate empty-state CTA while create form is open, and standardized Persian counts (`۰ سند`).
   - **Collection Detail:** Scoped edit, upload, documents, and members errors to their respective cards; added inline empty-name validation in Edit Collection (`noValidate`); added inline member row role editing with rollback on `409`; protected sole collection `Owner` (`مالک اصلی`) and self-row (`شما`) when multiple owners exist.
   - **Chat:** Added pure-React Markdown rendering (`**bold**`, `*italic*`, `` `code` ``, bullet/numbered lists, inline `: * **` split, zero `dangerouslySetInnerHTML`) with non-overlapping `[1]` citation chips, answer copy button (`کپی پاسخ`), and deduplicated empty-collection validation.
   - **Responsive (`<= 768px` & `1280×720`):** Fixed mobile sticky header wrapping, `.form-row > .field` scoping (eliminating 200px tall stacked fields on `/login` and 62px squeezed inputs in Members on mobile), card/table scroll containment, and opaque sticky header (`rgba(255, 255, 255, 0.98)`).

4. **Genuine Backend Bugs Observed (Untouched in `src/**`)**
   - `RagService.IsDegradedException` (`src/DocSearch.Infrastructure/Rag/RagService.cs`) does not unwrap `AggregateException` thrown by the OpenAI SDK `ClientRetryPolicy` on DNS/network failure (`Name or service not known`), returning HTTP `500` instead of `200` `degraded: true`.
   - `ExternalServiceUnavailableException` (`DocSearchExceptions.cs`) uses `ErrorCode = "server.error"` on HTTP `503`.
   - `CollectionsController.UpsertMember` uses generic `resource.not_found` (`404`) for unregistered user emails, and `CollectionDto` / `CollectionMemberDto` do not expose `ownerId` / `isPrimaryOwner`.
   - `AuthController.Register` accepts `displayName` without persisting/returning it in `/auth/me`.

## Verification State

- `web`: `npx tsc --noEmit` (0), `npm test` (9 files, 35 tests passed), `npm run build` (0).
- Live Docker container `docsearch-web` rebuilt and re-crawled at `1280×720` and `375×812`; post-fix spot-check verified `/login` field height (`67.8px`), upload/polling, and `/chat` pre-selection before a VPN/DNS drop triggered the `RagService` `AggregateException` 500 error.
- Temporary test collections/users cleaned up (`15` users, `139` collections, `141` docs, `565` conversations) and containers torn down with `docker compose --profile "*" down` (no `-v`).
