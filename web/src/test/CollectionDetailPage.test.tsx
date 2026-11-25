import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AuthProvider } from '../auth/AuthContext';
import { tokenStorage } from '../auth/tokenStorage';
import { fa } from '../i18n/fa';
import { CollectionDetailPage } from '../pages/CollectionDetailPage';

const COLLECTION_ID = '11111111-1111-1111-1111-111111111111';
const DOC_NEW_ID = '22222222-2222-2222-2222-222222222222';
const DOC_FAILED_ID = '33333333-3333-3333-3333-333333333333';

function seedAuthSession() {
  tokenStorage.setSession({
    accessToken: 'jwt-valid-token',
    expiresAtUtc: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
    roles: ['User'],
  });
}

function renderDetailPage() {
  return render(
    <AuthProvider>
      <MemoryRouter initialEntries={[`/collections/${COLLECTION_ID}`]}>
        <Routes>
          <Route path="/collections/:id" element={<CollectionDetailPage />} />
          <Route path="/login" element={<div>Login Page</div>} />
        </Routes>
      </MemoryRouter>
    </AuthProvider>,
  );
}

describe('CollectionDetailPage', () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('uploads PDF via field "file" with progress, polls status to Ready, and shows details', async () => {
    seedAuthSession();
    let docGetCalls = 0;

    vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => {
      const url = String(input);
      if (url.endsWith('/api/v1/auth/me')) {
        return new Response(
          JSON.stringify({ id: 'owner-1', email: 'owner@uni.ac.ir', roles: ['User'] }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      if (url.endsWith(`/api/v1/collections/${COLLECTION_ID}`)) {
        return new Response(
          JSON.stringify({
            id: COLLECTION_ID,
            name: 'آیین‌نامه‌ها',
            description: 'مجموعه آزمایشی',
            role: 'Owner',
            documentCount: 1,
            createdAt: '2026-10-01T10:00:00Z',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      if (url.includes(`/api/v1/collections/${COLLECTION_ID}/documents`)) {
        return new Response(
          JSON.stringify({
            items: [
              {
                id: DOC_FAILED_ID,
                collectionId: COLLECTION_ID,
                fileName: 'broken-scan.pdf',
                sizeBytes: 2048,
                status: 'Failed',
                pageCount: 2,
                createdAt: '2026-10-01T09:00:00Z',
              },
            ],
            total: 1,
            page: 1,
            pageSize: 20,
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      if (url.endsWith(`/api/v1/collections/${COLLECTION_ID}/members`)) {
        return new Response(
          JSON.stringify([{ userId: 'owner-1', email: 'owner@uni.ac.ir', role: 'Owner' }]),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      if (url.endsWith(`/api/v1/documents/${DOC_FAILED_ID}`)) {
        return new Response(
          JSON.stringify({
            id: DOC_FAILED_ID,
            collectionId: COLLECTION_ID,
            fileName: 'broken-scan.pdf',
            sizeBytes: 2048,
            status: 'Failed',
            errorMessage: 'No extractable text found in PDF.',
            pageCount: 2,
            ocrPages: [1, 2],
            chunkCount: 0,
            createdAt: '2026-10-01T09:00:00Z',
            processedAt: '2026-10-01T09:01:00Z',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      if (url.endsWith(`/api/v1/documents/${DOC_NEW_ID}`)) {
        docGetCalls += 1;
        const status = docGetCalls === 1 ? 'Processing' : 'Ready';
        return new Response(
          JSON.stringify({
            id: DOC_NEW_ID,
            collectionId: COLLECTION_ID,
            fileName: 'aein-91.pdf',
            sizeBytes: 4096,
            status,
            errorMessage: null,
            pageCount: status === 'Ready' ? 14 : null,
            ocrPages: status === 'Ready' ? [3] : null,
            chunkCount: status === 'Ready' ? 18 : 0,
            createdAt: '2026-10-02T10:00:00Z',
            processedAt: status === 'Ready' ? '2026-10-02T10:00:05Z' : null,
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });

    // Mock XMLHttpRequest to verify multipart field 'file' and progress events
    let capturedFormData: FormData | null = null;
    class MockXHR {
      upload = { onprogress: null as ((ev: ProgressEvent) => void) | null };
      onload: (() => void) | null = null;
      onerror: (() => void) | null = null;
      ontimeout: (() => void) | null = null;
      status = 202;
      responseText = JSON.stringify({
        id: DOC_NEW_ID,
        collectionId: COLLECTION_ID,
        fileName: 'aein-91.pdf',
        sizeBytes: 4096,
        status: 'Uploaded',
        pageCount: null,
        createdAt: '2026-10-02T10:00:00Z',
      });
      open = vi.fn();
      setRequestHeader = vi.fn();
      getResponseHeader = vi.fn(() => null);
      abort = vi.fn();
      send = vi.fn((body: FormData) => {
        capturedFormData = body;
        this.upload.onprogress?.({
          lengthComputable: true,
          loaded: 50,
          total: 100,
        } as ProgressEvent);
        setTimeout(() => {
          this.onload?.();
        }, 10);
      });
    }
    vi.stubGlobal('XMLHttpRequest', MockXHR);

    renderDetailPage();

    expect(await screen.findByText('broken-scan.pdf')).toBeInTheDocument();
    expect(await screen.findByText('No extractable text found in PDF.')).toBeInTheDocument();

    const fileInput = document.querySelector('input[type="file"]') as HTMLInputElement;
    const pdfFile = new File(['%PDF-1.4 content'], 'aein-91.pdf', { type: 'application/pdf' });
    fireEvent.change(fileInput, { target: { files: [pdfFile] } });

    expect(await screen.findByRole('progressbar')).toHaveAttribute('aria-valuenow', '50');
    expect(capturedFormData?.get('file')).toBe(pdfFile);

    expect(await screen.findByText('aein-91.pdf')).toBeInTheDocument();
    const table = screen.getByRole('table');
    expect(within(table).getByText(fa.documentStatuses.Uploaded)).toBeInTheDocument();

    // Poll 1 -> Processing, Poll 2 -> Ready
    await waitFor(
      () => {
        expect(within(table).getByText(fa.documentStatuses.Processing)).toBeInTheDocument();
      },
      { timeout: 3000 },
    );

    await waitFor(
      () => {
        expect(within(table).getByText(fa.documentStatuses.Ready)).toBeInTheDocument();
      },
      { timeout: 3000 },
    );

    // Inspect merged details for Ready row (chunkCount + ocrPages)
    const detailButtons = screen.getAllByRole('button', { name: fa.common.details });
    fireEvent.click(detailButtons[0]);
    expect(await screen.findByText('18')).toBeInTheDocument();
    expect(screen.getByText('1 (3)')).toBeInTheDocument();
  });

  it('hides upload, delete, reprocess, and member panel for Viewer role and downloads PDF with Bearer header', async () => {
    seedAuthSession();
    const user = userEvent.setup();
    const authHeadersSent: string[] = [];

    vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
      const url = String(input);
      const headers = init?.headers as Record<string, string> | undefined;
      if (headers?.Authorization) {
        authHeadersSent.push(headers.Authorization);
      }
      if (url.endsWith('/api/v1/auth/me')) {
        return new Response(
          JSON.stringify({ id: 'viewer-1', email: 'viewer@uni.ac.ir', roles: ['User'] }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      if (url.endsWith(`/api/v1/collections/${COLLECTION_ID}`)) {
        return new Response(
          JSON.stringify({
            id: COLLECTION_ID,
            name: 'مجموعه خواندنی',
            description: null,
            role: 'Viewer',
            documentCount: 1,
            createdAt: '2026-10-01T10:00:00Z',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      if (url.includes(`/api/v1/collections/${COLLECTION_ID}/documents`)) {
        return new Response(
          JSON.stringify({
            items: [
              {
                id: DOC_NEW_ID,
                collectionId: COLLECTION_ID,
                fileName: 'reg-1403.pdf',
                sizeBytes: 1024,
                status: 'Ready',
                pageCount: 5,
                createdAt: '2026-10-01T10:00:00Z',
              },
            ],
            total: 1,
            page: 1,
            pageSize: 20,
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      if (url.endsWith(`/api/v1/documents/${DOC_NEW_ID}/file`)) {
        return new Response(new Blob(['%PDF-1.4'], { type: 'application/pdf' }), { status: 200 });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });

    const createObjectUrlSpy = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:pdf-1');
    const revokeSpy = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
    const anchorClickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    renderDetailPage();

    expect(await screen.findByText('reg-1403.pdf')).toBeInTheDocument();
    expect(screen.queryByText(fa.collectionDetail.uploadSectionTitle)).not.toBeInTheDocument();
    expect(screen.queryByText(fa.collectionDetail.membersSectionTitle)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: fa.collectionDetail.deleteDocument })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: fa.collectionDetail.reprocess })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: fa.collectionDetail.downloadPdf }));

    await waitFor(() => {
      expect(createObjectUrlSpy).toHaveBeenCalledTimes(1);
    });
    expect(anchorClickSpy).toHaveBeenCalledTimes(1);
    expect(authHeadersSent).toContain('Bearer jwt-valid-token');
    // Must not revoke synchronously before browser completes download
    expect(revokeSpy).not.toHaveBeenCalled();
  });
});
