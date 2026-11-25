import { Fragment, useCallback, useEffect, useId, useRef, useState, type FormEvent } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { ApiError, translateDocumentError } from '../api/errors';
import { collectionsApi, documentsApi, triggerPdfAction } from '../api/endpoints';
import type {
  CollectionDto,
  CollectionMemberDto,
  CollectionRole,
  DocumentDetailResponse,
  DocumentResponse,
  DocumentStatus,
} from '../api/types';
import { ErrorAlert } from '../components/ErrorAlert';
import { FileDrop } from '../components/FileDrop';
import { AlertTriangleIcon, ChatIcon, FileTextIcon, TrashIcon } from '../components/Icons';
import { Layout } from '../components/Layout';
import { RoleBadge, StatusBadge } from '../components/StatusBadge';
import { fa } from '../i18n/fa';

const PAGE_SIZE = 20;
const POLL_INTERVAL_MS = 1500;
const MAX_CONSECUTIVE_POLL_ERRORS = 3;

function formatBytes(sizeBytes: number): string {
  if (sizeBytes >= 1024 * 1024) {
    return fa.collectionDetail.sizeMb((sizeBytes / (1024 * 1024)).toFixed(1));
  }
  const kb = Math.max(1, Math.round(sizeBytes / 1024));
  return fa.collectionDetail.sizeKb(kb);
}

function isNonTerminal(status: DocumentStatus): boolean {
  return status === 'Uploaded' || status === 'Processing';
}

export function CollectionDetailPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();

  const [collection, setCollection] = useState<CollectionDto | null>(null);
  const [documents, setDocuments] = useState<DocumentResponse[]>([]);
  const [totalDocs, setTotalDocs] = useState(0);
  const [page, setPage] = useState(1);
  const [statusFilter, setStatusFilter] = useState<DocumentStatus | ''>('');
  const [detailsById, setDetailsById] = useState<Record<string, DocumentDetailResponse>>({});
  const [expandedDocIds, setExpandedDocIds] = useState<Record<string, boolean>>({});
  const [loadingDetailIds, setLoadingDetailIds] = useState<Record<string, boolean>>({});

  const [isLoading, setIsLoading] = useState(true);
  const [pageError, setPageError] = useState<unknown>(null);
  const [actionError, setActionError] = useState<unknown>(null);
  const [actionNotice, setActionNotice] = useState<string | null>(null);

  // Edit collection
  const [isEditingCollection, setIsEditingCollection] = useState(false);
  const [editName, setEditName] = useState('');
  const [editDescription, setEditDescription] = useState('');
  const [isSavingCollection, setIsSavingCollection] = useState(false);

  // Upload state
  const [isUploading, setIsUploading] = useState(false);
  const [uploadProgress, setUploadProgress] = useState(0);
  const [uploadingFileName, setUploadingFileName] = useState<string | null>(null);

  // Row operations
  const [downloadingDocId, setDownloadingDocId] = useState<string | null>(null);
  const [reprocessingDocId, setReprocessingDocId] = useState<string | null>(null);
  const [deletingDocId, setDeletingDocId] = useState<string | null>(null);
  const [isDeletingCollection, setIsDeletingCollection] = useState(false);

  // Polling state
  const [pollingStopped, setPollingStopped] = useState(false);
  const [pollTrigger, setPollTrigger] = useState(0);
  const pollFailCountRef = useRef(0);

  // Members state (Owner only)
  const [members, setMembers] = useState<CollectionMemberDto[]>([]);
  const [memberEmail, setMemberEmail] = useState('');
  const [memberRole, setMemberRole] = useState<CollectionRole>('Viewer');
  const [isSavingMember, setIsSavingMember] = useState(false);
  const [removingMemberId, setRemovingMemberId] = useState<string | null>(null);

  const statusSelectId = useId();
  const editNameId = useId();
  const editDescId = useId();
  const memberEmailId = useId();
  const memberRoleId = useId();

  const loadAll = useCallback(
    async (targetPage: number, targetStatus: DocumentStatus | '', signal?: AbortSignal) => {
      if (!id) return;
      setIsLoading(true);
      setPageError(null);
      try {
        const [col, docPage] = await Promise.all([
          collectionsApi.get(id, signal),
          documentsApi.list(id, { page: targetPage, pageSize: PAGE_SIZE, status: targetStatus, signal }),
        ]);

        setCollection(col);
        setEditName(col.name);
        setEditDescription(col.description ?? '');
        setDocuments(docPage.items);
        setTotalDocs(docPage.total);

        if (col.role === 'Owner') {
          const memberList = await collectionsApi.listMembers(id, signal);
          setMembers(memberList);
        } else {
          setMembers([]);
        }
      } catch (err) {
        if (err instanceof DOMException && err.name === 'AbortError') {
          return;
        }
        setPageError(err);
      } finally {
        setIsLoading(false);
      }
    },
    [id],
  );

  useEffect(() => {
    const controller = new AbortController();
    void loadAll(page, statusFilter, controller.signal);
    return () => controller.abort();
  }, [page, statusFilter, loadAll]);

  // Fetch details for initially Failed rows so errorMessage is surfaced cleanly
  useEffect(() => {
    const failedWithoutDetails = documents.filter(
      (doc) => doc.status === 'Failed' && !detailsById[doc.id],
    );
    if (failedWithoutDetails.length === 0) {
      return;
    }

    const controller = new AbortController();
    let active = true;

    void Promise.allSettled(
      failedWithoutDetails.map((doc) => documentsApi.get(doc.id, controller.signal)),
    ).then((results) => {
      if (!active) return;
      const updates: Record<string, DocumentDetailResponse> = {};
      for (const res of results) {
        if (res.status === 'fulfilled' && res.value && typeof res.value === 'object' && 'id' in res.value) {
          updates[res.value.id] = res.value;
        }
      }
      if (Object.keys(updates).length > 0) {
        setDetailsById((prev) => ({ ...prev, ...updates }));
      }
    });

    return () => {
      active = false;
      controller.abort();
    };
  }, [documents, detailsById]);

  const activePollIds = documents.filter((d) => isNonTerminal(d.status)).map((d) => d.id);
  const activePollKey = activePollIds.join(',');

  // Polling loop for Uploaded / Processing documents
  useEffect(() => {
    if (!activePollKey || pollingStopped) {
      return;
    }

    const idsToPoll = activePollKey.split(',').filter(Boolean);
    let cancelled = false;
    let timerId: number | undefined;
    const controller = new AbortController();

    const pollOnce = async () => {
      try {
        const results = await Promise.all(
          idsToPoll.map((docId) => documentsApi.get(docId, controller.signal)),
        );
        if (cancelled) return;

        pollFailCountRef.current = 0;

        const detailMap: Record<string, DocumentDetailResponse> = {};
        for (const detail of results) {
          if (detail && detail.id) {
            detailMap[detail.id] = detail;
          }
        }

        setDetailsById((prev) => ({ ...prev, ...detailMap }));
        setDocuments((prevDocs) =>
          prevDocs.map((doc) => {
            const detail = detailMap[doc.id];
            if (!detail) return doc;
            return {
              ...doc,
              status: detail.status,
              pageCount: detail.pageCount,
            };
          }),
        );

        const stillRunning = results.some((d) => d && isNonTerminal(d.status));
        if (stillRunning && !cancelled) {
          timerId = window.setTimeout(() => {
            void pollOnce();
          }, POLL_INTERVAL_MS);
        }
      } catch (err) {
        if (cancelled || (err instanceof DOMException && err.name === 'AbortError')) {
          return;
        }
        if (err instanceof ApiError && (err.status === 401 || err.status === 403 || err.status === 404)) {
          setPollingStopped(true);
          return;
        }
        pollFailCountRef.current += 1;
        if (pollFailCountRef.current >= MAX_CONSECUTIVE_POLL_ERRORS) {
          setPollingStopped(true);
          return;
        }
        if (!cancelled) {
          timerId = window.setTimeout(() => {
            void pollOnce();
          }, POLL_INTERVAL_MS);
        }
      }
    };

    timerId = window.setTimeout(() => {
      void pollOnce();
    }, POLL_INTERVAL_MS);

    return () => {
      cancelled = true;
      controller.abort();
      if (timerId !== undefined) {
        window.clearTimeout(timerId);
      }
    };
  }, [activePollKey, pollingStopped, pollTrigger]);

  const handleUpload = async (file: File) => {
    if (!id) return;
    setActionError(null);
    setActionNotice(null);
    setIsUploading(true);
    setUploadProgress(0);
    setUploadingFileName(file.name);

    try {
      const uploaded = await documentsApi.upload(id, file, {
        onProgress: (percent) => setUploadProgress(percent),
      });
      setPollingStopped(false);
      pollFailCountRef.current = 0;
      setPollTrigger((n) => n + 1);
      setActionNotice(fa.collectionDetail.uploadSuccess);

      setDocuments((prev) => {
        const filtered = prev.filter((d) => d.id !== uploaded.id);
        return [uploaded, ...filtered];
      });
      setTotalDocs((prev) => prev + 1);
    } catch (err) {
      setActionError(err);
    } finally {
      setIsUploading(false);
      setUploadProgress(0);
      setUploadingFileName(null);
    }
  };

  const handleToggleDetails = async (docId: string) => {
    const currentlyExpanded = Boolean(expandedDocIds[docId]);
    setExpandedDocIds((prev) => ({ ...prev, [docId]: !currentlyExpanded }));

    if (!currentlyExpanded && !detailsById[docId]) {
      setLoadingDetailIds((prev) => ({ ...prev, [docId]: true }));
      try {
        const detail = await documentsApi.get(docId);
        setDetailsById((prev) => ({ ...prev, [docId]: detail }));
      } catch (err) {
        setActionError(err);
      } finally {
        setLoadingDetailIds((prev) => ({ ...prev, [docId]: false }));
      }
    }
  };

  const handleDownloadPdf = async (doc: DocumentResponse, mode: 'open' | 'download') => {
    setActionError(null);
    setDownloadingDocId(doc.id);
    try {
      const blob = await documentsApi.downloadFile(doc.id);
      triggerPdfAction(blob, doc.fileName, mode);
    } catch (err) {
      setActionError(err);
    } finally {
      setDownloadingDocId(null);
    }
  };

  const handleReprocess = async (docId: string) => {
    setActionError(null);
    setActionNotice(null);
    setReprocessingDocId(docId);
    try {
      const updated = await documentsApi.reprocess(docId);
      setPollingStopped(false);
      pollFailCountRef.current = 0;
      setPollTrigger((n) => n + 1);
      setDocuments((prev) => prev.map((d) => (d.id === docId ? updated : d)));
      setDetailsById((prev) => {
        const copy = { ...prev };
        delete copy[docId];
        return copy;
      });
    } catch (err) {
      setActionError(err);
    } finally {
      setReprocessingDocId(null);
    }
  };

  const handleDeleteDocument = async (doc: DocumentResponse) => {
    if (!window.confirm(fa.collectionDetail.confirmDeleteDocument(doc.fileName))) {
      return;
    }
    setActionError(null);
    setDeletingDocId(doc.id);
    try {
      await documentsApi.delete(doc.id);
      setDocuments((prev) => prev.filter((d) => d.id !== doc.id));
      setTotalDocs((prev) => Math.max(0, prev - 1));
    } catch (err) {
      setActionError(err);
    } finally {
      setDeletingDocId(null);
    }
  };

  const handleSaveCollection = async (event: FormEvent) => {
    event.preventDefault();
    if (!id) return;
    const trimmedName = editName.trim();
    if (!trimmedName || trimmedName.length > 200) {
      return;
    }

    setActionError(null);
    setIsSavingCollection(true);
    try {
      const updated = await collectionsApi.update(id, {
        name: trimmedName,
        description: editDescription.trim() || null,
      });
      setCollection(updated);
      setIsEditingCollection(false);
      setActionNotice(fa.collections.updatedSuccess);
    } catch (err) {
      setActionError(err);
    } finally {
      setIsSavingCollection(false);
    }
  };

  const handleDeleteCollection = async () => {
    if (!collection) return;
    if (!window.confirm(fa.collections.confirmDeleteCollection(collection.name))) {
      return;
    }

    setActionError(null);
    setIsDeletingCollection(true);
    try {
      await collectionsApi.delete(collection.id);
      navigate('/collections', { replace: true });
    } catch (err) {
      setActionError(err);
      setIsDeletingCollection(false);
    }
  };

  const handleUpsertMember = async (event: FormEvent) => {
    event.preventDefault();
    if (!id) return;
    const trimmedEmail = memberEmail.trim();
    if (!trimmedEmail) {
      return;
    }

    setActionError(null);
    setActionNotice(null);
    setIsSavingMember(true);
    try {
      const saved = await collectionsApi.upsertMember(id, {
        email: trimmedEmail,
        role: memberRole,
      });
      setMembers((prev) => {
        const exists = prev.some((m) => m.userId === saved.userId);
        return exists ? prev.map((m) => (m.userId === saved.userId ? saved : m)) : [...prev, saved];
      });
      setMemberEmail('');
      setActionNotice(fa.collectionDetail.members.updatedSuccess);
    } catch (err) {
      setActionError(err);
    } finally {
      setIsSavingMember(false);
    }
  };

  const handleRemoveMember = async (userId: string) => {
    if (!id) return;
    setActionError(null);
    setRemovingMemberId(userId);
    try {
      await collectionsApi.removeMember(id, userId);
      setMembers((prev) => prev.filter((m) => m.userId !== userId));
    } catch (err) {
      setActionError(err);
    } finally {
      setRemovingMemberId(null);
    }
  };

  const canEdit = collection?.role === 'Editor' || collection?.role === 'Owner';
  const isOwner = collection?.role === 'Owner';
  const totalPages = Math.max(1, Math.ceil(totalDocs / PAGE_SIZE));

  return (
    <Layout>
      <div className="detail-stack">
        <div>
          <Link to="/collections" className="btn btn-ghost btn-sm">
            {fa.collectionDetail.backToCollections}
          </Link>
        </div>

        {pageError !== null && (
          <ErrorAlert error={pageError} onRetry={() => void loadAll(page, statusFilter)} />
        )}

        {isLoading ? (
          <div className="detail-stack" role="status" aria-live="polite" aria-label={fa.common.loading}>
            <span className="sr-only">{fa.common.loading}</span>
            <div className="card skeleton-card" aria-hidden="true" style={{ minBlockSize: '110px' }}>
              <div className="skeleton-line skeleton-line-lg" />
              <div className="skeleton-line skeleton-line-sm" />
            </div>
            <div className="card skeleton-card" aria-hidden="true" style={{ minBlockSize: '190px' }}>
              <div className="skeleton-line skeleton-line-md" />
              <div className="skeleton-line" />
              <div className="skeleton-line" />
              <div className="skeleton-line skeleton-line-sm" />
            </div>
          </div>
        ) : (
          collection && (
            <>
              <section className="card">
                <div className="page-header" style={{ marginBlockEnd: 0 }}>
                  <div>
                    <div style={{ display: 'flex', alignItems: 'center', gap: '10px', flexWrap: 'wrap' }}>
                      <h1 className="page-title">
                        <bdi>{collection.name}</bdi>
                      </h1>
                      <RoleBadge role={collection.role} />
                    </div>
                    {collection.description && (
                      <p className="page-subtitle" dir="auto">
                        {collection.description}
                      </p>
                    )}
                  </div>

                  <div className="collection-card-actions">
                    <Link
                      to={`/chat?collectionId=${encodeURIComponent(collection.id)}`}
                      className="btn btn-primary"
                    >
                      <ChatIcon />
                      <span>{fa.collectionDetail.askAction}</span>
                    </Link>

                    {canEdit && (
                      <button
                        type="button"
                        className="btn btn-secondary"
                        onClick={() => setIsEditingCollection((prev) => !prev)}
                      >
                        {isEditingCollection ? fa.common.cancel : fa.collections.editCollection}
                      </button>
                    )}

                    {isOwner && (
                      <button
                        type="button"
                        className="btn btn-danger"
                        disabled={isDeletingCollection}
                        onClick={() => void handleDeleteCollection()}
                      >
                        <TrashIcon />
                        <span>
                          {isDeletingCollection ? fa.common.deleting : fa.collections.deleteCollection}
                        </span>
                      </button>
                    )}
                  </div>
                </div>

                {isEditingCollection && canEdit && (
                  <form
                    className="form-stack"
                    style={{ marginBlockStart: '16px', paddingBlockStart: '16px', borderTop: '1px solid var(--color-border)' }}
                    onSubmit={handleSaveCollection}
                  >
                    <div className="form-row">
                      <div className="field">
                        <label className="field-label" htmlFor={editNameId}>
                          {fa.collections.nameLabel}
                        </label>
                        <input
                          id={editNameId}
                          type="text"
                          className="input"
                          maxLength={200}
                          required
                          value={editName}
                          onChange={(e) => setEditName(e.target.value)}
                        />
                      </div>
                      <div className="field">
                        <label className="field-label" htmlFor={editDescId}>
                          {fa.collections.descriptionLabel}
                        </label>
                        <input
                          id={editDescId}
                          type="text"
                          className="input"
                          maxLength={2000}
                          value={editDescription}
                          onChange={(e) => setEditDescription(e.target.value)}
                        />
                      </div>
                      <button type="submit" className="btn btn-primary" disabled={isSavingCollection}>
                        {isSavingCollection ? fa.common.saving : fa.common.save}
                      </button>
                    </div>
                  </form>
                )}
              </section>

              {actionError !== null && (
                <ErrorAlert error={actionError} onDismiss={() => setActionError(null)} />
              )}

              {actionNotice && (
                <div className="alert alert-success" role="status">
                  <p className="alert-detail">{actionNotice}</p>
                  <button
                    type="button"
                    className="btn btn-ghost btn-sm"
                    onClick={() => setActionNotice(null)}
                  >
                    {fa.common.close}
                  </button>
                </div>
              )}

              {canEdit && (
                <section className="card" aria-label={fa.collectionDetail.uploadSectionTitle}>
                  <h2 className="section-heading">{fa.collectionDetail.uploadSectionTitle}</h2>
                  <FileDrop
                    isUploading={isUploading}
                    uploadProgress={uploadProgress}
                    selectedFileName={uploadingFileName}
                    onFileSelected={(file) => void handleUpload(file)}
                  />
                </section>
              )}

              <section className="card" aria-label={fa.collectionDetail.documentsSectionTitle}>
                <div className="page-header" style={{ marginBlockEnd: '16px' }}>
                  <h2 className="section-heading" style={{ margin: 0 }}>
                    {fa.collectionDetail.documentsSectionTitle}
                  </h2>

                  <div className="form-row" style={{ alignItems: 'center' }}>
                    <label className="field-label" htmlFor={statusSelectId}>
                      {fa.collectionDetail.statusFilterLabel}
                    </label>
                    <select
                      id={statusSelectId}
                      className="select status-filter-select"
                      value={statusFilter}
                      onChange={(e) => {
                        setPage(1);
                        setStatusFilter(e.target.value as DocumentStatus | '');
                      }}
                    >
                      <option value="">{fa.collectionDetail.allStatuses}</option>
                      <option value="Ready">{fa.documentStatuses.Ready}</option>
                      <option value="Processing">{fa.documentStatuses.Processing}</option>
                      <option value="Uploaded">{fa.documentStatuses.Uploaded}</option>
                      <option value="Failed">{fa.documentStatuses.Failed}</option>
                    </select>
                  </div>
                </div>

                {activePollIds.length > 0 && !pollingStopped && (
                  <div className="alert alert-info" role="status" aria-live="polite" style={{ marginBlockEnd: '12px' }}>
                    <p className="alert-detail">{fa.collectionDetail.pollingNotice}</p>
                  </div>
                )}

                {activePollIds.length > 0 && pollingStopped && (
                  <div className="alert alert-warning" role="alert" style={{ marginBlockEnd: '12px' }}>
                    <p className="alert-detail">{fa.collectionDetail.pollingStoppedNotice}</p>
                    <button
                      type="button"
                      className="btn btn-secondary btn-sm"
                      onClick={() => {
                        pollFailCountRef.current = 0;
                        setPollingStopped(false);
                        setPollTrigger((n) => n + 1);
                      }}
                    >
                      {fa.common.retry}
                    </button>
                  </div>
                )}

                {documents.length === 0 ? (
                  <div className="empty-state">
                    <p>
                      {statusFilter
                        ? fa.collectionDetail.noMatchingDocuments
                        : fa.collectionDetail.emptyDocuments}
                    </p>
                  </div>
                ) : (
                  <>
                    <div className="table-scroll">
                      <table className="data-table">
                        <thead>
                          <tr>
                            <th>{fa.collectionDetail.columns.fileName}</th>
                            <th className="col-center">{fa.collectionDetail.columns.size}</th>
                            <th className="col-center">{fa.collectionDetail.columns.pages}</th>
                            <th className="col-center">{fa.collectionDetail.columns.status}</th>
                            <th>{fa.collectionDetail.columns.createdAt}</th>
                            <th>{fa.collectionDetail.columns.actions}</th>
                          </tr>
                        </thead>
                        <tbody>
                          {documents.map((doc) => {
                            const detail = detailsById[doc.id];
                            const isExpanded = Boolean(expandedDocIds[doc.id]);
                            const isLoadingDetail = Boolean(loadingDetailIds[doc.id]);
                            const translatedError =
                              doc.status === 'Failed' || detail?.errorMessage
                                ? translateDocumentError(detail?.errorMessage)
                                : null;

                            return (
                              <Fragment key={doc.id}>
                                <tr>
                                  <td>
                                    <bdi className="bidi-plain">{doc.fileName}</bdi>
                                    {doc.status === 'Failed' && !isExpanded && translatedError && (
                                      <div>
                                        <button
                                          type="button"
                                          className="doc-row-error-hint"
                                          onClick={() => void handleToggleDetails(doc.id)}
                                          title={fa.collectionDetail.detailFields.errorHintClick}
                                        >
                                          <AlertTriangleIcon />
                                          <span>{translatedError.title}</span>
                                        </button>
                                      </div>
                                    )}
                                  </td>
                                  <td className="col-center">
                                    <bdi dir="ltr">{formatBytes(doc.sizeBytes)}</bdi>
                                  </td>
                                  <td className="col-center">
                                    <bdi dir="ltr">{doc.pageCount ?? '—'}</bdi>
                                  </td>
                                  <td className="col-center">
                                    <StatusBadge status={doc.status} />
                                  </td>
                                  <td>
                                    <bdi dir="ltr">{new Date(doc.createdAt).toLocaleDateString('fa-IR')}</bdi>
                                  </td>
                                  <td>
                                    <div className="doc-actions">
                                      <button
                                        type="button"
                                        className="btn btn-secondary btn-sm"
                                        aria-expanded={isExpanded}
                                        onClick={() => void handleToggleDetails(doc.id)}
                                      >
                                        {isExpanded ? fa.common.hideDetails : fa.common.details}
                                      </button>

                                      <button
                                        type="button"
                                        className="btn btn-secondary btn-sm"
                                        disabled={downloadingDocId === doc.id}
                                        onClick={() => void handleDownloadPdf(doc, 'download')}
                                      >
                                        <FileTextIcon />
                                        <span>
                                          {downloadingDocId === doc.id
                                            ? fa.collectionDetail.openingPdf
                                            : fa.collectionDetail.downloadPdf}
                                        </span>
                                      </button>

                                      {canEdit && doc.status !== 'Processing' && (
                                        <button
                                          type="button"
                                          className="btn btn-secondary btn-sm"
                                          disabled={reprocessingDocId === doc.id}
                                          onClick={() => void handleReprocess(doc.id)}
                                        >
                                          {reprocessingDocId === doc.id
                                            ? fa.collectionDetail.reprocessing
                                            : fa.collectionDetail.reprocess}
                                        </button>
                                      )}

                                      {canEdit && (
                                        <button
                                          type="button"
                                          className="btn btn-danger btn-sm"
                                          disabled={deletingDocId === doc.id}
                                          onClick={() => void handleDeleteDocument(doc)}
                                        >
                                          <TrashIcon />
                                          <span>
                                            {deletingDocId === doc.id
                                              ? fa.common.deleting
                                              : fa.collectionDetail.deleteDocument}
                                          </span>
                                        </button>
                                      )}
                                    </div>
                                  </td>
                                </tr>
                                {isExpanded && (
                                  <tr>
                                    <td colSpan={6}>
                                      {isLoadingDetail ? (
                                        <span>{fa.collectionDetail.detailFields.loading}</span>
                                      ) : detail ? (
                                        <div className="doc-detail-panel panel-reveal">
                                          <div className="doc-detail-stats">
                                            <div className="doc-stat-item">
                                              <span className="doc-stat-label">
                                                {fa.collectionDetail.detailFields.chunks}
                                              </span>
                                              <span className="doc-stat-value">
                                                <bdi dir="ltr">{detail.chunkCount}</bdi>
                                              </span>
                                            </div>
                                            <div className="doc-stat-item">
                                              <span className="doc-stat-label">
                                                {fa.collectionDetail.detailFields.ocrPagesPrefix}{' '}
                                                <bdi dir="ltr">
                                                  {fa.collectionDetail.detailFields.ocrAcronym}
                                                </bdi>
                                              </span>
                                              <span className="doc-stat-value">
                                                {detail.ocrPages && detail.ocrPages.length > 0 ? (
                                                  <bdi dir="ltr">
                                                    {detail.ocrPages.length} ({detail.ocrPages.join(', ')})
                                                  </bdi>
                                                ) : (
                                                  fa.collectionDetail.detailFields.none
                                                )}
                                              </span>
                                            </div>
                                            <div className="doc-stat-item">
                                              <span className="doc-stat-label">
                                                {fa.collectionDetail.detailFields.processedAt}
                                              </span>
                                              <span className="doc-stat-value">
                                                {detail.processedAt ? (
                                                  <bdi dir="ltr">
                                                    {new Date(detail.processedAt).toLocaleString('fa-IR')}
                                                  </bdi>
                                                ) : (
                                                  '—'
                                                )}
                                              </span>
                                            </div>
                                          </div>
                                          {translatedError && (
                                            <div className="doc-error-callout" role="alert">
                                              <div className="doc-error-callout-header">
                                                <AlertTriangleIcon />
                                                <span>{translatedError.title}</span>
                                              </div>
                                              <p className="doc-error-callout-guidance">
                                                {translatedError.guidance}
                                              </p>
                                              <div className="doc-error-callout-actions">
                                                {translatedError.technicalDetail && (
                                                  <details className="doc-error-technical">
                                                    <summary>
                                                      {fa.collectionDetail.detailFields.technicalDetailToggle}
                                                    </summary>
                                                    <bdi dir="ltr" className="mono doc-error-technical-text">
                                                      {translatedError.technicalDetail}
                                                    </bdi>
                                                  </details>
                                                )}
                                                {canEdit && doc.status !== 'Processing' && (
                                                  <button
                                                    type="button"
                                                    className="btn btn-secondary btn-sm"
                                                    disabled={reprocessingDocId === doc.id}
                                                    onClick={() => void handleReprocess(doc.id)}
                                                  >
                                                    {reprocessingDocId === doc.id
                                                      ? fa.collectionDetail.reprocessing
                                                      : fa.collectionDetail.reprocess}
                                                  </button>
                                                )}
                                              </div>
                                            </div>
                                          )}
                                        </div>
                                      ) : (
                                        <span>{fa.common.empty}</span>
                                      )}
                                    </td>
                                  </tr>
                                )}
                              </Fragment>
                            );
                          })}
                        </tbody>
                      </table>
                    </div>

                    {totalPages > 1 && (
                      <nav className="pagination" aria-label={fa.collectionDetail.documentsSectionTitle}>
                        <span>{fa.common.totalItems(totalDocs)}</span>
                        <div className="pagination-controls">
                          <button
                            type="button"
                            className="btn btn-secondary btn-sm"
                            disabled={page <= 1}
                            onClick={() => setPage((p) => Math.max(1, p - 1))}
                          >
                            {fa.common.previousPage}
                          </button>
                          <span>{fa.common.pageOf(page, totalPages)}</span>
                          <button
                            type="button"
                            className="btn btn-secondary btn-sm"
                            disabled={page >= totalPages}
                            onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
                          >
                            {fa.common.nextPage}
                          </button>
                        </div>
                      </nav>
                    )}
                  </>
                )}
              </section>

              {isOwner && (
                <section className="card" aria-label={fa.collectionDetail.membersSectionTitle}>
                  <h2 className="section-heading">{fa.collectionDetail.membersSectionTitle}</h2>

                  <form className="form-row" onSubmit={handleUpsertMember}>
                    <div className="field">
                      <label className="field-label" htmlFor={memberEmailId}>
                        {fa.collectionDetail.members.emailLabel}
                      </label>
                      <input
                        id={memberEmailId}
                        type="email"
                        dir="ltr"
                        className="input"
                        placeholder={fa.collectionDetail.members.emailPlaceholder}
                        value={memberEmail}
                        onChange={(e) => setMemberEmail(e.target.value)}
                        required
                      />
                    </div>

                    <div className="field" style={{ maxInlineSize: '180px' }}>
                      <label className="field-label" htmlFor={memberRoleId}>
                        {fa.collectionDetail.members.roleLabel}
                      </label>
                      <select
                        id={memberRoleId}
                        className="select"
                        value={memberRole}
                        onChange={(e) => setMemberRole(e.target.value as CollectionRole)}
                      >
                        <option value="Viewer">{fa.roles.Viewer}</option>
                        <option value="Editor">{fa.roles.Editor}</option>
                        <option value="Owner">{fa.roles.Owner}</option>
                      </select>
                    </div>

                    <button type="submit" className="btn btn-primary" disabled={isSavingMember}>
                      {isSavingMember
                        ? fa.collectionDetail.members.adding
                        : fa.collectionDetail.members.addButton}
                    </button>
                  </form>

                  {members.length === 0 ? (
                    <p style={{ marginBlockStart: '16px' }}>{fa.collectionDetail.members.empty}</p>
                  ) : (
                    <ul className="member-list">
                      {members.map((member) => (
                        <li key={member.userId} className="member-item">
                          <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
                            <bdi>{member.email}</bdi>
                            <RoleBadge role={member.role} />
                          </div>
                          <button
                            type="button"
                            className="btn btn-danger btn-sm"
                            disabled={removingMemberId === member.userId}
                            onClick={() => void handleRemoveMember(member.userId)}
                          >
                            {removingMemberId === member.userId
                              ? fa.common.deleting
                              : fa.collectionDetail.members.removeButton}
                          </button>
                        </li>
                      ))}
                    </ul>
                  )}
                </section>
              )}
            </>
          )
        )}
      </div>
    </Layout>
  );
}
