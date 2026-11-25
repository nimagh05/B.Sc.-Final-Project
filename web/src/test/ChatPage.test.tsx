import fs from 'node:fs';
import path from 'node:path';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { AuthProvider } from '../auth/AuthContext';
import { tokenStorage } from '../auth/tokenStorage';
import { fa } from '../i18n/fa';
import { ChatPage } from '../pages/ChatPage';

const COL_1 = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const COL_2 = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';

function seedAuth() {
  tokenStorage.setSession({
    accessToken: 'jwt-chat-token',
    expiresAtUtc: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
    roles: ['User'],
  });
}

function renderChat(initialEntry = `/chat?collectionId=${COL_2}`) {
  return render(
    <AuthProvider>
      <MemoryRouter initialEntries={[initialEntry]}>
        <Routes>
          <Route path="/chat" element={<ChatPage />} />
          <Route path="/login" element={<div>Login Page</div>} />
        </Routes>
      </MemoryRouter>
    </AuthProvider>,
  );
}

describe('ChatPage and RTL hygiene', () => {
  it('enforces lang="fa" dir="rtl" in index.html without external CDN links', () => {
    const indexHtml = fs.readFileSync(path.resolve(process.cwd(), 'index.html'), 'utf8');
    expect(indexHtml).toMatch(/<html\s+lang="fa"\s+dir="rtl"/i);
    expect(indexHtml).not.toMatch(/fonts\.googleapis\.com|cdn\.|unpkg\.com|jsdelivr/i);
  });

  it('pre-selects collectionId from query param, highlights clicked citation source, and renders <bdi>/dir="auto"', async () => {
    seedAuth();
    const user = userEvent.setup();
    const scrollIntoViewMock = vi.fn();
    HTMLElement.prototype.scrollIntoView = scrollIntoViewMock;

    let capturedAskBody: { collectionIds?: string[] } | null = null;

    vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
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
                id: COL_1,
                name: 'آیین‌نامه کارشناسی',
                description: null,
                role: 'Viewer',
                documentCount: 1,
                createdAt: '2026-10-01T00:00:00Z',
              },
              {
                id: COL_2,
                name: 'آیین‌نامه ارشد',
                description: null,
                role: 'Viewer',
                documentCount: 2,
                createdAt: '2026-10-01T00:00:00Z',
              },
            ],
            total: 2,
            page: 1,
            pageSize: 100,
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      if (url.includes('/api/v1/conversations')) {
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      if (url.endsWith('/api/v1/ask')) {
        capturedAskBody = JSON.parse(String(init?.body)) as { collectionIds?: string[] };
        return new Response(
          JSON.stringify({
            answer: 'سقف مجاز ۲۰ واحد است [1].',
            notFound: false,
            degraded: false,
            conversationId: 'conv-1',
            sources: [
              {
                index: 1,
                documentId: 'doc-91',
                fileName: 'aein-91.pdf',
                page: 12,
                sectionTitle: 'ماده ۱۲',
                snippet: 'حداکثر واحد مجاز در نیمسال ۲۰ واحد است.',
                score: 0.87,
              },
            ],
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });

    const { container } = renderChat(`/chat?collectionId=${COL_2}`);

    const arshadCheckbox = await screen.findByLabelText('آیین‌نامه ارشد');
    const bsCheckbox = screen.getByLabelText('آیین‌نامه کارشناسی');
    expect(arshadCheckbox).toBeChecked();
    expect(bsCheckbox).not.toBeChecked();

    await user.type(screen.getByLabelText(fa.chat.questionLabel), 'حداکثر واحد مجاز چند واحد است؟');
    await user.click(screen.getByRole('button', { name: fa.chat.sendButton }));

    expect(await screen.findByText(/سقف مجاز ۲۰ واحد است/)).toBeInTheDocument();
    expect(capturedAskBody?.collectionIds).toEqual([COL_2]);

    const fileBdi = container.querySelector('.source-file-title bdi');
    expect(fileBdi).toHaveTextContent('aein-91.pdf');
    expect(container.querySelector('.source-snippet')).toHaveAttribute('dir', 'auto');

    const citationBtn = screen.getByRole('button', { name: fa.chat.citationAriaLabel(1) });
    await user.click(citationBtn);

    expect(citationBtn).toHaveAttribute('aria-pressed', 'true');
    const sourceCard = document.querySelector('.source-card');
    expect(sourceCard).toHaveAttribute('data-active', 'true');
    expect(scrollIntoViewMock).toHaveBeenCalled();
  });

  it('renders DegradedBanner while preserving sources, notFound notice, and 429 Retry-After error', async () => {
    seedAuth();
    const user = userEvent.setup();
    let askAttempt = 0;

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
                id: COL_1,
                name: 'مجموعه تست',
                description: null,
                role: 'Viewer',
                documentCount: 1,
                createdAt: '2026-10-01T00:00:00Z',
              },
            ],
            total: 1,
            page: 1,
            pageSize: 100,
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      if (url.includes('/api/v1/conversations')) {
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      if (url.endsWith('/api/v1/ask')) {
        askAttempt += 1;
        if (askAttempt === 1) {
          return new Response(
            JSON.stringify({
              answer: null,
              notFound: false,
              degraded: true,
              conversationId: 'conv-deg',
              sources: [
                {
                  index: 1,
                  documentId: 'doc-1',
                  fileName: 'rules.pdf',
                  page: 4,
                  sectionTitle: null,
                  snippet: 'متن بازیابی‌شده در حالت بازیابی مستقیم.',
                  score: 0.72,
                },
              ],
            }),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          );
        }
        if (askAttempt === 2) {
          return new Response(
            JSON.stringify({
              answer: '«در اسناد موجود پاسخی برای این پرسش پیدا نشد.»',
              notFound: true,
              degraded: false,
              conversationId: 'conv-deg',
              sources: [],
            }),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          );
        }
        return new Response(
          JSON.stringify({
            type: 'https://docsearch.local/errors/rate.limited',
            title: 'Too Many Requests',
            status: 429,
            detail: 'Rate limit exceeded.',
            code: 'rate.limited',
            traceId: 'trace-429',
          }),
          {
            status: 429,
            headers: {
              'Content-Type': 'application/problem+json',
              'Retry-After': '45',
            },
          },
        );
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });

    renderChat('/chat');
    await screen.findByLabelText('مجموعه تست');

    const textbox = screen.getByLabelText(fa.chat.questionLabel);
    const sendBtn = screen.getByRole('button', { name: fa.chat.sendButton });

    // 1) Degraded mode
    await user.type(textbox, 'پرسش اول در زمان قطعی مدل');
    await user.click(sendBtn);
    expect(await screen.findByText(fa.chat.degradedTitle)).toBeInTheDocument();
    expect(screen.getByText('rules.pdf')).toBeInTheDocument();

    // 2) Not found mode
    await user.type(textbox, 'پرسش خارج از اسناد');
    await user.click(sendBtn);
    expect(await screen.findByText(fa.chat.notFoundTitle)).toBeInTheDocument();
    expect(screen.getByText('«در اسناد موجود پاسخی برای این پرسش پیدا نشد.»')).toBeInTheDocument();

    // 3) 429 Retry-After
    await user.type(textbox, 'پرسش سوم با محدودیت نرخ');
    await user.click(sendBtn);
    await waitFor(() => {
      expect(screen.getByText(fa.errors.rateLimitedWithSeconds(45))).toBeInTheDocument();
    });
  });
});
