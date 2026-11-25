import { describe, expect, it } from 'vitest';
import { translateDocumentError } from '../api/errors';
import { fa } from '../i18n/fa';

describe('translateDocumentError', () => {
  it('maps "Document contains no extractable text." to Persian OCR guidance', () => {
    const result = translateDocumentError('Document contains no extractable text.');
    expect(result.title).toBe(fa.collectionDetail.documentErrors.noExtractableText.title);
    expect(result.guidance).toContain('OCR');
    expect(result.technicalDetail).toBe('Document contains no extractable text.');
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
