import { describe, expect, it, vi } from 'vitest';
import { apiRequest, setUnauthorizedHandler } from '../api/client';
import { authApi } from '../api/endpoints';
import { ApiError } from '../api/errors';
import { tokenStorage } from '../auth/tokenStorage';

describe('tokenStorage and 401 handling', () => {
  it('stores JWT in memory and sessionStorage without touching localStorage', () => {
    const localSetSpy = vi.spyOn(Storage.prototype, 'setItem');
    const future = new Date(Date.now() + 60 * 60 * 1000).toISOString();

    tokenStorage.setSession({
      accessToken: 'jwt-test-token',
      expiresAtUtc: future,
      roles: ['User'],
    });

    expect(tokenStorage.getAccessToken()).toBe('jwt-test-token');
    expect(window.sessionStorage.getItem('docsearch_auth_session')).toContain('jwt-test-token');
    expect(window.localStorage.length).toBe(0);
    expect(localSetSpy).toHaveBeenCalledTimes(1);
    expect(localSetSpy.mock.instances[0]).toBe(window.sessionStorage);

    tokenStorage.clear();
    expect(tokenStorage.getAccessToken()).toBeNull();
    expect(window.sessionStorage.getItem('docsearch_auth_session')).toBeNull();
  });

  it('rejects expired tokens and records one-shot expired notice flag', () => {
    const past = new Date(Date.now() - 60_000).toISOString();
    tokenStorage.setSession({
      accessToken: 'expired-token',
      expiresAtUtc: past,
      roles: ['User'],
    });

    expect(tokenStorage.getAccessToken()).toBeNull();
    expect(tokenStorage.consumeExpiredNotice()).toBe(true);
    expect(tokenStorage.consumeExpiredNotice()).toBe(false);
  });

  it('triggers unauthorized handler only when Bearer token was attached, never on login', async () => {
    const unauthorizedMock = vi.fn();
    setUnauthorizedHandler(unauthorizedMock);

    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(
        JSON.stringify({
          type: 'https://docsearch.local/errors/auth.invalid_credentials',
          title: 'Unauthorized',
          status: 401,
          detail: 'Invalid email or password.',
          code: 'auth.invalid_credentials',
          traceId: 'trace-1',
        }),
        {
          status: 401,
          headers: { 'Content-Type': 'application/problem+json' },
        },
      ),
    );

    await expect(authApi.login({ email: 'a@b.com', password: 'wrong' })).rejects.toBeInstanceOf(
      ApiError,
    );
    expect(unauthorizedMock).not.toHaveBeenCalled();

    tokenStorage.setSession({
      accessToken: 'valid-jwt',
      expiresAtUtc: new Date(Date.now() + 3600_000).toISOString(),
      roles: ['User'],
    });

    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          type: 'https://docsearch.local/errors/auth.unauthorized',
          title: 'Unauthorized',
          status: 401,
          detail: 'Authentication is required.',
          code: 'auth.unauthorized',
          traceId: 'trace-2',
        }),
        {
          status: 401,
          headers: { 'Content-Type': 'application/problem+json' },
        },
      ),
    );

    await expect(apiRequest('/api/v1/collections')).rejects.toBeInstanceOf(ApiError);
    expect(unauthorizedMock).toHaveBeenCalledTimes(1);

    setUnauthorizedHandler(null);
  });
});
