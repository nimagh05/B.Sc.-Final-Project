import { fa } from '../i18n/fa';
import type { ProblemDetailsResponse } from './types';

export type ErrorContext =
  | 'auth'
  | 'register'
  | 'member'
  | 'upload'
  | 'collection'
  | 'document'
  | 'chat'
  | 'general';

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
          : status === 405
            ? 'http.method_not_allowed'
            : status === 413
              ? 'document.too_large'
              : status === 415
                ? 'http.unsupported_media_type'
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

  if (normalized.includes('password-protected') || normalized.includes('encrypted')) {
    return {
      title: fa.collectionDetail.documentErrors.encryptedPdf.title,
      guidance: fa.collectionDetail.documentErrors.encryptedPdf.guidance,
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

export function translateValidationDetail(rawMessage: string): string | null {
  const normalized = rawMessage.trim().toLowerCase();
  if (!normalized) {
    return null;
  }

  if (normalized.includes('valid email') || normalized.includes('email')) {
    if (normalized.includes('required')) {
      return fa.auth.emailRequired;
    }
    if (normalized.includes('already taken') || normalized.includes('already exists')) {
      return fa.errors.emailTaken;
    }
    return fa.auth.emailRequired;
  }

  if (normalized.includes('password') || normalized.includes('digit') || normalized.includes('uppercase') || normalized.includes('lowercase')) {
    return fa.auth.passwordInvalidPolicy;
  }

  if (normalized.includes('collection name')) {
    return fa.collections.nameRequired;
  }

  if (normalized.includes('question')) {
    return fa.chat.validation.questionLength;
  }

  if (normalized.includes('collectionids')) {
    return fa.chat.validation.noCollectionsSelected;
  }

  if (normalized.includes('topk')) {
    return 'مقدار تعداد نتایج بازیابی (topK) باید بین ۱ تا ۲۰ باشد.';
  }

  return null;
}

function resolveNotFoundMessage(detail: string, context: ErrorContext): string {
  const detailLower = detail.toLowerCase();

  if (detailLower.includes('user with this email')) {
    return fa.errors.memberUserNotFound;
  }
  if (detailLower.includes('conversation')) {
    return fa.errors.conversationNotFound;
  }
  if (detailLower.includes('none of the requested collections')) {
    return fa.errors.askCollectionsNotFound;
  }
  if (detailLower.includes('document')) {
    return fa.errors.documentNotFound;
  }
  if (detailLower.includes('collection')) {
    return fa.errors.collectionNotFound;
  }

  switch (context) {
    case 'member':
      return fa.errors.memberUserNotFound;
    case 'document':
      return fa.errors.documentNotFound;
    case 'collection':
      return fa.errors.collectionNotFound;
    case 'chat':
      return fa.errors.conversationNotFound;
    default:
      return fa.errors.notFound;
  }
}

function resolveForbiddenMessage(detail: string, context: ErrorContext): string {
  const detailLower = detail.toLowerCase();
  if (context === 'register' || detailLower.includes('self-registration')) {
    return fa.errors.registrationDisabled;
  }
  if (context === 'collection' || context === 'document' || context === 'member') {
    return fa.errors.forbiddenAction;
  }
  return fa.errors.forbidden;
}

function resolveServiceUnavailableMessage(detail: string): string {
  const detailLower = detail.toLowerCase();
  if (detailLower.includes('embedding')) {
    return fa.errors.serviceUnavailableEmbedding;
  }
  if (detailLower.includes('pdfextractor') || detailLower.includes('extractor')) {
    return fa.errors.serviceUnavailableExtractor;
  }
  return fa.errors.serviceUnavailable;
}

export function isExpectedUserError(error: unknown): boolean {
  if (!(error instanceof ApiError)) {
    return false;
  }
  return (
    !error.isNetworkError &&
    [400, 401, 403, 404, 409, 413, 415, 429].includes(error.status)
  );
}

export function getFriendlyErrorMessage(
  error: unknown,
  context: ErrorContext = 'general',
): string {
  if (!(error instanceof ApiError)) {
    return error instanceof Error && error.message ? error.message : fa.errors.unknown;
  }

  if (error.isNetworkError) {
    return fa.errors.network;
  }

  // Backend ExternalServiceUnavailableException currently uses status 503 with code "server.error"
  if (error.status === 503) {
    return resolveServiceUnavailableMessage(error.detail);
  }

  if (error.status === 502 || error.status === 504) {
    return fa.errors.gatewayTimeout;
  }

  switch (error.code) {
    case 'auth.invalid_credentials':
      return fa.errors.invalidCredentials;
    case 'auth.unauthorized':
      return fa.errors.unauthorized;
    case 'auth.forbidden':
      return resolveForbiddenMessage(error.detail, context);
    case 'auth.email_taken':
      return fa.errors.emailTaken;
    case 'resource.not_found':
      return resolveNotFoundMessage(error.detail, context);
    case 'collection.name_taken':
      return fa.errors.collectionNameTaken;
    case 'collection.owner_protected': {
      const detailLower = error.detail.toLowerCase();
      if (detailLower.includes('demote')) {
        return fa.errors.ownerProtectedDemote;
      }
      if (detailLower.includes('remove')) {
        return fa.errors.ownerProtectedRemove;
      }
      return fa.errors.ownerProtected;
    }
    case 'document.invalid_file':
      return fa.errors.invalidFile;
    case 'document.too_large':
      return fa.errors.fileTooLarge;
    case 'document.duplicate':
      return fa.errors.duplicateDocument;
    case 'document.conflict':
      return fa.errors.documentConflict;
    case 'rag.invalid_scope':
      return fa.errors.ragInvalidScope;
    case 'reindex.already_running':
      return fa.errors.reindexAlreadyRunning;
    case 'http.method_not_allowed':
      return fa.errors.methodNotAllowed;
    case 'http.unsupported_media_type':
      return fa.errors.unsupportedMediaType;
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
      if (error.status === 403) return resolveForbiddenMessage(error.detail, context);
      if (error.status === 404) return resolveNotFoundMessage(error.detail, context);
      if (error.status === 405) return fa.errors.methodNotAllowed;
      if (error.status === 413) return fa.errors.fileTooLarge;
      if (error.status === 415) return fa.errors.unsupportedMediaType;
      if (error.status === 400) return fa.errors.validationFailed;
      if (error.status >= 500) return fa.errors.serverError;
      return fa.errors.unknown;
  }
}
