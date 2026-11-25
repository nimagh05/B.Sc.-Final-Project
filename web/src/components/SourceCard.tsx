import type { SourceCitationDto } from '../api/types';
import { fa } from '../i18n/fa';

interface SourceCardProps {
  source: SourceCitationDto;
  elementId: string;
  isActive: boolean;
  isDownloading: boolean;
  onOpenPdf: (documentId: string, fileName: string) => void;
}

export function SourceCard({
  source,
  elementId,
  isActive,
  isDownloading,
  onOpenPdf,
}: SourceCardProps) {
  return (
    <article
      id={elementId}
      className="source-card"
      data-active={isActive ? 'true' : 'false'}
      tabIndex={-1}
    >
      <div className="source-card-header">
        <span className="source-index-pill" aria-hidden="true">
          <bdi dir="ltr">[{source.index}]</bdi>
        </span>
        <span className="source-file-title">
          <bdi>{source.fileName}</bdi>
        </span>
      </div>

      <div className="source-meta-row">
        <span>{fa.chat.sourceCard.page(source.page)}</span>
        {source.sectionTitle && (
          <span>
            {fa.chat.sourceCard.section} <bdi>{source.sectionTitle}</bdi>
          </span>
        )}
        <span>
          {fa.chat.sourceCard.score}{' '}
          <bdi dir="ltr" className="mono">
            {source.score.toFixed(2)}
          </bdi>
        </span>
      </div>

      <p className="source-snippet" dir="auto">
        {source.snippet}
      </p>

      <div>
        <button
          type="button"
          className="btn btn-secondary btn-sm"
          disabled={isDownloading}
          onClick={() => onOpenPdf(source.documentId, source.fileName)}
        >
          {isDownloading ? fa.chat.sourceCard.downloading : fa.chat.sourceCard.openPdf}
        </button>
      </div>
    </article>
  );
}
