import type { ReactNode } from 'react';
import type { SourceCitationDto } from '../api/types';
import { fa } from '../i18n/fa';

interface CitationTextProps {
  text: string;
  sources: SourceCitationDto[];
  activeSourceIndex: number | null;
  onSelectSource: (index: number) => void;
}

const BRACKET_PATTERN = /\[(\d+(?:\s*[,،]\s*\d+)*)\]/g;

function renderParagraphContent(
  line: string,
  validIndices: Set<number>,
  activeSourceIndex: number | null,
  onSelectSource: (index: number) => void,
  lineKey: number,
): ReactNode[] {
  const nodes: ReactNode[] = [];
  let lastIndex = 0;

  BRACKET_PATTERN.lastIndex = 0;
  let match: RegExpExecArray | null;

  while ((match = BRACKET_PATTERN.exec(line)) !== null) {
    if (match.index > lastIndex) {
      nodes.push(line.slice(lastIndex, match.index));
    }

    const rawGroup = match[1];
    const parts = rawGroup.split(/([,،]\s*)/);
    const parsedTokens: Array<{ isSeparator: boolean; raw: string; num?: number; isValid?: boolean }> = [];
    let hasAtLeastOneValid = false;

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
        }
        parsedTokens.push({ isSeparator: false, raw: part, num, isValid });
      } else {
        parsedTokens.push({ isSeparator: false, raw: part, isValid: false });
      }
    }

    if (!hasAtLeastOneValid) {
      nodes.push(match[0]);
    } else {
      nodes.push(
        <span key={`c-${lineKey}-${match.index}`} className="citation-group" dir="ltr">
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
                  key={`b-${lineKey}-${match!.index}-${tokIdx}`}
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

  if (lastIndex < line.length) {
    nodes.push(line.slice(lastIndex));
  }

  return nodes;
}

export function CitationText({
  text,
  sources,
  activeSourceIndex,
  onSelectSource,
}: CitationTextProps) {
  const validIndices = new Set(sources.map((s) => s.index));
  const lines = text.split(/\r?\n/).filter((line) => line.trim().length > 0);

  if (lines.length === 0) {
    return null;
  }

  return (
    <div className="answer-body">
      {lines.map((line, idx) => (
        <p key={idx} className="answer-paragraph" dir="auto">
          {renderParagraphContent(line, validIndices, activeSourceIndex, onSelectSource, idx)}
        </p>
      ))}
    </div>
  );
}
