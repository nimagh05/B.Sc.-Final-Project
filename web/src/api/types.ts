export type DocumentStatus = 'Uploaded' | 'Processing' | 'Ready' | 'Failed';

export type CollectionRole = 'Viewer' | 'Editor' | 'Owner';

export interface ProblemDetailsResponse {
  type: string;
  title: string;
  status: number;
  detail: string;
  instance?: string | null;
  code: string;
  traceId: string;
  errors?: Record<string, string[]> | null;
}

export interface RegisterRequest {
  email: string;
  password: string;
  displayName?: string | null;
}

export interface RegisterResponse {
  id: string;
  email: string;
}

export interface LoginRequest {
  email: string;
  password: string;
}

export interface LoginResponse {
  accessToken: string;
  expiresAtUtc: string;
  roles: string[];
}

export interface CurrentUserResponse {
  id: string;
  email: string;
  roles: string[];
}

export interface CollectionDto {
  id: string;
  name: string;
  description: string | null;
  role: CollectionRole;
  documentCount: number;
  createdAt: string;
}

export interface CreateCollectionRequest {
  name: string;
  description?: string | null;
}

export interface UpdateCollectionRequest {
  name: string;
  description?: string | null;
}

export interface PagedCollectionsResponse {
  items: CollectionDto[];
  total: number;
  page: number;
  pageSize: number;
}

export interface CollectionMemberDto {
  userId: string;
  email: string;
  role: CollectionRole;
}

export interface UpsertMemberRequest {
  email: string;
  role: CollectionRole;
}

export interface DocumentResponse {
  id: string;
  collectionId: string;
  fileName: string;
  sizeBytes: number;
  status: DocumentStatus;
  pageCount: number | null;
  createdAt: string;
}

export interface DocumentDetailResponse {
  id: string;
  collectionId: string;
  fileName: string;
  sizeBytes: number;
  status: DocumentStatus;
  errorMessage: string | null;
  pageCount: number | null;
  ocrPages: number[] | null;
  chunkCount: number;
  createdAt: string;
  processedAt: string | null;
}

export interface PagedResult<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

export interface AskRequestDto {
  question: string;
  collectionIds: string[];
  conversationId?: string | null;
  topK?: number | null;
}

export interface SourceCitationDto {
  index: number;
  documentId: string;
  fileName: string;
  page: number;
  sectionTitle: string | null;
  snippet: string;
  score: number;
}

export interface AskResponse {
  answer: string | null;
  notFound: boolean;
  degraded: boolean;
  conversationId: string | null;
  sources: SourceCitationDto[];
}

export interface ConversationSummaryDto {
  id: string;
  title: string | null;
  createdAt: string;
}

export interface ConversationMessageDto {
  id: string;
  conversationId: string;
  role: 'user' | 'assistant';
  content: string;
  degraded: boolean;
  notFound: boolean;
  createdAt: string;
  sources?: SourceCitationDto[] | null;
}
