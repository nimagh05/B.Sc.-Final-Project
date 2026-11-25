import { Fragment, type ReactNode } from 'react';
import type { SourceCitationDto } from '../api/types';
import { fa } from '../i18n/fa';

interface CitationTextProps {
  text: string;
  sources: SourceCitationDto[];
  activeSourceIndex: number | null;
  onSelectSource: (index: number) => void;
}

const BRACKET_PATTERN = /\[(\d+(?:\s*[,،]\s*\d+)*)\]/g;

function renderCitationsInText(
  segment: string,
  validIndices: Set<number>,
  activeSourceIndex: number | null,
  onSelectSource: (index: number) => void,
  keyPrefix: string,
): ReactNode[] {
  const nodes: ReactNode[] = [];
  let lastIndex = 0;

  BRACKET_PATTERN.lastIndex = 0;
  let match: RegExpExecArray | null;

  while ((match = BRACKET_PATTERN.exec(segment)) !== null) {
    if (match.index > lastIndex) {
      nodes.push(segment.slice(lastIndex, match.index));
    }

    const rawGroup = match[1];
    const parts = rawGroup.split(/([,،]\s*)/);
    const parsedTokens: Array<{ isSeparator: boolean; raw: string; num?: number; isValid?: boolean }> = [];
    let hasAtLeastOneValid = false;
    let hasInvalidNumber = false;

    for (const part of parts) {
      if (!part) continue;
      if (/^[,،]\s*$/.test(part)) {
        parsedTokens.push({ isSeparator: true, raw: part });
        continue;
      }

      const trimmed = part.trim();
      if (/^\d+$/.test(trimmed)) {
        const num = Number.parseInt(trimmed, 10);
        const isValid = validIndices.has(num);
        if (isValid) {
          hasAtLeastOneValid = true;
        } else {
          hasInvalidNumber = true;
        }
        parsedTokens.push({ isSeparator: false, raw: part, num, isValid });
      } else {
        hasInvalidNumber = true;
        parsedTokens.push({ isSeparator: false, raw: part, isValid: false });
      }
    }

    if (!hasAtLeastOneValid) {
      nodes.push(match[0]);
    } else if (!hasInvalidNumber) {
      const validNums = parsedTokens.filter((t) => !t.isSeparator && t.isValid && t.num !== undefined);
      if (validNums.length === 1) {
        const num = validNums[0].num!;
        const isActive = activeSourceIndex === num;
        nodes.push(
          <span key={`c-${keyPrefix}-${match.index}`} className="citation-group" dir="ltr">
            <button
              type="button"
              className="citation-chip"
              aria-label={fa.chat.citationAriaLabel(num)}
              aria-pressed={isActive}
              onClick={() => onSelectSource(num)}
            >
              [{num}]
            </button>
          </span>,
        );
      } else {
        nodes.push(
          <span key={`c-${keyPrefix}-${match.index}`} className="citation-group" dir="ltr">
            {validNums.map((tok, idx) => {
              const num = tok.num!;
              const isActive = activeSourceIndex === num;
              return (
                <button
                  key={`b-${keyPrefix}-${match!.index}-${idx}`}
                  type="button"
                  className="citation-chip"
                  aria-label={fa.chat.citationAriaLabel(num)}
                  aria-pressed={isActive}
                  onClick={() => onSelectSource(num)}
                >
                  [{num}]
                </button>
              );
            })}
          </span>,
        );
      }
    } else {
      nodes.push(
        <span key={`c-${keyPrefix}-${match.index}`} className="citation-group" dir="ltr">
          [
          {parsedTokens.map((tok, tokIdx) => {
            if (tok.isSeparator) {
              return tok.raw;
            }
            if (tok.isValid && tok.num !== undefined) {
              const num = tok.num;
              const isActive = activeSourceIndex === num;
              return (
                <button
                  key={`b-${keyPrefix}-${match!.index}-${tokIdx}`}
                  type="button"
                  className="citation-chip"
                  aria-label={fa.chat.citationAriaLabel(num)}
                  aria-pressed={isActive}
                  onClick={() => onSelectSource(num)}
                >
                  {num}
                </button>
              );
            }
            return tok.raw;
          })}
          ]
        </span>,
      );
    }

    lastIndex = match.index + match[0].length;
  }

  if (lastIndex < segment.length) {
    nodes.push(segment.slice(lastIndex));
  }

  return nodes;
}

type InlineToken =
  | { kind: 'text'; value: string }
  | { kind: 'bold'; value: string }
  | { kind: 'italic'; value: string }
  | { kind: 'code'; value: string };

function tokenizeInlineMarkdown(line: string): InlineToken[] {
  const tokens: InlineToken[] = [];
  let cursor = 0;

  while (cursor < line.length) {
    const nextBold = line.indexOf('**', cursor);
    const nextCode = line.indexOf('`', cursor);
    const nextSingleStar = findNextSingleStar(line, cursor);

    const candidates: Array<{ pos: number; kind: 'bold' | 'code' | 'italic' }> = [];
    if (nextBold !== -1) candidates.push({ pos: nextBold, kind: 'bold' });
    if (nextCode !== -1) candidates.push({ pos: nextCode, kind: 'code' });
    if (nextSingleStar !== -1) candidates.push({ pos: nextSingleStar, kind: 'italic' });

    if (candidates.length === 0) {
      tokens.push({ kind: 'text', value: line.slice(cursor) });
      break;
    }

    candidates.sort((a, b) => a.pos - b.pos);
    const earliest = candidates[0];

    if (earliest.pos > cursor) {
      tokens.push({ kind: 'text', value: line.slice(cursor, earliest.pos) });
    }

    if (earliest.kind === 'code') {
      const end = line.indexOf('`', earliest.pos + 1);
      const inner = end !== -1 ? line.slice(earliest.pos + 1, end) : '';
      if (end !== -1 && inner.length > 0) {
        tokens.push({ kind: 'code', value: inner });
        cursor = end + 1;
      } else {
        tokens.push({ kind: 'text', value: '`' });
        cursor = earliest.pos + 1;
      }
      continue;
    }

    if (earliest.kind === 'bold') {
      const end = findClosingDoubleStar(line, earliest.pos + 2);
      if (end !== -1) {
        tokens.push({ kind: 'bold', value: line.slice(earliest.pos + 2, end) });
        cursor = end + 2;
      } else {
        tokens.push({ kind: 'text', value: '**' });
        cursor = earliest.pos + 2;
      }
      continue;
    }

    // earliest.kind === 'italic'
    const end = findClosingSingleStar(line, earliest.pos + 1);
    if (end !== -1) {
      tokens.push({ kind: 'italic', value: line.slice(earliest.pos + 1, end) });
      cursor = end + 1;
    } else {
      tokens.push({ kind: 'text', value: '*' });
      cursor = earliest.pos + 1;
    }
  }

  return tokens;
}

function findNextSingleStar(line: string, from: number): number {
  let idx = line.indexOf('*', from);
  while (idx !== -1) {
    const nextChar = line[idx + 1];
    const prevChar = idx > 0 ? line[idx - 1] : '';
    if (nextChar !== '*' && prevChar !== '*') {
      return idx;
    }
    if (nextChar === '*') {
      idx = line.indexOf('*', idx + 2);
    } else {
      idx = line.indexOf('*', idx + 1);
    }
  }
  return -1;
}

function findClosingDoubleStar(line: string, contentStart: number): number {
  if (contentStart >= line.length || /\s/.test(line[contentStart])) {
    return -1;
  }
  let searchFrom = contentStart;
  while (searchFrom < line.length) {
    const closeIdx = line.indexOf('**', searchFrom);
    if (closeIdx === -1) {
      return -1;
    }
    if (closeIdx > contentStart && !/\s/.test(line[closeIdx - 1])) {
      return closeIdx;
    }
    searchFrom = closeIdx + 2;
  }
  return -1;
}

function findClosingSingleStar(line: string, contentStart: number): number {
  if (contentStart >= line.length || /\s/.test(line[contentStart])) {
    return -1;
  }
  let searchFrom = contentStart;
  while (searchFrom < line.length) {
    const closeIdx = line.indexOf('*', searchFrom);
    if (closeIdx === -1) {
      return -1;
    }
    if (line[closeIdx + 1] === '*') {
      searchFrom = closeIdx + 2;
      continue;
    }
    if (closeIdx > contentStart && !/\s/.test(line[closeIdx - 1])) {
      return closeIdx;
    }
    searchFrom = closeIdx + 1;
  }
  return -1;
}

function renderInlineContent(
  line: string,
  validIndices: Set<number>,
  activeSourceIndex: number | null,
  onSelectSource: (index: number) => void,
  blockKey: string,
): ReactNode[] {
  const inlineTokens = tokenizeInlineMarkdown(line);
  return inlineTokens.map((tok, tokIdx) => {
    const key = `${blockKey}-t${tokIdx}`;
    if (tok.kind === 'code') {
      return (
        <code key={key} className="inline-code">
          <bdi>{tok.value}</bdi>
        </code>
      );
    }

    const children = renderCitationsInText(
      tok.value,
      validIndices,
      activeSourceIndex,
      onSelectSource,
      key,
    );

    if (tok.kind === 'bold') {
      return <strong key={key}>{children}</strong>;
    }
    if (tok.kind === 'italic') {
      return <em key={key}>{children}</em>;
    }
    return <Fragment key={key}>{children}</Fragment>;
  });
}

type AnswerBlock =
  | { kind: 'paragraph'; text: string }
  | { kind: 'heading'; text: string }
  | { kind: 'ul'; items: string[] }
  | { kind: 'ol'; items: string[] };

function parseAnswerBlocks(text: string): AnswerBlock[] {
  const normalizedBreaks = text.replace(/([:：])\s+\*\s+(?=\*\*)/g, '$1\n* ');
  const rawLines = normalizedBreaks.split(/\r?\n/);
  const blocks: AnswerBlock[] = [];

  for (const rawLine of rawLines) {
    const trimmed = rawLine.trim();
    if (!trimmed) {
      continue;
    }

    const ulMatch = rawLine.match(/^\s*(?:[-*]|\u2022)\s+(.+)$/);
    if (ulMatch) {
      const last = blocks[blocks.length - 1];
      if (last && last.kind === 'ul') {
        last.items.push(ulMatch[1].trim());
      } else {
        blocks.push({ kind: 'ul', items: [ulMatch[1].trim()] });
      }
      continue;
    }

    const olMatch = rawLine.match(/^\s*\d{1,2}[.)]\s+(.+)$/);
    if (olMatch) {
      const last = blocks[blocks.length - 1];
      if (last && last.kind === 'ol') {
        last.items.push(olMatch[1].trim());
      } else {
        blocks.push({ kind: 'ol', items: [olMatch[1].trim()] });
      }
      continue;
    }

    const headingMatch = rawLine.match(/^#{1,3}\s+(.+)$/);
    if (headingMatch) {
      blocks.push({ kind: 'heading', text: headingMatch[1].trim() });
      continue;
    }

    blocks.push({ kind: 'paragraph', text: trimmed });
  }

  return blocks;
}

export function CitationText({
  text,
  sources,
  activeSourceIndex,
  onSelectSource,
}: CitationTextProps) {
  const validIndices = new Set(sources.map((s) => s.index));
  const blocks = parseAnswerBlocks(text);

  if (blocks.length === 0) {
    return null;
  }

  return (
    <div className="answer-body">
      {blocks.map((block, idx) => {
        if (block.kind === 'heading') {
          return (
            <h4 key={idx} className="answer-heading" dir="auto">
              {renderInlineContent(
                block.text,
                validIndices,
                activeSourceIndex,
                onSelectSource,
                `h-${idx}`,
              )}
            </h4>
          );
        }

        if (block.kind === 'ul') {
          return (
            <ul key={idx} className="answer-list">
              {block.items.map((item, itemIdx) => (
                <li key={itemIdx} dir="auto">
                  {renderInlineContent(
                    item,
                    validIndices,
                    activeSourceIndex,
                    onSelectSource,
                    `ul-${idx}-${itemIdx}`,
                  )}
                </li>
              ))}
            </ul>
          );
        }

        if (block.kind === 'ol') {
          return (
            <ol key={idx} className="answer-list answer-list-ordered">
              {block.items.map((item, itemIdx) => (
                <li key={itemIdx} dir="auto">
                  {renderInlineContent(
                    item,
                    validIndices,
                    activeSourceIndex,
                    onSelectSource,
                    `ol-${idx}-${itemIdx}`,
                  )}
                </li>
              ))}
            </ol>
          );
        }

        return (
          <p key={idx} className="answer-paragraph" dir="auto">
            {renderInlineContent(
              block.text,
              validIndices,
              activeSourceIndex,
              onSelectSource,
              `p-${idx}`,
            )}
          </p>
        );
      })}
    </div>
  );
}
