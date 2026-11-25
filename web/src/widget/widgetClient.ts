import { ApiError, parseProblemDetailsPayload, parseRetryAfter } from '../api/errors';
import type { AskRequestDto, AskResponse } from '../api/types';
import { fa } from '../i18n/fa';

export interface ParsedApiBaseUrl {
  valid: boolean;
  baseUrl: string;
}

export function normalizeApiBaseUrl(raw: string): ParsedApiBaseUrl {
  const trimmed = raw.trim();
  if (!trimmed) {
    return { valid: false, baseUrl: '' };
  }

  // Allow same-origin relative path prefix (e.g. "/docsearch-proxy" or "/"), reject protocol-relative "//".
  if (trimmed.startsWith('/')) {
    if (trimmed.startsWith('//')) {
      return { valid: false, baseUrl: '' };
    }
    return { valid: true, baseUrl: trimmed.replace(/\/+$/, '') };
  }

  try {
    const parsed = new URL(trimmed);
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
      return { valid: false, baseUrl: '' };
    }
    const path = parsed.pathname === '/' ? '' : parsed.pathname.replace(/\/+$/, '');
    return { valid: true, baseUrl: `${parsed.origin}${path}` };
  } catch {
    return { valid: false, baseUrl: '' };
  }
}

export function parseFontBaseUrl(raw: string): string | null {
  const trimmed = raw.trim();
  if (!trimmed) {
    return null;
  }

  try {
    const parsed = new URL(trimmed);
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
      return null;
    }
    const path = parsed.pathname === '/' ? '' : parsed.pathname.replace(/\/+$/, '');
    return `${parsed.origin}${path}`;
  } catch {
    return null;
  }
}

export function parseCollectionIds(raw: string): string[] {
  const parts = raw
    .split(',')
    .map((item) => item.trim())
    .filter((item) => item.length > 0);
  return Array.from(new Set(parts));
}

function buildEndpointUrl(baseUrl: string, path: string): string {
  const normalizedBase = baseUrl.replace(/\/+$/, '');
  const normalizedPath = path.startsWith('/') ? path : `/${path}`;
  return `${normalizedBase}${normalizedPath}`;
}

export async function widgetAsk(params: {
  apiUrl: string;
  token: string;
  payload: AskRequestDto;
  signal?: AbortSignal;
}): Promise<AskResponse> {
  const url = buildEndpointUrl(params.apiUrl, '/api/v1/ask');

  let response: Response;
  try {
    response = await fetch(url, {
      method: 'POST',
      headers: {
        Accept: 'application/json, application/problem+json',
        'Content-Type': 'application/json',
        Authorization: `Bearer ${params.token}`,
      },
      body: JSON.stringify(params.payload),
      signal: params.signal,
    });
  } catch (err) {
    if (err instanceof DOMException && err.name === 'AbortError') {
      throw err;
    }
    throw new ApiError({
      status: 0,
      code: 'network.error',
      detail: fa.errors.network,
      isNetworkError: true,
    });
  }

  const rawText = await response.text();
  if (!response.ok) {
    const retryAfter =
      response.status === 429 ? parseRetryAfter(response.headers.get('Retry-After')) : undefined;
    throw parseProblemDetailsPayload(rawText, response.status, retryAfter);
  }

  if (!rawText) {
    throw new ApiError({
      status: response.status,
      code: 'response.empty',
      detail: fa.errors.unknown,
    });
  }

  return JSON.parse(rawText) as AskResponse;
}

export async function widgetDownloadDocument(params: {
  apiUrl: string;
  token: string;
  documentId: string;
  signal?: AbortSignal;
}): Promise<Blob> {
  const url = buildEndpointUrl(
    params.apiUrl,
    `/api/v1/documents/${encodeURIComponent(params.documentId)}/file`,
  );

  let response: Response;
  try {
    response = await fetch(url, {
      method: 'GET',
      headers: {
        Accept: 'application/pdf, application/problem+json',
        Authorization: `Bearer ${params.token}`,
      },
      signal: params.signal,
    });
  } catch (err) {
    if (err instanceof DOMException && err.name === 'AbortError') {
      throw err;
    }
    throw new ApiError({
      status: 0,
      code: 'network.error',
      detail: fa.errors.network,
      isNetworkError: true,
    });
  }

  if (!response.ok) {
    const rawText = await response.text();
    const retryAfter =
      response.status === 429 ? parseRetryAfter(response.headers.get('Retry-After')) : undefined;
    throw parseProblemDetailsPayload(rawText, response.status, retryAfter);
  }

  return response.blob();
}

export function openPdfBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const opened = window.open(url, '_blank', 'noopener,noreferrer');

  if (!opened) {
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
  }

  window.setTimeout(() => {
    URL.revokeObjectURL(url);
  }, 60_000);
}
