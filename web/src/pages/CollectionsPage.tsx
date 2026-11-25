import { useCallback, useEffect, useId, useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { collectionsApi } from '../api/endpoints';
import type { CollectionDto } from '../api/types';
import { ErrorAlert } from '../components/ErrorAlert';
import { ChatIcon, FileTextIcon, FolderIcon, PlusIcon } from '../components/Icons';
import { Layout } from '../components/Layout';
import { RoleBadge } from '../components/StatusBadge';
import { useToast } from '../components/Toast';
import { fa } from '../i18n/fa';

const PAGE_SIZE = 20;

export function CollectionsPage() {
  const { showToast } = useToast();

  const [items, setItems] = useState<CollectionDto[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [isLoading, setIsLoading] = useState(true);
  const [loadError, setLoadError] = useState<unknown>(null);

  const [showCreate, setShowCreate] = useState(false);
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [formError, setFormError] = useState<string | null>(null);
  const [createApiError, setCreateApiError] = useState<unknown>(null);
  const [isCreating, setIsCreating] = useState(false);
  const [filterText, setFilterText] = useState('');

  const nameId = useId();
  const descId = useId();
  const filterId = useId();

  const loadCollections = useCallback(async (targetPage: number, signal?: AbortSignal) => {
    setIsLoading(true);
    setLoadError(null);
    try {
      const result = await collectionsApi.list(targetPage, PAGE_SIZE, signal);
      setItems(result.items);
      setTotal(result.total);
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') {
        return;
      }
      setLoadError(err);
    } finally {
      setIsLoading(false);
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void loadCollections(page, controller.signal);
    return () => controller.abort();
  }, [page, loadCollections]);

  const handleCreate = async (event: FormEvent) => {
    event.preventDefault();
    const trimmed = name.trim();
    if (!trimmed || trimmed.length > 200) {
      setFormError(fa.collections.nameRequired);
      return;
    }

    setFormError(null);
    setCreateApiError(null);
    setIsCreating(true);

    try {
      await collectionsApi.create({
        name: trimmed,
        description: description.trim() || null,
      });
      setName('');
      setDescription('');
      setShowCreate(false);
      setPage(1);
      showToast(fa.collections.createdSuccess, 'success');
      await loadCollections(1);
    } catch (err) {
      setCreateApiError(err);
    } finally {
      setIsCreating(false);
    }
  };

  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const normalizedFilter = filterText.trim().toLowerCase();
  const visibleItems = normalizedFilter
    ? items.filter(
        (c) =>
          c.name.toLowerCase().includes(normalizedFilter) ||
          (c.description && c.description.toLowerCase().includes(normalizedFilter)),
      )
    : items;

  return (
    <Layout>
      <div className="page-header">
        <div>
          <h1 className="page-title">{fa.collections.pageTitle}</h1>
          <p className="page-subtitle">{fa.collections.pageDescription}</p>
        </div>
        <button
          type="button"
          className="btn btn-primary"
          onClick={() => {
            setShowCreate((prev) => !prev);
            setFormError(null);
            setCreateApiError(null);
          }}
        >
          {!showCreate && <PlusIcon />}
          <span>{showCreate ? fa.common.cancel : fa.collections.newCollection}</span>
        </button>
      </div>

      {showCreate && (
        <section className="card panel-reveal" style={{ marginBlockEnd: '24px' }} aria-label={fa.collections.newCollection}>
          <h2 className="section-heading">{fa.collections.newCollection}</h2>
          <form className="form-stack" onSubmit={handleCreate} noValidate>
            {createApiError !== null && (
              <ErrorAlert
                error={createApiError}
                context="collection"
                onDismiss={() => setCreateApiError(null)}
              />
            )}

            <div className="field">
              <label className="field-label" htmlFor={nameId}>
                {fa.collections.nameLabel}
              </label>
              <input
                id={nameId}
                type="text"
                className="input"
                maxLength={200}
                placeholder={fa.collections.namePlaceholder}
                value={name}
                onChange={(e) => {
                  setName(e.target.value);
                  if (formError) setFormError(null);
                  if (createApiError) setCreateApiError(null);
                }}
                aria-invalid={Boolean(formError)}
              />
              {formError && (
                <span className="field-error" role="alert">
                  {formError}
                </span>
              )}
            </div>

            <div className="field">
              <label className="field-label" htmlFor={descId}>
                {fa.collections.descriptionLabel}
              </label>
              <input
                id={descId}
                type="text"
                className="input"
                maxLength={2000}
                placeholder={fa.collections.descriptionPlaceholder}
                value={description}
                onChange={(e) => setDescription(e.target.value)}
              />
            </div>

            <div>
              <button type="submit" className="btn btn-primary" disabled={isCreating}>
                {isCreating ? fa.common.creating : fa.common.create}
              </button>
            </div>
          </form>
        </section>
      )}

      {loadError !== null && (
        <div style={{ marginBlockEnd: '16px' }}>
          <ErrorAlert
            error={loadError}
            context="collection"
            onRetry={() => void loadCollections(page)}
          />
        </div>
      )}

      {isLoading ? (
        <div className="collections-grid" role="status" aria-live="polite" aria-label={fa.common.loading}>
          <span className="sr-only">{fa.common.loading}</span>
          {[0, 1, 2].map((idx) => (
            <div key={idx} className="card skeleton-card" aria-hidden="true">
              <div className="skeleton-line skeleton-line-lg" />
              <div className="skeleton-line skeleton-line-md" />
              <div className="skeleton-line skeleton-line-sm" style={{ marginBlockStart: 'auto' }} />
            </div>
          ))}
        </div>
      ) : items.length === 0 && loadError === null ? (
        <div className="card empty-state">
          <h2 className="empty-state-title">{fa.collections.emptyTitle}</h2>
          <p>{fa.collections.emptyDescription}</p>
          {!showCreate && (
            <button type="button" className="btn btn-primary" onClick={() => setShowCreate(true)}>
              <PlusIcon />
              <span>{fa.collections.newCollection}</span>
            </button>
          )}
        </div>
      ) : (
        <>
          <div className="collections-toolbar">
            <label className="sr-only" htmlFor={filterId}>
              {fa.collections.filterLabel}
            </label>
            <input
              id={filterId}
              type="search"
              className="input"
              placeholder={fa.collections.filterPlaceholder}
              value={filterText}
              onChange={(e) => setFilterText(e.target.value)}
            />
          </div>

          {visibleItems.length === 0 ? (
            <div className="card empty-state">
              <p>{fa.collections.filterEmpty}</p>
            </div>
          ) : (
            <div className="collections-grid">
              {visibleItems.map((collection) => (
                <article key={collection.id} className="card collection-card">
                  <div>
                    <div className="collection-card-head">
                      <h2 className="collection-card-title">
                        <bdi>{collection.name}</bdi>
                      </h2>
                      <RoleBadge role={collection.role} />
                    </div>
                    {collection.description && (
                      <p className="collection-card-desc">
                        <bdi>{collection.description}</bdi>
                      </p>
                    )}
                  </div>

                  <div className="collection-card-footer">
                    <div className="collection-card-meta">
                      <span className="collection-meta-item">
                        <FileTextIcon />
                        <span>{fa.collections.documentCount(collection.documentCount)}</span>
                      </span>
                      <span>
                        <bdi dir="ltr">{new Date(collection.createdAt).toLocaleDateString('fa-IR')}</bdi>
                      </span>
                    </div>

                    <div className="collection-card-actions">
                      <Link to={`/collections/${collection.id}`} className="btn btn-primary btn-sm">
                        <FolderIcon />
                        <span>{fa.collections.openCollection}</span>
                      </Link>
                      <Link
                        to={`/chat?collectionId=${encodeURIComponent(collection.id)}`}
                        className="btn btn-secondary btn-sm"
                      >
                        <ChatIcon />
                        <span>{fa.collections.askInCollection}</span>
                      </Link>
                    </div>
                  </div>
                </article>
              ))}
            </div>
          )}

          {totalPages > 1 && (
            <nav className="pagination" aria-label={fa.collections.pageTitle}>
              <span>{fa.common.totalItems(total)}</span>
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
    </Layout>
  );
}
