import { fa } from '../i18n/fa';
import type { ProblemDetailsResponse } from './types';

export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly detail: string;
  readonly traceId?: string;
  readonly errors?: Record<string, string[]> | null;
  readonly retryAfterSeconds?: number;
  readonly isNetworkError: boolean;

  constructor(params: {
    status: number;
    code: string;
    detail: string;
    traceId?: string;
    errors?: Record<string, string[]> | null;
    retryAfterSeconds?: number;
    isNetworkError?: boolean;
  }) {
    super(params.detail);
    this.name = 'ApiError';
    this.status = params.status;
    this.code = params.code;
    this.detail = params.detail;
    this.traceId = params.traceId;
    this.errors = params.errors;
    this.retryAfterSeconds = params.retryAfterSeconds;
    this.isNetworkError = Boolean(params.isNetworkError);
  }
}

export function parseRetryAfter(headerValue: string | null): number | undefined {
  if (!headerValue) {
    return undefined;
  }

  const trimmed = headerValue.trim();
  if (/^\d+$/.test(trimmed)) {
    const parsed = Number.parseInt(trimmed, 10);
    return Number.isFinite(parsed) && parsed > 0 ? parsed : undefined;
  }

  const targetDateMs = Date.parse(trimmed);
  if (!Number.isNaN(targetDateMs)) {
    const deltaSeconds = Math.ceil((targetDateMs - Date.now()) / 1000);
    return deltaSeconds > 0 ? deltaSeconds : 1;
  }

  return undefined;
}

export function parseProblemDetailsPayload(
  rawText: string,
  status: number,
  retryAfterSeconds?: number,
): ApiError {
  if (rawText.trim().length > 0) {
    try {
      const parsed = JSON.parse(rawText) as Partial<ProblemDetailsResponse>;
      if (typeof parsed === 'object' && parsed !== null) {
        return new ApiError({
          status: typeof parsed.status === 'number' ? parsed.status : status,
          code: typeof parsed.code === 'string' && parsed.code ? parsed.code : `http.${status}`,
          detail:
            typeof parsed.detail === 'string' && parsed.detail
              ? parsed.detail
              : typeof parsed.title === 'string' && parsed.title
                ? parsed.title
                : fa.errors.unknown,
          traceId: typeof parsed.traceId === 'string' ? parsed.traceId : undefined,
          errors: parsed.errors ?? null,
          retryAfterSeconds,
        });
      }
    } catch {
      // Non-JSON error body; fall back below.
    }
  }

  const fallbackCode =
    status === 401
      ? 'auth.unauthorized'
      : status === 403
        ? 'auth.forbidden'
        : status === 404
          ? 'resource.not_found'
          : status === 413
            ? 'document.too_large'
            : status === 429
              ? 'rate.limited'
              : status >= 500
                ? 'server.error'
                : `http.${status}`;

  return new ApiError({
    status,
    code: fallbackCode,
    detail: fa.errors.unknown,
    retryAfterSeconds,
  });
}

export interface TranslatedDocumentError {
  title: string;
  guidance: string;
  technicalDetail?: string;
}

export function translateDocumentError(rawError?: string | null): TranslatedDocumentError {
  const trimmed = rawError?.trim();
  const normalized = trimmed?.toLowerCase() ?? '';

  if (!trimmed) {
    return {
      title: fa.collectionDetail.documentErrors.unknown.title,
      guidance: fa.collectionDetail.documentErrors.unknown.guidance,
    };
  }

  if (normalized.includes('no extractable text')) {
    return {
      title: fa.collectionDetail.documentErrors.noExtractableText.title,
      guidance: fa.collectionDetail.documentErrors.noExtractableText.guidance,
      technicalDetail: trimmed,
    };
  }

  if (normalized.includes('no text chunks') || normalized.includes('produced no chunks')) {
    return {
      title: fa.collectionDetail.documentErrors.noChunks.title,
      guidance: fa.collectionDetail.documentErrors.noChunks.guidance,
      technicalDetail: trimmed,
    };
  }

  if (normalized.includes('timed out') || normalized.includes('timeout')) {
    return {
      title: fa.collectionDetail.documentErrors.timeout.title,
      guidance: fa.collectionDetail.documentErrors.timeout.guidance,
      technicalDetail: trimmed,
    };
  }

  if (normalized.includes('invalid or corrupt') || normalized.includes('corrupt')) {
    return {
      title: fa.collectionDetail.documentErrors.corruptPdf.title,
      guidance: fa.collectionDetail.documentErrors.corruptPdf.guidance,
      technicalDetail: trimmed,
    };
  }

  if (normalized.includes('exceeds') || normalized.includes('size limit') || normalized.includes('too large')) {
    return {
      title: fa.collectionDetail.documentErrors.tooLarge.title,
      guidance: fa.collectionDetail.documentErrors.tooLarge.guidance,
      technicalDetail: trimmed,
    };
  }

  if (normalized.includes('embedding') || normalized.includes('dimension mismatch')) {
    return {
      title: fa.collectionDetail.documentErrors.embeddingUnavailable.title,
      guidance: fa.collectionDetail.documentErrors.embeddingUnavailable.guidance,
      technicalDetail: trimmed,
    };
  }

  if (
    normalized.includes('extract') ||
    normalized.includes('sidecar') ||
    normalized.includes('ocr')
  ) {
    return {
      title: fa.collectionDetail.documentErrors.extractionUnavailable.title,
      guidance: fa.collectionDetail.documentErrors.extractionUnavailable.guidance,
      technicalDetail: trimmed,
    };
  }

  return {
    title: fa.collectionDetail.documentErrors.unknown.title,
    guidance: fa.collectionDetail.documentErrors.unknown.guidance,
    technicalDetail: trimmed,
  };
}

export function getFriendlyErrorMessage(error: unknown): string {
  if (!(error instanceof ApiError)) {
    return error instanceof Error && error.message ? error.message : fa.errors.unknown;
  }

  if (error.isNetworkError) {
    return fa.errors.network;
  }

  switch (error.code) {
    case 'auth.invalid_credentials':
      return fa.errors.invalidCredentials;
    case 'auth.unauthorized':
      return fa.errors.unauthorized;
    case 'auth.forbidden':
      return fa.errors.forbidden;
    case 'auth.email_taken':
      return fa.errors.emailTaken;
    case 'resource.not_found':
      return fa.errors.notFound;
    case 'collection.name_taken':
      return fa.errors.collectionNameTaken;
    case 'collection.owner_protected':
      return fa.errors.ownerProtected;
    case 'document.invalid_file':
      return fa.errors.invalidFile;
    case 'document.too_large':
      return fa.errors.fileTooLarge;
    case 'document.duplicate':
      return fa.errors.duplicateDocument;
    case 'document.conflict':
      return fa.errors.documentConflict;
    case 'validation.failed':
      return fa.errors.validationFailed;
    case 'rate.limited':
      return error.retryAfterSeconds
        ? fa.errors.rateLimitedWithSeconds(error.retryAfterSeconds)
        : fa.errors.rateLimited;
    case 'server.error':
      return fa.errors.serverError;
    default:
      if (error.status === 429) {
        return error.retryAfterSeconds
          ? fa.errors.rateLimitedWithSeconds(error.retryAfterSeconds)
          : fa.errors.rateLimited;
      }
      if (error.status === 401) return fa.errors.unauthorized;
      if (error.status === 403) return fa.errors.forbidden;
      if (error.status === 404) return fa.errors.notFound;
      if (error.status === 413) return fa.errors.fileTooLarge;
      if (error.status >= 500) return fa.errors.serverError;
      return error.detail || fa.errors.unknown;
  }
}
