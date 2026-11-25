import fs from 'node:fs';
import path from 'node:path';
import { act, fireEvent, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fa } from '../i18n/fa';
import { DocSearchWidgetElement } from '../widget/docsearch-widget';

const COL_1 = '0196f8c0-0000-7000-8000-000000000001';
const COL_2 = '0196f8c0-0000-7000-8000-000000000002';

type WidgetEl = DocSearchWidgetElement;

async function mountWidget(attrs: Record<string, string> = {}): Promise<WidgetEl> {
  const el = document.createElement('docsearch-widget') as WidgetEl;
  for (const [key, value] of Object.entries(attrs)) {
    el.setAttribute(key, value);
  }
  await act(async () => {
    document.body.appendChild(el);
  });
  return el;
}

function getShadowQueries(el: WidgetEl) {
  const shadow = el.shadowRoot;
  if (!shadow) {
    throw new Error('Expected open shadowRoot on <docsearch-widget>');
  }
  const container = shadow.querySelector('.ds-widget-root') as HTMLElement | null;
  if (!container) {
    throw new Error('Expected .ds-widget-root inside shadowRoot');
  }
  return {
    shadow,
    container,
    queries: within(container),
  };
}

afterEach(() => {
  document.body.innerHTML = '';
});

describe('<docsearch-widget> Custom Element', () => {
  it('mounts inside open Shadow DOM with dir="rtl", lang="fa", :host tokens, widget-title, and zero CDN/storage usage', async () => {
    const el = await mountWidget({
      'api-url': 'http://api.example.test/',
      token: 'jwt-init',
      'collection-ids': COL_1,
      'widget-title': 'پرسش از آیین‌نامه‌ها',
      placeholder: 'پرسش آزمایشی را بنویسید',
    });

    const { shadow, container, queries } = getShadowQueries(el);
    expect(shadow.mode).toBe('open');
    expect(container).toHaveAttribute('dir', 'rtl');
    expect(container).toHaveAttribute('lang', 'fa');
    expect(el.hasAttribute('title')).toBe(false);

    const styleEl = shadow.querySelector('style[data-docsearch-widget-style]');
    expect(styleEl).not.toBeNull();
    expect(styleEl?.textContent).toContain(':host');
    expect(styleEl?.textContent).toContain('all: initial');
    expect(styleEl?.textContent).toContain('--color-primary: #1e3a5f');
    expect(styleEl?.textContent).toContain('.ds-widget-root');
    expect(styleEl?.textContent).toContain('font-family: var(--font-sans)');

    expect(queries.getByRole('heading', { name: 'پرسش از آیین‌نامه‌ها' })).toBeInTheDocument();
    expect(queries.getByPlaceholderText('پرسش آزمایشی را بنویسید')).toBeInTheDocument();

    // Verify widget source and demo contain zero CDN URLs, dangerouslySetInnerHTML, or localStorage/sessionStorage
    const widgetDir = path.resolve(process.cwd(), 'src/widget');
    for (const fileName of fs.readdirSync(widgetDir)) {
      const content = fs.readFileSync(path.join(widgetDir, fileName), 'utf8');
      expect(content).not.toMatch(/dangerouslySetInnerHTML|localStorage|sessionStorage/i);
      expect(content).not.toMatch(/fonts\.googleapis\.com|cdn\.|unpkg\.com|jsdelivr/i);
    }

    const demoHtml = fs.readFileSync(path.resolve(process.cwd(), 'widget-demo.html'), 'utf8');
    expect(demoHtml).not.toMatch(/fonts\.googleapis\.com|cdn\.|unpkg\.com|jsdelivr/i);
    expect(demoHtml).not.toMatch(/localStorage|sessionStorage/i);
  });

  it('supports pre-upgrade properties and safe disconnect/reconnect without DOMException', async () => {
    const tagName = 'docsearch-widget-preupgrade';
    const preEl = document.createElement(tagName) as unknown as Record<string, unknown> & HTMLElement;
    preEl.token = 'pre-upgrade-jwt';
    preEl.apiUrl = 'https://pre.example.test/';
    preEl.collectionIds = `${COL_1}, ${COL_2}`;
    preEl.widgetTitle = 'عنوان پیش‌ارتقا';
    preEl.placeholder = 'متن پیش‌ارتقا';

    class PreUpgradeWidget extends DocSearchWidgetElement {}
    if (!customElements.get(tagName)) {
      customElements.define(tagName, PreUpgradeWidget);
    }

    await act(async () => {
      document.body.appendChild(preEl);
    });

    const upgraded = preEl as unknown as DocSearchWidgetElement;
    expect(upgraded.token).toBe('pre-upgrade-jwt');
    expect(upgraded.hasAttribute('token')).toBe(false);

    const { queries } = getShadowQueries(upgraded);
    expect(queries.getByRole('heading', { name: 'عنوان پیش‌ارتقا' })).toBeInTheDocument();
    expect(queries.getByPlaceholderText('متن پیش‌ارتقا')).toBeInTheDocument();

    // Disconnect and reconnect safely without throwing NotSupportedError from attachShadow
    await act(async () => {
      upgraded.remove();
    });
    expect(() => {
      document.body.appendChild(upgraded);
    }).not.toThrow();

    await waitFor(() => {
      const reattached = getShadowQueries(upgraded);
      expect(reattached.queries.getByRole('heading', { name: 'عنوان پیش‌ارتقا' })).toBeInTheDocument();
    });
  });

  it('calls POST /api/v1/ask with Bearer token from el.token, highlights SourceCard inside shadowRoot, opens PDF, and copies answer', async () => {
    const user = userEvent.setup();
    const scrollIntoViewMock = vi.fn();
    HTMLElement.prototype.scrollIntoView = scrollIntoViewMock;

    const writeTextMock = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText: writeTextMock },
    });

    const openSpy = vi.spyOn(window, 'open').mockReturnValue(null);

    let capturedAskUrl = '';
    let capturedAskAuth = '';
    let capturedAskBody: { question?: string; collectionIds?: string[]; conversationId?: string | null } | null = null;
    let capturedPdfUrl = '';
    let capturedPdfAuth = '';

    vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
      const url = String(input);
      const headers = init?.headers as Record<string, string> | undefined;
      if (url.endsWith('/api/v1/ask')) {
        capturedAskUrl = url;
        capturedAskAuth = headers?.Authorization ?? '';
        capturedAskBody = JSON.parse(String(init?.body)) as {
          question?: string;
          collectionIds?: string[];
          conversationId?: string | null;
        };
        return new Response(
          JSON.stringify({
            answer: 'پاسخ مستند:\n* **سقف واحد:** ۲۰ واحد است [1].\n* کد نمونه `CS101` و <script>alert(1)</script>',
            notFound: false,
            degraded: false,
            conversationId: 'conv-widget-1',
            sources: [
              {
                index: 1,
                documentId: 'doc-91',
                fileName: 'aein-91.pdf',
                page: 12,
                sectionTitle: 'ماده ۱۲',
                snippet: 'حداکثر واحد مجاز در نیمسال ۲۰ واحد است.',
                score: 0.91,
              },
            ],
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (url.endsWith('/api/v1/documents/doc-91/file')) {
        capturedPdfUrl = url;
        capturedPdfAuth = headers?.Authorization ?? '';
        return new Response(new Blob(['%PDF-1.4 mock'], { type: 'application/pdf' }), {
          status: 200,
          headers: { 'Content-Type': 'application/pdf' },
        });
      }

      throw new Error(`Unexpected fetch: ${url}`);
    });

    const el = await mountWidget({
      'api-url': 'https://docs.university.ac.ir///',
      'collection-ids': ` ${COL_1} , ${COL_2} , ${COL_1} `,
    });

    await act(async () => {
      el.token = 'dynamic-jwt-secret';
    });

    // Dynamic el.token must NOT be serialized into DOM attribute
    expect(el.hasAttribute('token')).toBe(false);
    expect(el.outerHTML).not.toContain('dynamic-jwt-secret');

    const { shadow, container, queries } = getShadowQueries(el);
    const textbox = queries.getByLabelText(fa.chat.questionLabel);

    // Client-side validation for < 3 chars
    await user.type(textbox, 'اب');
    await user.click(queries.getByRole('button', { name: fa.chat.sendButton }));
    expect(queries.getByText(fa.chat.validation.questionLength)).toBeInTheDocument();
    expect(globalThis.fetch).not.toHaveBeenCalled();

    // Replace input value cleanly inside Shadow DOM (userEvent.clear checks document.activeElement)
    fireEvent.change(textbox, { target: { value: 'حداکثر واحد مجاز چند واحد است؟' } });
    await user.click(queries.getByRole('button', { name: fa.chat.sendButton }));

    await waitFor(() => {
      expect(queries.getByText(/پاسخ مستند:/)).toBeInTheDocument();
      expect(queries.getByText('CS101')).toBeInTheDocument();
    });

    expect(capturedAskUrl).toBe('https://docs.university.ac.ir/api/v1/ask');
    expect(capturedAskAuth).toBe('Bearer dynamic-jwt-secret');
    expect(capturedAskBody).toEqual({
      question: 'حداکثر واحد مجاز چند واحد است؟',
      collectionIds: [COL_1, COL_2],
      conversationId: null,
    });

    // XSS payload must be escaped as text, never injected as DOM tags
    expect(shadow.querySelector('script')).toBeNull();
    expect(queries.getByText(/<script>alert\(1\)<\/script>/)).toBeInTheDocument();

    // Bidi tags for filename & score
    const fileBdi = container.querySelector('.source-file-title bdi');
    expect(fileBdi).toHaveTextContent('aein-91.pdf');
    const scoreBdi = container.querySelector('.source-meta-row bdi[dir="ltr"]');
    expect(scoreBdi).toHaveTextContent('0.91');

    // Click citation chip [1] -> resolves SourceCard inside shadowRoot (not document)
    const getByIdSpy = vi.spyOn(shadow, 'getElementById');
    const citeBtn = queries.getByRole('button', { name: fa.chat.citationAriaLabel(1) });
    await user.click(citeBtn);

    expect(getByIdSpy).toHaveBeenCalled();
    expect(citeBtn).toHaveAttribute('aria-pressed', 'true');
    const sourceCard = container.querySelector('.source-card');
    expect(sourceCard).toHaveAttribute('data-active', 'true');
    expect(scrollIntoViewMock).toHaveBeenCalledWith({ block: 'nearest', behavior: 'smooth' });

    // Copy answer
    await user.click(queries.getByRole('button', { name: fa.chat.copyAnswer }));
    expect(writeTextMock).toHaveBeenCalled();
    expect(queries.getByRole('button', { name: fa.chat.copiedAnswer })).toBeInTheDocument();

    // Authenticated PDF open (popup blocked -> fallback download)
    await user.click(queries.getByRole('button', { name: fa.chat.sourceCard.openPdf }));
    await waitFor(() => {
      expect(capturedPdfUrl).toBe('https://docs.university.ac.ir/api/v1/documents/doc-91/file');
      expect(capturedPdfAuth).toBe('Bearer dynamic-jwt-secret');
      expect(openSpy).toHaveBeenCalled();
    });

    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.length).toBe(0);
  });

  it('preserves conversationId on token rotation, but aborts in-flight ask and resets conversationId on scope change', async () => {
    const user = userEvent.setup();
    const capturedCalls: Array<{ auth: string; body: { conversationId?: string | null; collectionIds?: string[] } }> = [];
    let pendingSignal: AbortSignal | undefined;

    vi.spyOn(globalThis, 'fetch').mockImplementation((_input, init) => {
      const headers = init?.headers as Record<string, string> | undefined;
      const body = JSON.parse(String(init?.body)) as { conversationId?: string | null; collectionIds?: string[] };
      capturedCalls.push({ auth: headers?.Authorization ?? '', body });

      if (capturedCalls.length === 1) {
        return Promise.resolve(
          new Response(
            JSON.stringify({
              answer: 'پاسخ نخست [1]',
              notFound: false,
              degraded: false,
              conversationId: 'conv-preserve-1',
              sources: [
                {
                  index: 1,
                  documentId: 'doc-1',
                  fileName: 'rule.pdf',
                  page: 2,
                  sectionTitle: null,
                  snippet: 'متن نمونه.',
                  score: 0.85,
                },
              ],
            }),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          ),
        );
      }

      if (capturedCalls.length === 2) {
        return Promise.resolve(
          new Response(
            JSON.stringify({
              answer: 'پاسخ دوم در همان گفتگو',
              notFound: false,
              degraded: false,
              conversationId: 'conv-preserve-1',
              sources: [],
            }),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          ),
        );
      }

      if (capturedCalls.length === 3) {
        pendingSignal = init?.signal ?? undefined;
        return new Promise<Response>((_resolve, reject) => {
          init?.signal?.addEventListener('abort', () => {
            reject(new DOMException('Aborted', 'AbortError'));
          });
        });
      }

      return Promise.resolve(
        new Response(
          JSON.stringify({
            answer: 'پاسخ در مجموعه جدید',
            notFound: false,
            degraded: false,
            conversationId: 'conv-new-2',
            sources: [],
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        ),
      );
    });

    const el = await mountWidget({
      'api-url': 'http://localhost:5000',
      'collection-ids': COL_1,
    });
    await act(async () => {
      el.token = 'token-v1';
    });

    const { queries } = getShadowQueries(el);
    const textbox = queries.getByLabelText(fa.chat.questionLabel);

    await user.type(textbox, 'پرسش اول');
    await user.click(queries.getByRole('button', { name: fa.chat.sendButton }));
    expect(await queries.findByText(/پاسخ نخست/)).toBeInTheDocument();

    // Rotate token via JS property: must preserve messages and conversationId
    await act(async () => {
      el.token = 'token-v2';
    });
    expect(queries.getByText(/پاسخ نخست/)).toBeInTheDocument();

    await user.type(textbox, 'پرسش دوم');
    await user.click(queries.getByRole('button', { name: fa.chat.sendButton }));
    expect(await queries.findByText(/پاسخ دوم در همان گفتگو/)).toBeInTheDocument();
    expect(capturedCalls[1].auth).toBe('Bearer token-v2');
    expect(capturedCalls[1].body.conversationId).toBe('conv-preserve-1');

    // Start a 3rd request, then change collection-ids while in flight -> must abort + reset conversationId
    await user.type(textbox, 'پرسش در حال اجرا');
    await user.click(queries.getByRole('button', { name: fa.chat.sendButton }));
    await waitFor(() => expect(capturedCalls).toHaveLength(3));
    expect(pendingSignal?.aborted).toBe(false);

    await act(async () => {
      el.setAttribute('collection-ids', COL_2);
    });

    await waitFor(() => {
      expect(pendingSignal?.aborted).toBe(true);
    });
    expect(queries.queryByText(/پاسخ نخست/)).not.toBeInTheDocument();

    // Next question in new scope must have conversationId: null and new collectionIds
    await user.type(textbox, 'پرسش پس از تغییر مجموعه');
    await user.click(queries.getByRole('button', { name: fa.chat.sendButton }));
    expect(await queries.findByText(/پاسخ در مجموعه جدید/)).toBeInTheDocument();
    expect(capturedCalls[3].body.conversationId).toBeNull();
    expect(capturedCalls[3].body.collectionIds).toEqual([COL_2]);
  });

  it('renders degraded, notFound, reset conversation, and error states (missing config, 401, 403, 404, 429 without Retry-After, network error)', async () => {
    const user = userEvent.setup();
    let mode: 'degraded' | 'notFound' | '401' | '403' | '404' | '429-cors' | 'network' = 'degraded';

    vi.spyOn(globalThis, 'fetch').mockImplementation(async () => {
      if (mode === 'degraded') {
        return new Response(
          JSON.stringify({
            answer: null,
            notFound: false,
            degraded: true,
            conversationId: 'c-deg',
            sources: [
              {
                index: 1,
                documentId: 'doc-d',
                fileName: 'fallback.pdf',
                page: 5,
                sectionTitle: 'ماده ۵',
                snippet: 'قطعه بازیابی‌شده در حالت افت سرویس.',
                score: 0.78,
              },
            ],
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (mode === 'notFound') {
        return new Response(
          JSON.stringify({
            answer: '«در اسناد موجود پاسخی برای این پرسش پیدا نشد.»',
            notFound: true,
            degraded: false,
            conversationId: 'c-nf',
            sources: [],
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (mode === '401') {
        return new Response(
          JSON.stringify({ status: 401, code: 'auth.unauthorized', detail: 'Unauthorized' }),
          { status: 401, headers: { 'Content-Type': 'application/problem+json' } },
        );
      }

      if (mode === '403') {
        return new Response(
          JSON.stringify({ status: 403, code: 'auth.forbidden', detail: 'Forbidden' }),
          { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
        );
      }

      if (mode === '404') {
        return new Response(
          JSON.stringify({
            status: 404,
            code: 'resource.not_found',
            detail: 'None of the requested collections were found or visible.',
          }),
          { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
        );
      }

      if (mode === '429-cors') {
        // Simulates cross-origin 429 where Retry-After header is blocked from JS access
        return new Response(
          JSON.stringify({ status: 429, code: 'rate.limited', detail: 'Rate limit exceeded.' }),
          { status: 429, headers: { 'Content-Type': 'application/problem+json' } },
        );
      }

      throw new TypeError('Failed to fetch');
    });

    // Missing config check (no token, no collection-ids)
    const el = await mountWidget({ 'api-url': 'http://localhost:5000' });
    const { queries } = getShadowQueries(el);
    expect(queries.getByRole('alert')).toHaveTextContent(fa.widget.missingCollectionIds);

    await act(async () => {
      el.collectionIds = COL_1;
    });
    expect(queries.getByRole('alert')).toHaveTextContent(fa.widget.missingToken);
    expect(globalThis.fetch).not.toHaveBeenCalled();

    // Unsafe font-base-url must be rejected safely without throwing
    await act(async () => {
      el.setAttribute('font-base-url', 'javascript:alert(1)');
      el.token = 'jwt-ok';
    });

    const textbox = queries.getByLabelText(fa.chat.questionLabel);
    const sendBtn = queries.getByRole('button', { name: fa.chat.sendButton });

    // Degraded state
    mode = 'degraded';
    await user.type(textbox, 'پرسش حالت افت سرویس');
    await user.click(sendBtn);
    expect(await queries.findByText(fa.chat.degradedTitle)).toBeInTheDocument();
    expect(queries.getByText('fallback.pdf')).toBeInTheDocument();

    // Not found state
    mode = 'notFound';
    await user.type(textbox, 'پرسش نامرتبط');
    await user.click(sendBtn);
    expect(await queries.findByText(fa.chat.notFoundTitle)).toBeInTheDocument();
    expect(queries.queryByText('«در اسناد موجود پاسخی برای این پرسش پیدا نشد.»')).not.toBeInTheDocument();

    // Reset via "گفتگوی جدید"
    await user.click(queries.getByRole('button', { name: fa.chat.newConversation }));
    expect(queries.queryByText(fa.chat.notFoundTitle)).not.toBeInTheDocument();
    expect(queries.getByText(fa.chat.emptyThreadTitle)).toBeInTheDocument();

    // 401
    mode = '401';
    await user.type(textbox, 'پرسش با خطای ۴۰۱');
    await user.click(sendBtn);
    expect(await queries.findByText(fa.errors.unauthorized)).toBeInTheDocument();

    // 403
    mode = '403';
    await user.click(sendBtn);
    expect(await queries.findByText(fa.errors.forbidden)).toBeInTheDocument();

    // 404
    mode = '404';
    await user.click(sendBtn);
    expect(await queries.findByText(fa.errors.askCollectionsNotFound)).toBeInTheDocument();

    // 429 without Retry-After header (CORS) -> clean Persian message, no NaN
    mode = '429-cors';
    await user.click(sendBtn);
    const rateErr = await queries.findByText(fa.errors.rateLimited);
    expect(rateErr).toBeInTheDocument();
    expect(rateErr.textContent).not.toContain('NaN');

    // Network / CORS failure
    mode = 'network';
    await user.click(sendBtn);
    expect(await queries.findByText(fa.errors.network)).toBeInTheDocument();
  });

  it('unmounts cleanly on disconnect without throwing', async () => {
    const el = await mountWidget({
      'api-url': 'http://localhost:5000',
      'collection-ids': COL_1,
      token: 'jwt-ok',
    });
    const { queries } = getShadowQueries(el);
    fireEvent.input(queries.getByLabelText(fa.chat.questionLabel), { target: { value: 'تست' } });
    expect(() => el.remove()).not.toThrow();
  });
});
