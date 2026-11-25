import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { AuthProvider } from '../auth/AuthContext';
import { tokenStorage } from '../auth/tokenStorage';
import { fa } from '../i18n/fa';
import { LoginPage } from '../pages/LoginPage';

function renderLogin() {
  return render(
    <AuthProvider>
      <MemoryRouter initialEntries={['/login']}>
        <Routes>
          <Route path="/login" element={<LoginPage />} />
          <Route path="/collections" element={<div>Collections Destination</div>} />
        </Routes>
      </MemoryRouter>
    </AuthProvider>,
  );
}

describe('LoginPage', () => {
  it('logs in, stores token outside localStorage, and navigates to /collections', async () => {
    const user = userEvent.setup();
    const expires = new Date(Date.now() + 60 * 60 * 1000).toISOString();

    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => {
      const url = String(input);
      if (url.endsWith('/api/v1/auth/login')) {
        return new Response(
          JSON.stringify({
            accessToken: 'jwt-abc-123',
            expiresAtUtc: expires,
            roles: ['User'],
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      if (url.endsWith('/api/v1/auth/me')) {
        return new Response(
          JSON.stringify({
            id: 'user-1',
            email: 'student@uni.ac.ir',
            roles: ['User'],
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });

    renderLogin();

    await user.type(screen.getByLabelText(fa.auth.emailLabel), 'student@uni.ac.ir');
    await user.type(screen.getByLabelText(fa.auth.passwordLabel), 'StrongPass123');
    await user.click(screen.getByRole('button', { name: fa.auth.loginButton }));

    expect(await screen.findByText('Collections Destination')).toBeInTheDocument();
    expect(tokenStorage.getAccessToken()).toBe('jwt-abc-123');
    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.getItem('docsearch_auth_session')).toContain('jwt-abc-123');
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('displays inline error on 401 invalid credentials without session-expired notice', async () => {
    const user = userEvent.setup();

    vi.spyOn(globalThis, 'fetch').mockImplementation(async () =>
      new Response(
        JSON.stringify({
          type: 'https://docsearch.local/errors/auth.invalid_credentials',
          title: 'Unauthorized',
          status: 401,
          detail: 'Invalid email or password.',
          code: 'auth.invalid_credentials',
          traceId: 'trace-login-fail',
        }),
        { status: 401, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    );

    renderLogin();

    await user.type(screen.getByLabelText(fa.auth.emailLabel), 'student@uni.ac.ir');
    await user.type(screen.getByLabelText(fa.auth.passwordLabel), 'WrongPass123');
    await user.click(screen.getByRole('button', { name: fa.auth.loginButton }));

    await waitFor(() => {
      expect(screen.getByText(fa.errors.invalidCredentials)).toBeInTheDocument();
    });
    expect(screen.queryByText(fa.auth.sessionExpired)).not.toBeInTheDocument();
    expect(tokenStorage.getAccessToken()).toBeNull();
  });

  it('validates registration password policy and switches back to login on success', async () => {
    const user = userEvent.setup();

    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ id: 'u2', email: 'new@uni.ac.ir' }), {
        status: 201,
        headers: { 'Content-Type': 'application/json' },
      }),
    );

    renderLogin();
    await user.click(screen.getByRole('tab', { name: fa.auth.registerTab }));

    await user.type(screen.getByLabelText(fa.auth.emailLabel), 'new@uni.ac.ir');
    await user.type(screen.getByLabelText(fa.auth.passwordLabel), 'weak');
    await user.click(screen.getByRole('button', { name: fa.auth.registerButton }));

    expect(screen.getByText(fa.auth.passwordInvalidPolicy)).toBeInTheDocument();

    const passInput = screen.getByLabelText(fa.auth.passwordLabel);
    await user.clear(passInput);
    await user.type(passInput, 'Str0ngPassword1');
    await user.click(screen.getByRole('button', { name: fa.auth.registerButton }));

    expect(await screen.findByText(fa.auth.registerSuccess)).toBeInTheDocument();
  });

  it('shows clear registration-disabled Persian error on 403 auth.forbidden', async () => {
    const user = userEvent.setup();

    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(
        JSON.stringify({
          type: 'https://docsearch.local/errors/auth.forbidden',
          title: 'Forbidden',
          status: 403,
          detail: 'Self-registration is disabled.',
          code: 'auth.forbidden',
          traceId: 'trace-reg-off',
        }),
        { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    );

    renderLogin();
    await user.click(screen.getByRole('tab', { name: fa.auth.registerTab }));

    await user.type(screen.getByLabelText(fa.auth.emailLabel), 'new@uni.ac.ir');
    await user.type(screen.getByLabelText(fa.auth.passwordLabel), 'Str0ngPassword1');
    await user.click(screen.getByRole('button', { name: fa.auth.registerButton }));

    expect(await screen.findByText(fa.errors.registrationDisabled)).toBeInTheDocument();
  });

  it('surfaces sessionExpiredNotice on mount when sessionStorage contains an expired session', async () => {
    window.sessionStorage.setItem(
      'docsearch_auth_session',
      JSON.stringify({
        accessToken: 'expired-jwt',
        expiresAtUtc: new Date(Date.now() - 120_000).toISOString(),
        roles: ['User'],
      }),
    );

    renderLogin();

    expect(await screen.findByText(fa.auth.sessionExpired)).toBeInTheDocument();
  });
});
