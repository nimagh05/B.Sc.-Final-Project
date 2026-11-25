import {
  ApiError,
  getFriendlyErrorMessage,
  isExpectedUserError,
  translateValidationDetail,
  type ErrorContext,
} from '../api/errors';
import { fa } from '../i18n/fa';
import { AlertTriangleIcon } from './Icons';

interface ErrorAlertProps {
  error: unknown;
  context?: ErrorContext;
  onRetry?: () => void;
  onDismiss?: () => void;
}

export function ErrorAlert({ error, context = 'general', onRetry, onDismiss }: ErrorAlertProps) {
  if (!error) {
    return null;
  }

  const message = getFriendlyErrorMessage(error, context);
  const traceId = error instanceof ApiError ? error.traceId : undefined;
  const expectedError = isExpectedUserError(error);
  const rawFieldErrors =
    error instanceof ApiError && error.errors ? Object.values(error.errors).flat() : [];
  const translatedFieldErrors = Array.from(
    new Set(
      rawFieldErrors
        .map((item) => translateValidationDetail(item))
        .filter((item): item is string => Boolean(item) && item !== message),
    ),
  );

  return (
    <div className="alert alert-error" role="alert">
      <div className="alert-content">
        <div className="alert-lead">
          <AlertTriangleIcon />
          <p className="alert-detail">{message}</p>
        </div>
        {translatedFieldErrors.length > 0 && (
          <ul style={{ margin: '4px 0 0', paddingInlineStart: '22px' }}>
            {translatedFieldErrors.map((item, idx) => (
              <li key={idx}>
                <bdi>{item}</bdi>
              </li>
            ))}
          </ul>
        )}
        {traceId &&
          (expectedError ? (
            <details className="alert-trace-details">
              <summary>{fa.common.traceIdToggle}</summary>
              <bdi dir="ltr" className="mono alert-trace-badge">
                {traceId}
              </bdi>
            </details>
          ) : (
            <div className="alert-footer">
              <span>{fa.common.traceIdLabel}</span>
              <bdi dir="ltr" className="mono alert-trace-badge">
                {traceId}
              </bdi>
            </div>
          ))}
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
