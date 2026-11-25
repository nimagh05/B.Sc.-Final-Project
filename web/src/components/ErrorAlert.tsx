import { ApiError, getFriendlyErrorMessage } from '../api/errors';
import { fa } from '../i18n/fa';

interface ErrorAlertProps {
  error: unknown;
  onRetry?: () => void;
  onDismiss?: () => void;
}

export function ErrorAlert({ error, onRetry, onDismiss }: ErrorAlertProps) {
  if (!error) {
    return null;
  }

  const message = getFriendlyErrorMessage(error);
  const traceId = error instanceof ApiError ? error.traceId : undefined;
  const fieldErrors = error instanceof ApiError && error.errors ? Object.values(error.errors).flat() : [];

  return (
    <div className="alert alert-error" role="alert">
      <div className="alert-content">
        <p className="alert-detail">{message}</p>
        {fieldErrors.length > 0 && (
          <ul style={{ margin: '4px 0 0', paddingInlineStart: '18px' }}>
            {fieldErrors.map((item, idx) => (
              <li key={idx}>
                <bdi>{item}</bdi>
              </li>
            ))}
          </ul>
        )}
        {traceId && (
          <span className="alert-trace">
            {fa.common.traceIdLabel} <bdi dir="ltr" className="mono">{traceId}</bdi>
          </span>
        )}
      </div>
      {(onRetry || onDismiss) && (
        <div className="doc-actions">
          {onRetry && (
            <button type="button" className="btn btn-secondary btn-sm" onClick={onRetry}>
              {fa.common.retry}
            </button>
          )}
          {onDismiss && (
            <button type="button" className="btn btn-ghost btn-sm" onClick={onDismiss}>
              {fa.common.close}
            </button>
          )}
        </div>
      )}
    </div>
  );
}
