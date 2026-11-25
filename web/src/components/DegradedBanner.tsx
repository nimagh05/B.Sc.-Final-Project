import { fa } from '../i18n/fa';

export function DegradedBanner() {
  return (
    <div className="alert alert-warning" role="status" aria-live="polite">
      <div className="alert-content">
        <p className="alert-title">{fa.chat.degradedTitle}</p>
        <p className="alert-detail">{fa.chat.degradedDescription}</p>
      </div>
    </div>
  );
}
