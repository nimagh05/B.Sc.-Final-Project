import { tokenStorage } from '../auth/tokenStorage';
import { fa } from '../i18n/fa';
import { ApiError, parseProblemDetailsPayload, parseRetryAfter } from './errors';

type UnauthorizedHandler = () => void;

let unauthorizedHandler: UnauthorizedHandler | null = null;

export function setUnauthorizedHandler(handler: UnauthorizedHandler | null): void {
  unauthorizedHandler = handler;
}

export function getApiBaseUrl(): string {
  const configured = import.meta.env.VITE_API_BASE_URL;
  if (typeof configured === 'string' && configured.trim().length > 0) {
    return configured.trim().replace(/\/+$/, '');
  }
  return '';
}

function buildUrl(path: string): string {
  const base = getApiBaseUrl();
  const normalizedPath = path.startsWith('/') ? path : `/${path}`;
  return `${base}${normalizedPath}`;
}

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE';
  body?: unknown;
  signal?: AbortSignal;
  attachAuth?: boolean;
}

export async function apiRequest<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { method = 'GET', body, signal, attachAuth = true } = options;
  const headers: Record<string, string> = {
    Accept: 'application/json, application/problem+json',
  };

  let tokenAttached = false;
  if (attachAuth) {
    const token = tokenStorage.getAccessToken();
    if (token) {
      headers.Authorization = `Bearer ${token}`;
      tokenAttached = true;
    }
  }

  let serializedBody: string | undefined;
  if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
    serializedBody = JSON.stringify(body);
  }

  let response: Response;
  try {
    response = await fetch(buildUrl(path), {
      method,
      headers,
      body: serializedBody,
      signal,
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

  if (response.status === 401 && tokenAttached) {
    unauthorizedHandler?.();
  }

  if (response.status === 204) {
    return undefined as T;
  }

  const rawText = await response.text();
  if (!response.ok) {
    const retryAfter =
      response.status === 429 ? parseRetryAfter(response.headers.get('Retry-After')) : undefined;
    throw parseProblemDetailsPayload(rawText, response.status, retryAfter);
  }

  if (!rawText) {
    return undefined as T;
  }

  return JSON.parse(rawText) as T;
}

export async function apiDownloadBlob(path: string, signal?: AbortSignal): Promise<Blob> {
  const headers: Record<string, string> = {
    Accept: 'application/pdf, application/problem+json',
  };

  const token = tokenStorage.getAccessToken();
  const tokenAttached = Boolean(token);
  if (token) {
    headers.Authorization = `Bearer ${token}`;
  }

  let response: Response;
  try {
    response = await fetch(buildUrl(path), {
      method: 'GET',
      headers,
      signal,
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

  if (response.status === 401 && tokenAttached) {
    unauthorizedHandler?.();
  }

  if (!response.ok) {
    const rawText = await response.text();
    const retryAfter =
      response.status === 429 ? parseRetryAfter(response.headers.get('Retry-After')) : undefined;
    throw parseProblemDetailsPayload(rawText, response.status, retryAfter);
  }

  return response.blob();
}

export function apiUploadFile<T>(
  path: string,
  file: File,
  options: {
    onProgress?: (percent: number) => void;
    signal?: AbortSignal;
  } = {},
): Promise<T> {
  const { onProgress, signal } = options;

  return new Promise<T>((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    const formData = new FormData();
    formData.append('file', file);

    let tokenAttached = false;

    const cleanup = () => {
      if (signal) {
        signal.removeEventListener('abort', handleAbort);
      }
    };

    const handleAbort = () => {
      xhr.abort();
      cleanup();
      reject(new DOMException('Upload aborted', 'AbortError'));
    };

    if (signal) {
      if (signal.aborted) {
        reject(new DOMException('Upload aborted', 'AbortError'));
        return;
      }
      signal.addEventListener('abort', handleAbort, { once: true });
    }

    xhr.open('POST', buildUrl(path));
    xhr.setRequestHeader('Accept', 'application/json, application/problem+json');

    const token = tokenStorage.getAccessToken();
    if (token) {
      xhr.setRequestHeader('Authorization', `Bearer ${token}`);
      tokenAttached = true;
    }

    xhr.upload.onprogress = (event) => {
      if (onProgress && event.lengthComputable && event.total > 0) {
        const percent = Math.min(100, Math.max(0, Math.round((event.loaded / event.total) * 100)));
        onProgress(percent);
      }
    };

    xhr.onload = () => {
      cleanup();
      if (xhr.status === 401 && tokenAttached) {
        unauthorizedHandler?.();
      }

      if (xhr.status >= 200 && xhr.status < 300) {
        try {
          resolve(JSON.parse(xhr.responseText) as T);
        } catch {
          reject(
            new ApiError({
              status: xhr.status,
              code: 'response.invalid_json',
              detail: fa.errors.unknown,
            }),
          );
        }
        return;
      }

      const retryAfter =
        xhr.status === 429 ? parseRetryAfter(xhr.getResponseHeader('Retry-After')) : undefined;
      reject(parseProblemDetailsPayload(xhr.responseText ?? '', xhr.status, retryAfter));
    };

    xhr.onerror = () => {
      cleanup();
      reject(
        new ApiError({
          status: 0,
          code: 'network.error',
          detail: fa.errors.network,
          isNetworkError: true,
        }),
      );
    };

    xhr.ontimeout = () => {
      cleanup();
      reject(
        new ApiError({
          status: 0,
          code: 'network.error',
          detail: fa.errors.network,
          isNetworkError: true,
        }),
      );
    };

    xhr.send(formData);
  });
}
