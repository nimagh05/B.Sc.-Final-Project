import { apiDownloadBlob, apiRequest, apiUploadFile } from './client';
import type {
  AskRequestDto,
  AskResponse,
  CollectionDto,
  CollectionMemberDto,
  ConversationMessageDto,
  ConversationSummaryDto,
  CreateCollectionRequest,
  CurrentUserResponse,
  DocumentDetailResponse,
  DocumentResponse,
  DocumentStatus,
  LoginRequest,
  LoginResponse,
  PagedCollectionsResponse,
  PagedResult,
  RegisterRequest,
  RegisterResponse,
  UpdateCollectionRequest,
  UpsertMemberRequest,
} from './types';

export const authApi = {
  login(payload: LoginRequest, signal?: AbortSignal): Promise<LoginResponse> {
    return apiRequest<LoginResponse>('/api/v1/auth/login', {
      method: 'POST',
      body: payload,
      signal,
      attachAuth: false,
    });
  },

  register(payload: RegisterRequest, signal?: AbortSignal): Promise<RegisterResponse> {
    return apiRequest<RegisterResponse>('/api/v1/auth/register', {
      method: 'POST',
      body: payload,
      signal,
      attachAuth: false,
    });
  },

  me(signal?: AbortSignal): Promise<CurrentUserResponse> {
    return apiRequest<CurrentUserResponse>('/api/v1/auth/me', {
      method: 'GET',
      signal,
      attachAuth: true,
    });
  },
};

export const collectionsApi = {
  list(page = 1, pageSize = 20, signal?: AbortSignal): Promise<PagedCollectionsResponse> {
    const params = new URLSearchParams({
      page: String(page),
      pageSize: String(pageSize),
    });
    return apiRequest<PagedCollectionsResponse>(`/api/v1/collections?${params.toString()}`, {
      signal,
    });
  },

  get(id: string, signal?: AbortSignal): Promise<CollectionDto> {
    return apiRequest<CollectionDto>(`/api/v1/collections/${encodeURIComponent(id)}`, { signal });
  },

  create(payload: CreateCollectionRequest, signal?: AbortSignal): Promise<CollectionDto> {
    return apiRequest<CollectionDto>('/api/v1/collections', {
      method: 'POST',
      body: payload,
      signal,
    });
  },

  update(
    id: string,
    payload: UpdateCollectionRequest,
    signal?: AbortSignal,
  ): Promise<CollectionDto> {
    return apiRequest<CollectionDto>(`/api/v1/collections/${encodeURIComponent(id)}`, {
      method: 'PUT',
      body: payload,
      signal,
    });
  },

  delete(id: string, signal?: AbortSignal): Promise<void> {
    return apiRequest<void>(`/api/v1/collections/${encodeURIComponent(id)}`, {
      method: 'DELETE',
      signal,
    });
  },

  listMembers(collectionId: string, signal?: AbortSignal): Promise<CollectionMemberDto[]> {
    return apiRequest<CollectionMemberDto[]>(
      `/api/v1/collections/${encodeURIComponent(collectionId)}/members`,
      { signal },
    );
  },

  upsertMember(
    collectionId: string,
    payload: UpsertMemberRequest,
    signal?: AbortSignal,
  ): Promise<CollectionMemberDto> {
    return apiRequest<CollectionMemberDto>(
      `/api/v1/collections/${encodeURIComponent(collectionId)}/members`,
      {
        method: 'PUT',
        body: payload,
        signal,
      },
    );
  },

  removeMember(collectionId: string, userId: string, signal?: AbortSignal): Promise<void> {
    return apiRequest<void>(
      `/api/v1/collections/${encodeURIComponent(collectionId)}/members/${encodeURIComponent(userId)}`,
      {
        method: 'DELETE',
        signal,
      },
    );
  },
};

export const documentsApi = {
  list(
    collectionId: string,
    options: { page?: number; pageSize?: number; status?: DocumentStatus | ''; signal?: AbortSignal } = {},
  ): Promise<PagedResult<DocumentResponse>> {
    const params = new URLSearchParams({
      page: String(options.page ?? 1),
      pageSize: String(options.pageSize ?? 20),
    });
    if (options.status) {
      params.set('status', options.status);
    }
    return apiRequest<PagedResult<DocumentResponse>>(
      `/api/v1/collections/${encodeURIComponent(collectionId)}/documents?${params.toString()}`,
      { signal: options.signal },
    );
  },

  get(id: string, signal?: AbortSignal): Promise<DocumentDetailResponse> {
    return apiRequest<DocumentDetailResponse>(`/api/v1/documents/${encodeURIComponent(id)}`, {
      signal,
    });
  },

  upload(
    collectionId: string,
    file: File,
    options: { onProgress?: (percent: number) => void; signal?: AbortSignal } = {},
  ): Promise<DocumentResponse> {
    return apiUploadFile<DocumentResponse>(
      `/api/v1/collections/${encodeURIComponent(collectionId)}/documents`,
      file,
      options,
    );
  },

  downloadFile(id: string, signal?: AbortSignal): Promise<Blob> {
    return apiDownloadBlob(`/api/v1/documents/${encodeURIComponent(id)}/file`, signal);
  },

  delete(id: string, signal?: AbortSignal): Promise<void> {
    return apiRequest<void>(`/api/v1/documents/${encodeURIComponent(id)}`, {
      method: 'DELETE',
      signal,
    });
  },

  reprocess(id: string, signal?: AbortSignal): Promise<DocumentResponse> {
    return apiRequest<DocumentResponse>(`/api/v1/documents/${encodeURIComponent(id)}/reprocess`, {
      method: 'POST',
      signal,
    });
  },
};

export const askApi = {
  ask(payload: AskRequestDto, signal?: AbortSignal): Promise<AskResponse> {
    return apiRequest<AskResponse>('/api/v1/ask', {
      method: 'POST',
      body: payload,
      signal,
    });
  },
};

export const conversationsApi = {
  list(page = 1, pageSize = 20, signal?: AbortSignal): Promise<ConversationSummaryDto[]> {
    const params = new URLSearchParams({
      page: String(page),
      pageSize: String(pageSize),
    });
    return apiRequest<ConversationSummaryDto[]>(`/api/v1/conversations?${params.toString()}`, {
      signal,
    });
  },

  getMessages(conversationId: string, signal?: AbortSignal): Promise<ConversationMessageDto[]> {
    return apiRequest<ConversationMessageDto[]>(
      `/api/v1/conversations/${encodeURIComponent(conversationId)}/messages`,
      { signal },
    );
  },

  delete(conversationId: string, signal?: AbortSignal): Promise<void> {
    return apiRequest<void>(`/api/v1/conversations/${encodeURIComponent(conversationId)}`, {
      method: 'DELETE',
      signal,
    });
  },
};

export function triggerPdfAction(blob: Blob, fileName: string, mode: 'open' | 'download'): void {
  const url = URL.createObjectURL(blob);

  if (mode === 'open') {
    const opened = window.open(url, '_blank', 'noopener,noreferrer');
    if (!opened) {
      const link = document.createElement('a');
      link.href = url;
      link.download = fileName;
      document.body.appendChild(link);
      link.click();
      link.remove();
    }
  } else {
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
  }

  // Delay revoking so browsers finish loading/downloading the PDF Blob URL.
  window.setTimeout(() => {
    URL.revokeObjectURL(url);
  }, 60_000);
}
