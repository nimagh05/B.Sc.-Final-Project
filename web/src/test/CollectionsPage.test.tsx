import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { AuthProvider } from '../auth/AuthContext';
import { tokenStorage } from '../auth/tokenStorage';
import { fa } from '../i18n/fa';
import { CollectionsPage } from '../pages/CollectionsPage';

function seedAuth() {
  tokenStorage.setSession({
    accessToken: 'jwt-collections-token',
    expiresAtUtc: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
    roles: ['User'],
  });
}

function renderCollections() {
  return render(
    <AuthProvider>
      <MemoryRouter initialEntries={['/collections']}>
        <Routes>
          <Route path="/collections" element={<CollectionsPage />} />
          <Route path="/login" element={<div>Login Page</div>} />
        </Routes>
      </MemoryRouter>
    </AuthProvider>,
  );
}

describe('CollectionsPage', () => {
  it('shows skeleton loader, aligns English descriptions via <bdi> without dir="auto" block flip, and filters locally', async () => {
    seedAuth();
    const user = userEvent.setup();

    vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => {
      const url = String(input);
      if (url.endsWith('/api/v1/auth/me')) {
        return new Response(
          JSON.stringify({ id: 'u1', email: 'u1@uni.ac.ir', roles: ['User'] }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      if (url.includes('/api/v1/collections')) {
        return new Response(
          JSON.stringify({
            items: [
              {
                id: 'c1',
                name: 'آیین‌نامه آموزشی',
                description: 'English academic regulations guide',
                role: 'Owner',
                documentCount: 3,
                createdAt: '2026-10-01T10:00:00Z',
              },
              {
                id: 'c2',
                name: 'پژوهش و پایان نامه',
                description: 'دستورالعمل نگارش رساله',
                role: 'Viewer',
                documentCount: 1,
                createdAt: '2026-10-02T10:00:00Z',
              },
            ],
            total: 2,
            page: 1,
            pageSize: 20,
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });

    const { container } = renderCollections();

    expect(screen.getByRole('status', { name: fa.common.loading })).toBeInTheDocument();

    expect(await screen.findByText('آیین‌نامه آموزشی')).toBeInTheDocument();
    expect(screen.getByText('پژوهش و پایان نامه')).toBeInTheDocument();

    // Description paragraph inherits RTL alignment while wrapping text in <bdi>
    const descEl = container.querySelector('.collection-card-desc');
    expect(descEl).not.toHaveAttribute('dir', 'auto');
    expect(descEl?.querySelector('bdi')).toHaveTextContent('English academic regulations guide');

    const filterInput = screen.getByPlaceholderText(fa.collections.filterPlaceholder);
    await user.type(filterInput, 'academic');

    await waitFor(() => {
      expect(screen.getByText('آیین‌نامه آموزشی')).toBeInTheDocument();
      expect(screen.queryByText('پژوهش و پایان نامه')).not.toBeInTheDocument();
    });
  });
});
