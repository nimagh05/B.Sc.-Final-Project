import { Fragment, useCallback, useEffect, useId, useMemo, useRef, useState, type FormEvent } from 'react';
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
import { useAuth } from '../auth/AuthContext';
import { ConfirmDialog } from '../components/ConfirmDialog';
import { CustomSelect, type CustomSelectOption } from '../components/CustomSelect';
import { ErrorAlert } from '../components/ErrorAlert';
import { FileDrop } from '../components/FileDrop';
import { AlertTriangleIcon, ChatIcon, FileTextIcon, ShieldIcon, TrashIcon } from '../components/Icons';
import { Layout } from '../components/Layout';
import { RoleBadge, StatusBadge } from '../components/StatusBadge';
import { useToast } from '../components/Toast';
import { fa, formatPersianNumber } from '../i18n/fa';

const PAGE_SIZE = 20;
const POLL_INTERVAL_MS = 1500;
const MAX_CONSECUTIVE_POLL_ERRORS = 3;

const ROLE_ORDER: Record<CollectionRole, number> = {
  Owner: 0,
  Editor: 1,
  Viewer: 2,
};

type StatusFilterValue = DocumentStatus | '';

const STATUS_FILTER_OPTIONS: readonly CustomSelectOption<StatusFilterValue>[] = [
  { value: '', label: fa.collectionDetail.allStatuses, dotColor: '#64748b' },
  { value: 'Ready', label: fa.documentStatuses.Ready, dotColor: '#166534' },
  { value: 'Processing', label: fa.documentStatuses.Processing, dotColor: '#1d4ed8' },
  { value: 'Uploaded', label: fa.documentStatuses.Uploaded, dotColor: '#475569' },
  { value: 'Failed', label: fa.documentStatuses.Failed, dotColor: '#b91c1c' },
];

const ROLE_OPTIONS: readonly CustomSelectOption<CollectionRole>[] = [
  {
    value: 'Viewer',
    label: fa.roles.Viewer,
    description: fa.roleDescriptions.Viewer,
  },
  {
    value: 'Editor',
    label: fa.roles.Editor,
    description: fa.roleDescriptions.Editor,
  },
  {
    value: 'Owner',
    label: fa.roles.Owner,
    description: fa.roleDescriptions.Owner,
  },
];

type ConfirmTarget =
  | { kind: 'collection' }
  | { kind: 'document'; doc: DocumentResponse }
  | { kind: 'member'; member: CollectionMemberDto };

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
  const { user } = useAuth();
  const { showToast } = useToast();

  const [collection, setCollection] = useState<CollectionDto | null>(null);
  const [documents, setDocuments] = useState<DocumentResponse[]>([]);
  const [totalDocs, setTotalDocs] = useState(0);
  const [page, setPage] = useState(1);
  const [statusFilter, setStatusFilter] = useState<StatusFilterValue>('');
  const [detailsById, setDetailsById] = useState<Record<string, DocumentDetailResponse>>({});
  const [expandedDocIds, setExpandedDocIds] = useState<Record<string, boolean>>({});
  const [loadingDetailIds, setLoadingDetailIds] = useState<Record<string, boolean>>({});

  const [isLoading, setIsLoading] = useState(true);
  const [pageError, setPageError] = useState<unknown>(null);

  // Scoped card errors
  const [editError, setEditError] = useState<unknown>(null);
  const [editNameError, setEditNameError] = useState<string | null>(null);
  const [uploadError, setUploadError] = useState<unknown>(null);
  const [documentsError, setDocumentsError] = useState<unknown>(null);
  const [membersError, setMembersError] = useState<unknown>(null);
  const [memberEmailError, setMemberEmailError] = useState<string | null>(null);

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
  const [updatingMemberId, setUpdatingMemberId] = useState<string | null>(null);
  const [removingMemberId, setRemovingMemberId] = useState<string | null>(null);

  // Confirmation modal state
  const [confirmTarget, setConfirmTarget] = useState<ConfirmTarget | null>(null);

  const statusLabelId = useId();
  const statusSelectId = useId();
  const editNameId = useId();
  const editDescId = useId();
  const memberEmailId = useId();
  const memberRoleLabelId = useId();
  const memberRoleId = useId();

  const loadAll = useCallback(
    async (targetPage: number, targetStatus: StatusFilterValue, signal?: AbortSignal) => {
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
    setUploadError(null);
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
      showToast(fa.collectionDetail.uploadSuccess, 'success');

      setDocuments((prev) => {
        const filtered = prev.filter((d) => d.id !== uploaded.id);
        return [uploaded, ...filtered];
      });
      setTotalDocs((prev) => prev + 1);
    } catch (err) {
      setUploadError(err);
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
        setDocumentsError(err);
      } finally {
        setLoadingDetailIds((prev) => ({ ...prev, [docId]: false }));
      }
    }
  };

  const handleDownloadPdf = async (doc: DocumentResponse, mode: 'open' | 'download') => {
    setDocumentsError(null);
    setDownloadingDocId(doc.id);
    try {
      const blob = await documentsApi.downloadFile(doc.id);
      triggerPdfAction(blob, doc.fileName, mode);
    } catch (err) {
      setDocumentsError(err);
    } finally {
      setDownloadingDocId(null);
    }
  };

  const handleReprocess = async (docId: string) => {
    setDocumentsError(null);
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
      showToast(fa.collectionDetail.reprocessSuccess, 'info');
    } catch (err) {
      setDocumentsError(err);
    } finally {
      setReprocessingDocId(null);
    }
  };

  const executeDeleteDocument = async (doc: DocumentResponse) => {
    setDocumentsError(null);
    setDeletingDocId(doc.id);
    try {
      await documentsApi.delete(doc.id);
      setDocuments((prev) => prev.filter((d) => d.id !== doc.id));
      setTotalDocs((prev) => Math.max(0, prev - 1));
      setConfirmTarget(null);
      showToast(fa.collectionDetail.deleteSuccess, 'success');
    } catch (err) {
      setConfirmTarget(null);
      setDocumentsError(err);
    } finally {
      setDeletingDocId(null);
    }
  };

  const handleToggleEditCollection = () => {
    setEditError(null);
    setEditNameError(null);
    if (collection) {
      setEditName(collection.name);
      setEditDescription(collection.description ?? '');
    }
    setIsEditingCollection((prev) => !prev);
  };

  const handleSaveCollection = async (event: FormEvent) => {
    event.preventDefault();
    if (!id) return;
    const trimmedName = editName.trim();
    if (!trimmedName || trimmedName.length > 200) {
      setEditNameError(fa.collections.nameRequired);
      return;
    }

    setEditNameError(null);
    setEditError(null);
    setIsSavingCollection(true);
    try {
      const updated = await collectionsApi.update(id, {
        name: trimmedName,
        description: editDescription.trim() || null,
      });
      setCollection(updated);
      setIsEditingCollection(false);
      showToast(fa.collections.updatedSuccess, 'success');
    } catch (err) {
      setEditError(err);
    } finally {
      setIsSavingCollection(false);
    }
  };

  const executeDeleteCollection = async () => {
    if (!collection) return;
    setEditError(null);
    setIsDeletingCollection(true);
    try {
      await collectionsApi.delete(collection.id);
      setConfirmTarget(null);
      showToast(fa.collections.deletedSuccess, 'success');
      navigate('/collections', { replace: true });
    } catch (err) {
      setConfirmTarget(null);
      setEditError(err);
      setIsDeletingCollection(false);
    }
  };

  const handleUpsertMember = async (event: FormEvent) => {
    event.preventDefault();
    if (!id) return;
    const trimmedEmail = memberEmail.trim();
    if (!trimmedEmail || !trimmedEmail.includes('@')) {
      setMemberEmailError(fa.collectionDetail.members.emailRequired);
      return;
    }

    setMemberEmailError(null);
    setMembersError(null);
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
      showToast(fa.collectionDetail.members.updatedSuccess, 'success');
    } catch (err) {
      setMembersError(err);
    } finally {
      setIsSavingMember(false);
    }
  };

  const handleInlineRoleChange = async (member: CollectionMemberDto, nextRole: CollectionRole) => {
    if (!id || nextRole === member.role) return;
    setMembersError(null);
    setUpdatingMemberId(member.userId);
    try {
      const saved = await collectionsApi.upsertMember(id, {
        email: member.email,
        role: nextRole,
      });
      setMembers((prev) => prev.map((m) => (m.userId === saved.userId ? saved : m)));
      showToast(
        fa.collectionDetail.members.roleUpdatedSuccess(member.email, fa.roles[saved.role]),
        'success',
      );
    } catch (err) {
      // Do not mutate members state on failure so row CustomSelect stays/rolls back to member.role
      setMembersError(err);
    } finally {
      setUpdatingMemberId(null);
    }
  };

  const executeRemoveMember = async (member: CollectionMemberDto) => {
    if (!id) return;
    setMembersError(null);
    setRemovingMemberId(member.userId);
    try {
      await collectionsApi.removeMember(id, member.userId);
      setMembers((prev) => prev.filter((m) => m.userId !== member.userId));
      setConfirmTarget(null);
      showToast(fa.collectionDetail.members.removedSuccess, 'success');
    } catch (err) {
      setConfirmTarget(null);
      setMembersError(err);
    } finally {
      setRemovingMemberId(null);
    }
  };

  const canEdit = collection?.role === 'Editor' || collection?.role === 'Owner';
  const isOwner = collection?.role === 'Owner';
  const totalPages = Math.max(1, Math.ceil(totalDocs / PAGE_SIZE));

  const ownerCount = useMemo(
    () => members.filter((m) => m.role === 'Owner').length,
    [members],
  );

  const sortedMembers = useMemo(
    () =>
      [...members].sort((a, b) => {
        const roleDiff = ROLE_ORDER[a.role] - ROLE_ORDER[b.role];
        if (roleDiff !== 0) return roleDiff;
        if (user?.id) {
          if (a.userId === user.id && b.userId !== user.id) return -1;
          if (b.userId === user.id && a.userId !== user.id) return 1;
        }
        return a.email.localeCompare(b.email);
      }),
    [members, user?.id],
  );

  const isConfirmBusy =
    isDeletingCollection || Boolean(deletingDocId) || Boolean(removingMemberId);

  const confirmModalCopy = (() => {
    if (!confirmTarget) return null;
    if (confirmTarget.kind === 'collection' && collection) {
      return {
        title: fa.collections.confirmDeleteTitle,
        description: fa.collections.confirmDeleteCollection(collection.name),
        confirmLabel: fa.collections.deleteCollection,
        onConfirm: executeDeleteCollection,
      };
    }
    if (confirmTarget.kind === 'document') {
      return {
        title: fa.collectionDetail.confirmDeleteDocumentTitle,
        description: fa.collectionDetail.confirmDeleteDocument(confirmTarget.doc.fileName),
        confirmLabel: fa.collectionDetail.deleteDocument,
        onConfirm: () => executeDeleteDocument(confirmTarget.doc),
      };
    }
    if (confirmTarget.kind === 'member') {
      return {
        title: fa.collectionDetail.members.confirmRemoveTitle,
        description: fa.collectionDetail.members.confirmRemoveMember(confirmTarget.member.email),
        confirmLabel: fa.collectionDetail.members.removeButton,
        onConfirm: () => executeRemoveMember(confirmTarget.member),
      };
    }
    return null;
  })();

  return (
    <Layout>
      <div className="detail-stack">
        <div>
          <Link to="/collections" className="btn btn-ghost btn-sm">
            {fa.collectionDetail.backToCollections}
          </Link>
        </div>

        {pageError !== null && (
          <ErrorAlert
            error={pageError}
            context="collection"
            onRetry={() => void loadAll(page, statusFilter)}
          />
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
                      <p className="page-subtitle">
                        <bdi>{collection.description}</bdi>
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
                        onClick={handleToggleEditCollection}
                      >
                        {isEditingCollection ? fa.common.cancel : fa.collections.editCollection}
                      </button>
                    )}

                    {isOwner && (
                      <button
                        type="button"
                        className="btn btn-danger"
                        disabled={isDeletingCollection}
                        onClick={() => setConfirmTarget({ kind: 'collection' })}
                      >
                        <TrashIcon />
                        <span>
                          {isDeletingCollection ? fa.common.deleting : fa.collections.deleteCollection}
                        </span>
                      </button>
                    )}
                  </div>
                </div>

                {!isEditingCollection && editError !== null && (
                  <div style={{ marginBlockStart: '16px' }}>
                    <ErrorAlert
                      error={editError}
                      context="collection"
                      onDismiss={() => setEditError(null)}
                    />
                  </div>
                )}

                {isEditingCollection && canEdit && (
                  <form
                    className="form-stack panel-reveal"
                    style={{
                      marginBlockStart: '16px',
                      paddingBlockStart: '16px',
                      borderTop: '1px solid var(--color-border)',
                    }}
                    onSubmit={handleSaveCollection}
                    noValidate
                  >
                    {editError !== null && (
                      <ErrorAlert
                        error={editError}
                        context="collection"
                        onDismiss={() => setEditError(null)}
                      />
                    )}

                    <div className="form-row collection-edit-form">
                      <div className="field">
                        <label className="field-label" htmlFor={editNameId}>
                          {fa.collections.nameLabel}
                        </label>
                        <input
                          id={editNameId}
                          type="text"
                          className="input"
                          maxLength={200}
                          value={editName}
                          onChange={(e) => {
                            setEditName(e.target.value);
                            if (editNameError) setEditNameError(null);
                            if (editError) setEditError(null);
                          }}
                          aria-invalid={Boolean(editNameError)}
                        />
                        {editNameError && (
                          <span className="field-error" role="alert">
                            {editNameError}
                          </span>
                        )}
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

              {canEdit && (
                <section className="card" aria-label={fa.collectionDetail.uploadSectionTitle}>
                  <h2 className="section-heading">{fa.collectionDetail.uploadSectionTitle}</h2>
                  <FileDrop
                    isUploading={isUploading}
                    uploadProgress={uploadProgress}
                    selectedFileName={uploadingFileName}
                    onFileSelected={(file) => void handleUpload(file)}
                    externalError={uploadError}
                    onClearExternalError={() => setUploadError(null)}
                  />
                </section>
              )}

              <section className="card" aria-label={fa.collectionDetail.documentsSectionTitle}>
                <div className="page-header" style={{ marginBlockEnd: '16px' }}>
                  <h2 className="section-heading" style={{ margin: 0 }}>
                    {fa.collectionDetail.documentsSectionTitle}
                  </h2>

                  <div className="status-filter-wrap">
                    <label id={statusLabelId} className="field-label" htmlFor={statusSelectId}>
                      {fa.collectionDetail.statusFilterLabel}
                    </label>
                    <CustomSelect
                      id={statusSelectId}
                      ariaLabelledBy={statusLabelId}
                      compact
                      alignEnd
                      className="status-filter-dropdown"
                      value={statusFilter}
                      options={STATUS_FILTER_OPTIONS}
                      onChange={(nextStatus) => {
                        setPage(1);
                        setStatusFilter(nextStatus);
                      }}
                    />
                  </div>
                </div>

                {documentsError !== null && (
                  <div style={{ marginBlockEnd: '12px' }}>
                    <ErrorAlert
                      error={documentsError}
                      context="document"
                      onDismiss={() => setDocumentsError(null)}
                    />
                  </div>
                )}

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
                                  <td className="col-filename">
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
                                  <td className="col-center col-nowrap">
                                    <bdi dir="ltr">{formatBytes(doc.sizeBytes)}</bdi>
                                  </td>
                                  <td className="col-center col-nowrap">
                                    <bdi dir="ltr">
                                      {doc.pageCount !== null ? formatPersianNumber(doc.pageCount) : '—'}
                                    </bdi>
                                  </td>
                                  <td className="col-center col-nowrap">
                                    <StatusBadge status={doc.status} />
                                  </td>
                                  <td className="col-nowrap">
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
                                          onClick={() => setConfirmTarget({ kind: 'document', doc })}
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

                  {membersError !== null && (
                    <div style={{ marginBlockEnd: '12px' }}>
                      <ErrorAlert
                        error={membersError}
                        context="member"
                        onDismiss={() => setMembersError(null)}
                      />
                    </div>
                  )}

                  <form className="form-row member-add-form" onSubmit={handleUpsertMember} noValidate>
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
                        onChange={(e) => {
                          setMemberEmail(e.target.value);
                          if (memberEmailError) setMemberEmailError(null);
                          if (membersError) setMembersError(null);
                        }}
                        aria-invalid={Boolean(memberEmailError)}
                      />
                      {memberEmailError && (
                        <span className="field-error" role="alert">
                          {memberEmailError}
                        </span>
                      )}
                    </div>

                    <div className="field member-role-field">
                      <label id={memberRoleLabelId} className="field-label" htmlFor={memberRoleId}>
                        {fa.collectionDetail.members.roleLabel}
                      </label>
                      <CustomSelect
                        id={memberRoleId}
                        ariaLabelledBy={memberRoleLabelId}
                        value={memberRole}
                        options={ROLE_OPTIONS}
                        onChange={(nextRole) => {
                          setMemberRole(nextRole);
                          if (membersError) setMembersError(null);
                        }}
                      />
                    </div>

                    <button type="submit" className="btn btn-primary" disabled={isSavingMember}>
                      {isSavingMember
                        ? fa.collectionDetail.members.adding
                        : fa.collectionDetail.members.addButton}
                    </button>
                  </form>

                  {sortedMembers.length === 0 ? (
                    <p style={{ marginBlockStart: '16px' }}>{fa.collectionDetail.members.empty}</p>
                  ) : (
                    <ul className="member-list">
                      {sortedMembers.map((member) => {
                        const isSoleOwner = ownerCount === 1 && member.role === 'Owner';
                        const isSelf = Boolean(user?.id && member.userId === user.id);
                        const isLockedRow = isSoleOwner || (ownerCount > 1 && isSelf);
                        const isRowBusy =
                          updatingMemberId === member.userId || removingMemberId === member.userId;

                        return (
                          <li key={member.userId} className="member-item">
                            <div className="member-info">
                              <bdi>{member.email}</bdi>
                              {isSoleOwner ? (
                                <span className="badge badge-primary-owner">
                                  <ShieldIcon />
                                  <span>{fa.collectionDetail.members.primaryOwnerBadge}</span>
                                </span>
                              ) : (
                                <RoleBadge role={member.role} />
                              )}
                              {isSelf && (
                                <span className="badge badge-you">
                                  {fa.collectionDetail.members.youBadge}
                                </span>
                              )}
                            </div>

                            {isLockedRow ? (
                              <div
                                className="member-locked-note"
                                title={
                                  isSoleOwner
                                    ? fa.collectionDetail.members.protectedOwnerHint
                                    : fa.collectionDetail.members.selfRowProtectedHint
                                }
                              >
                                <ShieldIcon />
                                <span>
                                  {isSoleOwner
                                    ? fa.collectionDetail.members.protectedOwnerHint
                                    : fa.collectionDetail.members.selfRowProtectedHint}
                                </span>
                              </div>
                            ) : (
                              <div className="member-controls">
                                {updatingMemberId === member.userId && (
                                  <span className="field-hint">
                                    {fa.collectionDetail.members.savingRole}
                                  </span>
                                )}
                                <CustomSelect
                                  compact
                                  alignEnd
                                  className="member-inline-role-select"
                                  ariaLabel={fa.collectionDetail.members.rowRoleAriaLabel(member.email)}
                                  value={member.role}
                                  options={ROLE_OPTIONS}
                                  disabled={isRowBusy}
                                  onChange={(nextRole) => void handleInlineRoleChange(member, nextRole)}
                                />
                                <button
                                  type="button"
                                  className="btn btn-danger btn-sm"
                                  disabled={isRowBusy}
                                  onClick={() => setConfirmTarget({ kind: 'member', member })}
                                >
                                  <TrashIcon />
                                  <span>
                                    {removingMemberId === member.userId
                                      ? fa.common.deleting
                                      : fa.collectionDetail.members.removeButton}
                                  </span>
                                </button>
                              </div>
                            )}
                          </li>
                        );
                      })}
                    </ul>
                  )}
                </section>
              )}
            </>
          )
        )}
      </div>

      {confirmModalCopy && (
        <ConfirmDialog
          open={confirmTarget !== null}
          title={confirmModalCopy.title}
          description={confirmModalCopy.description}
          confirmLabel={confirmModalCopy.confirmLabel}
          variant="danger"
          isBusy={isConfirmBusy}
          onConfirm={confirmModalCopy.onConfirm}
          onCancel={() => {
            if (!isConfirmBusy) {
              setConfirmTarget(null);
            }
          }}
        />
      )}
    </Layout>
  );
}
