import { describe, expect, it } from 'vitest';
import {
  ApiError,
  getFriendlyErrorMessage,
  isExpectedUserError,
  translateDocumentError,
  translateValidationDetail,
} from '../api/errors';
import { fa } from '../i18n/fa';

describe('translateDocumentError', () => {
  it('maps "Document contains no extractable text." to Persian guidance', () => {
    const result = translateDocumentError('Document contains no extractable text.');
    expect(result.title).toBe(fa.collectionDetail.documentErrors.noExtractableText.title);
    expect(result.guidance).toBe(fa.collectionDetail.documentErrors.noExtractableText.guidance);
    expect(result.technicalDetail).toBe('Document contains no extractable text.');
  });

  it('maps password-protected / encrypted PDF errors to Persian encrypted guidance', () => {
    const result = translateDocumentError('The provided PDF is invalid or corrupt: Password-protected or encrypted PDFs are not supported.');
    expect(result.title).toBe(fa.collectionDetail.documentErrors.encryptedPdf.title);
    expect(result.guidance).toBe(fa.collectionDetail.documentErrors.encryptedPdf.guidance);
  });

  it('maps timeout errors to Persian timeout message', () => {
    const result = translateDocumentError('The operation timed out during document processing.');
    expect(result.title).toBe(fa.collectionDetail.documentErrors.timeout.title);
    expect(result.guidance).toContain('پردازش مجدد');
    expect(result.technicalDetail).toBe('The operation timed out during document processing.');
  });

  it('maps unknown errors to Persian fallback while keeping technical detail secondary', () => {
    const result = translateDocumentError('An unexpected internal failure happened (NullReferenceException).');
    expect(result.title).toBe(fa.collectionDetail.documentErrors.unknown.title);
    expect(result.guidance).toBe(fa.collectionDetail.documentErrors.unknown.guidance);
    expect(result.technicalDetail).toBe('An unexpected internal failure happened (NullReferenceException).');
  });

  it('handles null or empty error messages cleanly', () => {
    const result = translateDocumentError(null);
    expect(result.title).toBe(fa.collectionDetail.documentErrors.unknown.title);
    expect(result.technicalDetail).toBeUndefined();
  });
});

describe('getFriendlyErrorMessage', () => {
  it('maps unregistered member email 404 to clear Persian message', () => {
    const err = new ApiError({
      status: 404,
      code: 'resource.not_found',
      detail: 'User with this email was not found.',
    });
    expect(getFriendlyErrorMessage(err, 'member')).toBe(fa.errors.memberUserNotFound);
  });

  it('disambiguates resource.not_found across conversation, collection, and document', () => {
    expect(
      getFriendlyErrorMessage(
        new ApiError({ status: 404, code: 'resource.not_found', detail: 'Conversation was not found.' }),
      ),
    ).toBe(fa.errors.conversationNotFound);

    expect(
      getFriendlyErrorMessage(
        new ApiError({
          status: 404,
          code: 'resource.not_found',
          detail: 'None of the requested collections were found or visible.',
        }),
      ),
    ).toBe(fa.errors.askCollectionsNotFound);

    expect(
      getFriendlyErrorMessage(
        new ApiError({ status: 404, code: 'resource.not_found', detail: 'Document was not found or is not visible.' }),
      ),
    ).toBe(fa.errors.documentNotFound);
  });

  it('maps 403 disabled self-registration vs permission denied cleanly', () => {
    const disabledReg = new ApiError({
      status: 403,
      code: 'auth.forbidden',
      detail: 'Self-registration is disabled.',
    });
    expect(getFriendlyErrorMessage(disabledReg, 'register')).toBe(fa.errors.registrationDisabled);

    const permDenied = new ApiError({
      status: 403,
      code: 'auth.forbidden',
      detail: 'You do not have permission to perform this action.',
    });
    expect(getFriendlyErrorMessage(permDenied, 'member')).toBe(fa.errors.forbiddenAction);
  });

  it('maps 503 ExternalServiceUnavailableException before generic server.error', () => {
    const embeddingDown = new ApiError({
      status: 503,
      code: 'server.error',
      detail: "External service 'Embedding' is unavailable: Embedding service is currently unavailable.",
    });
    expect(getFriendlyErrorMessage(embeddingDown)).toBe(fa.errors.serviceUnavailableEmbedding);

    const extractorDown = new ApiError({
      status: 503,
      code: 'server.error',
      detail: "External service 'PdfExtractor' is unavailable: connection failed.",
    });
    expect(getFriendlyErrorMessage(extractorDown)).toBe(fa.errors.serviceUnavailableExtractor);
    expect(isExpectedUserError(embeddingDown)).toBe(false);
  });

  it('maps rag.invalid_scope, reindex.already_running, 405, 415, and owner_protected', () => {
    expect(
      getFriendlyErrorMessage(
        new ApiError({ status: 400, code: 'rag.invalid_scope', detail: 'Invalid scope' }),
      ),
    ).toBe(fa.errors.ragInvalidScope);

    expect(
      getFriendlyErrorMessage(
        new ApiError({ status: 409, code: 'reindex.already_running', detail: 'Already running' }),
      ),
    ).toBe(fa.errors.reindexAlreadyRunning);

    expect(
      getFriendlyErrorMessage(
        new ApiError({ status: 405, code: 'http.method_not_allowed', detail: 'Method not allowed' }),
      ),
    ).toBe(fa.errors.methodNotAllowed);

    expect(
      getFriendlyErrorMessage(
        new ApiError({ status: 415, code: 'http.unsupported_media_type', detail: 'Unsupported media type' }),
      ),
    ).toBe(fa.errors.unsupportedMediaType);

    expect(
      getFriendlyErrorMessage(
        new ApiError({ status: 409, code: 'collection.owner_protected', detail: 'Cannot demote the collection owner.' }),
      ),
    ).toBe(fa.errors.ownerProtectedDemote);
  });

  it('translates common backend English validation details to Persian', () => {
    expect(translateValidationDetail('A valid email address is required.')).toBe(fa.auth.emailRequired);
    expect(translateValidationDetail('Password must contain at least one digit.')).toBe(
      fa.auth.passwordInvalidPolicy,
    );
    expect(translateValidationDetail('Collection name is required.')).toBe(fa.collections.nameRequired);
    expect(translateValidationDetail('Question must be between 3 and 2000 characters.')).toBe(
      fa.chat.validation.questionLength,
    );
  });
});
