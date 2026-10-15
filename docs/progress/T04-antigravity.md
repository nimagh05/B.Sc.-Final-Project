# Task Handoff — T04: PersianTextNormalizer

**Agent:** Antigravity (Gemini 3.8 Flash)  
**Date:** 2026-10-15  
**Branch:** `feature/T04-normalizer`  
**Status:** Completed and Verified  

---

## 1. What was done
- **PersianUnicodeConstants** (`src/DocSearch.Application/Text/PersianUnicodeConstants.cs`):
  - Defined explicitly named Unicode code points, boundaries, and helper functions with zero magic character literals.
  - Covers invisible/bidi controls (`\u200B`, `\u200E`, `\u200F`, `\u202A–\u202E`, `\u2066–\u2069`, `\uFEFF`, `\u00AD`), Persian/Arabic letters & variants (`ي`/`ى` $\to$ `ی`, `ك` $\to$ `ک`, `ة`, `ه`, `\u0640` tatweel, `\u200C` ZWNJ), Arabic diacritics (`U+064B–U+065F` and `U+0670`), Persian digits (`۰-۹`), Arabic-Indic digits (`٠-٩`), Arabic decimal and thousands separators (`٫`, `٬`), punctuation (`،`, `؛`, `؟`, `«`, `»`, `…`, `٪`), and whitespace/hyphen constants.
- **PersianTextNormalizer** (`src/DocSearch.Application/Text/PersianTextNormalizer.cs`):
  - Implemented `ITextNormalizer` pure, stateless, thread-safe service adhering strictly to `SPEC-PERSIAN-AND-RAG.md` §1:
    - `NormalizeForDisplay`: Unicode NFKC $\to$ removal of invisible/bidi controls (preserving ZWNJ) $\to$ newline normalization (`\r\n`, `\r` $\to$ `\n`) $\to$ space separator/tab normalization $\to$ letter unification (`ي`/`ى` $\to$ `ی`, `ك` $\to$ `ک`, preserving `ة` and hamza forms) $\to$ tatweel removal $\to$ ZWNJ cleanup (runs collapsed, line boundary and whitespace-adjacent ZWNJ stripped) $\to$ whitespace collapsing, line trimming, and 3+ newlines collapsed to 2 $\to$ whole text trim.
    - `NormalizeForEmbedding`: `NormalizeForDisplay` output $\to$ Latin hyphen line breaks joined (`word-\nnext` $\to$ `wordnext`) $\to$ character pass removing Arabic diacritics, mapping Persian/Arabic-Indic digits to ASCII `0-9`, mapping separators `٫` $\to$ `.`, `٬` $\to$ `,`, mapping `ة` $\to$ `ه`, and joining single newlines inside paragraphs to space while preserving blank lines (`\n\n`) $\to$ whitespace cleanup and trim.
    - `NormalizeForSearch`: `NormalizeForEmbedding` output $\to$ character pass converting ZWNJ $\to$ space, mapping punctuation (`،` $\to$ `,`, `؛` $\to$ `;`, `؟` $\to$ `?`, `«»` $\to$ `"`, `…` $\to$ `...`, `٪` $\to$ `%`), Latin lowercasing $\to$ whitespace cleanup and trim.
  - Implemented source-generated regexes (`[GeneratedRegex]`) with defensive timeouts (`1000 ms`).
  - Optimized critical execution paths to guarantee sub-millisecond execution on typical chunks and $< 50\text{ ms}$ on $> 100\text{ KB}$ inputs.
- **Dependency Injection** (`src/DocSearch.Application/DependencyInjection.cs`):
  - Registered `PersianTextNormalizer` as `ITextNormalizer` singleton in `AddApplication()`.
- **Comprehensive Unit, Property, and Performance Tests** (`tests/DocSearch.Application.Tests/PersianTextNormalizerTests.cs`):
  - All 15 mandatory test cases from `SPEC-PERSIAN-AND-RAG.md` §1.5 table.
  - 35+ additional edge cases covering presentation forms (NFKC), bidi/invisible removal, ZWNJ boundary rules, de-hyphenation across line breaks, diacritics stripping, Persian and Arabic-Indic digit mapping, punctuation substitution, Latin lowercasing, and null/empty/whitespace inputs.
  - Property-style idempotency test verifying $N(N(x)) == N(x)$ across 250 deterministically generated strings (`new Random(1337)`) for all three normalizer layers (750 checks).
  - Performance sanity test verifying that a $\ge 100\text{ KB}$ mixed Persian/English text normalizes in $< 50\text{ ms}$ after warm-up.
  - DI registration test verifying `ITextNormalizer` registers as a singleton of type `PersianTextNormalizer`.

---

## 2. Verification results
- `dotnet build -warnaserror`: **0 warnings, 0 errors**.
- `dotnet test --filter "FullyQualifiedName~PersianTextNormalizerTests"`: **52 passed, 0 failed**.
- `dotnet test --filter "Category!=RequiresOllama"`: **81 passed, 0 failed** (66 Application.Tests, 9 Api.Tests, 6 Infrastructure.Tests).
- `dotnet format --verify-no-changes`: **Clean (0 errors, 0 diff)**.

---

## 3. How to continue / Commands to run
- To run normalizer unit tests:
  ```powershell
  dotnet test --filter "FullyQualifiedName~PersianTextNormalizerTests"
  ```
- To run all unit and integration tests:
  ```powershell
  dotnet test --filter "Category!=RequiresOllama"
  ```
- Recommended next task: **T05 — PDF extraction adapter** (`[PLAN-FIRST]`).
