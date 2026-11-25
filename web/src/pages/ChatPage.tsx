import { useCallback, useEffect, useId, useState, type FormEvent, type KeyboardEvent } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { askApi, collectionsApi, conversationsApi, documentsApi, triggerPdfAction } from '../api/endpoints';
import type {
  CollectionDto,
  ConversationSummaryDto,
  SourceCitationDto,
} from '../api/types';
import { CitationText } from '../components/CitationText';
import { ConfirmDialog } from '../components/ConfirmDialog';
import { DegradedBanner } from '../components/DegradedBanner';
import { ErrorAlert } from '../components/ErrorAlert';
import {
  CheckIcon,
  CopyIcon,
  FolderIcon,
  PlusIcon,
  SendIcon,
  SparklesIcon,
  TrashIcon,
} from '../components/Icons';
import { Layout } from '../components/Layout';
import { SourceCard } from '../components/SourceCard';
import { useToast } from '../components/Toast';
import { fa, formatPersianNumber } from '../i18n/fa';

interface ChatMessageItem {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  notFound: boolean;
  degraded: boolean;
  sources: SourceCitationDto[];
}

function getComposerDirection(text: string): 'rtl' | 'ltr' {
  const trimmed = text.trim();
  if (!trimmed) {
    return 'rtl';
  }
  const firstStrong = trimmed.match(/[A-Za-z\u0600-\u06FF\u0750-\u077F\uFB50-\uFDFF\uFE70-\uFEFF]/);
  if (firstStrong && /[A-Za-z]/.test(firstStrong[0])) {
    return 'ltr';
  }
  return 'rtl';
}

export function ChatPage() {
  const [searchParams] = useSearchParams();
  const initialCollectionIdParam = searchParams.get('collectionId');
  const { showToast } = useToast();

  const [collections, setCollections] = useState<CollectionDto[]>([]);
  const [selectedCollectionIds, setSelectedCollectionIds] = useState<string[]>([]);
  const [conversations, setConversations] = useState<ConversationSummaryDto[]>([]);
  const [activeConversationId, setActiveConversationId] = useState<string | null>(null);
  const [messages, setMessages] = useState<ChatMessageItem[]>([]);

  const [question, setQuestion] = useState('');
  const [validationError, setValidationError] = useState<string | null>(null);
  const [initLoading, setInitLoading] = useState(true);
  const [messagesLoading, setMessagesLoading] = useState(false);
  const [isAsking, setIsAsking] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [pdfError, setPdfError] = useState<unknown>(null);

  const [activeCitation, setActiveCitation] = useState<{ messageId: string; index: number } | null>(
    null,
  );
  const [downloadingSourceKey, setDownloadingSourceKey] = useState<string | null>(null);
  const [copiedMessageId, setCopiedMessageId] = useState<string | null>(null);
  const [confirmDeleteConv, setConfirmDeleteConv] = useState<ConversationSummaryDto | null>(null);
  const [isDeletingConv, setIsDeletingConv] = useState(false);

  const questionId = useId();

  const loadInitial = useCallback(
    async (signal?: AbortSignal) => {
      setInitLoading(true);
      setError(null);
      try {
        const [colPage, convList] = await Promise.all([
          collectionsApi.list(1, 100, signal),
          conversationsApi.list(1, 50, signal),
        ]);

        setCollections(colPage.items);
        setConversations(convList);

        setSelectedCollectionIds((prev) => {
          if (initialCollectionIdParam && colPage.items.some((c) => c.id === initialCollectionIdParam)) {
            return [initialCollectionIdParam];
          }
          if (prev.length > 0) {
            return prev.filter((cid) => colPage.items.some((c) => c.id === cid));
          }
          return colPage.items.length > 0 ? [colPage.items[0].id] : [];
        });
      } catch (err) {
        if (err instanceof DOMException && err.name === 'AbortError') {
          return;
        }
        setError(err);
      } finally {
        setInitLoading(false);
      }
    },
    [initialCollectionIdParam],
  );

  useEffect(() => {
    const controller = new AbortController();
    void loadInitial(controller.signal);
    return () => controller.abort();
  }, [loadInitial]);

  const toggleCollection = (collectionId: string) => {
    setValidationError(null);
    setSelectedCollectionIds((prev) =>
      prev.includes(collectionId)
        ? prev.filter((id) => id !== collectionId)
        : prev.length < 20
          ? [...prev, collectionId]
          : prev,
    );
  };

  const handleSelectConversation = async (conversationId: string) => {
    setError(null);
    setPdfError(null);
    setActiveCitation(null);
    setActiveConversationId(conversationId);
    setMessagesLoading(true);

    try {
      const loaded = await conversationsApi.getMessages(conversationId);
      setMessages(
        loaded.map((m) => ({
          id: m.id,
          role: m.role,
          content: m.content,
          notFound: m.notFound,
          degraded: m.degraded,
          sources: m.sources ?? [],
        })),
      );
    } catch (err) {
      setError(err);
    } finally {
      setMessagesLoading(false);
    }
  };

  const executeDeleteConversation = async () => {
    if (!confirmDeleteConv) return;
    const targetId = confirmDeleteConv.id;
    setError(null);
    setIsDeletingConv(true);
    try {
      await conversationsApi.delete(targetId);
      setConversations((prev) => prev.filter((c) => c.id !== targetId));
      if (activeConversationId === targetId) {
        setActiveConversationId(null);
        setMessages([]);
        setActiveCitation(null);
      }
      setConfirmDeleteConv(null);
      showToast(fa.chat.conversationDeletedSuccess, 'success');
    } catch (err) {
      setConfirmDeleteConv(null);
      setError(err);
    } finally {
      setIsDeletingConv(false);
    }
  };

  const handleNewConversation = () => {
    setActiveConversationId(null);
    setMessages([]);
    setActiveCitation(null);
    setError(null);
    setValidationError(null);
  };

  const handleCopyAnswer = async (msg: ChatMessageItem) => {
    if (!msg.content) return;
    try {
      if (navigator.clipboard && typeof navigator.clipboard.writeText === 'function') {
        await navigator.clipboard.writeText(msg.content);
      }
      setCopiedMessageId(msg.id);
      showToast(fa.chat.answerCopiedToast, 'success');
      window.setTimeout(() => {
        setCopiedMessageId((prev) => (prev === msg.id ? null : prev));
      }, 2000);
    } catch {
      // Clipboard API unavailable
    }
  };

  const submitQuestion = async () => {
    const trimmed = question.trim();
    if (trimmed.length < 3 || trimmed.length > 2000) {
      setValidationError(fa.chat.validation.questionLength);
      return;
    }

    if (selectedCollectionIds.length === 0) {
      setValidationError(fa.chat.validation.noCollectionsSelected);
      return;
    }

    if (selectedCollectionIds.length > 20) {
      setValidationError(fa.chat.validation.tooManyCollections);
      return;
    }

    setValidationError(null);
    setError(null);
    setPdfError(null);
    setIsAsking(true);

    const optimisticUserId = `user-${Date.now()}`;
    setMessages((prev) => [
      ...prev,
      {
        id: optimisticUserId,
        role: 'user',
        content: trimmed,
        notFound: false,
        degraded: false,
        sources: [],
      },
    ]);
    setQuestion('');

    try {
      const response = await askApi.ask({
        question: trimmed,
        collectionIds: selectedCollectionIds,
        conversationId: activeConversationId,
      });

      if (response.conversationId && response.conversationId !== activeConversationId) {
        setActiveConversationId(response.conversationId);
        const convId = response.conversationId;
        setConversations((prev) => {
          if (prev.some((c) => c.id === convId)) {
            return prev;
          }
          return [
            {
              id: convId,
              title: trimmed.slice(0, 60),
              createdAt: new Date().toISOString(),
            },
            ...prev,
          ];
        });
      }

      const assistantMsg: ChatMessageItem = {
        id: `assistant-${Date.now()}`,
        role: 'assistant',
        content: response.answer ?? '',
        notFound: response.notFound,
        degraded: response.degraded,
        sources: response.sources ?? [],
      };
      setMessages((prev) => [...prev, assistantMsg]);
      setActiveCitation(null);
    } catch (err) {
      setMessages((prev) => prev.filter((m) => m.id !== optimisticUserId));
      setQuestion(trimmed);
      setError(err);
    } finally {
      setIsAsking(false);
    }
  };

  const handleFormSubmit = (event: FormEvent) => {
    event.preventDefault();
    void submitQuestion();
  };

  const handleTextareaKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') {
      event.preventDefault();
      if (!isAsking) {
        void submitQuestion();
      }
    }
  };

  const handleSelectCitation = (messageId: string, index: number) => {
    setActiveCitation({ messageId, index });
    const targetId = `source-${messageId}-${index}`;
    const element = document.getElementById(targetId);
    if (element) {
      if (typeof element.scrollIntoView === 'function') {
        element.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
      }
      element.focus({ preventScroll: true });
    }
  };

  const handleOpenSourcePdf = async (sourceKey: string, documentId: string, fileName: string) => {
    setPdfError(null);
    setDownloadingSourceKey(sourceKey);
    try {
      const blob = await documentsApi.downloadFile(documentId);
      triggerPdfAction(blob, fileName, 'open');
    } catch (err) {
      setPdfError(err);
    } finally {
      setDownloadingSourceKey(null);
    }
  };

  const showSidebarCollectionHint =
    selectedCollectionIds.length === 0 &&
    validationError !== fa.chat.validation.noCollectionsSelected;

  return (
    <Layout>
      <div className="page-header" style={{ marginBlockEnd: '16px' }}>
        <h1 className="page-title">{fa.chat.pageTitle}</h1>
      </div>

      <div className="chat-layout">
        <aside className="chat-sidebar" aria-label={fa.chat.collectionsHeading}>
          <section className="card">
            <div className="chat-sidebar-head">
              <h2 className="section-heading collection-meta-item" style={{ margin: 0, fontSize: '1rem' }}>
                <FolderIcon />
                <span>{fa.chat.collectionsHeading}</span>
              </h2>
            </div>

            {initLoading ? (
              <p>{fa.common.loading}</p>
            ) : collections.length === 0 ? (
              <div className="form-stack">
                <p className="field-hint">{fa.chat.noCollectionsAvailable}</p>
                <Link to="/collections" className="btn btn-secondary btn-sm">
                  {fa.nav.collections}
                </Link>
              </div>
            ) : (
              <>
                <div className="doc-actions" style={{ marginBlockEnd: '10px' }}>
                  <button
                    type="button"
                    className="btn btn-ghost btn-sm"
                    onClick={() => {
                      setValidationError(null);
                      setSelectedCollectionIds(collections.slice(0, 20).map((c) => c.id));
                    }}
                  >
                    {fa.chat.selectAllCollections}
                  </button>
                  <button
                    type="button"
                    className="btn btn-ghost btn-sm"
                    onClick={() => setSelectedCollectionIds([])}
                  >
                    {fa.chat.clearCollections}
                  </button>
                </div>

                <div className="collection-picker-list">
                  {collections.map((col) => (
                    <label key={col.id} className="checkbox-row">
                      <input
                        type="checkbox"
                        checked={selectedCollectionIds.includes(col.id)}
                        onChange={() => toggleCollection(col.id)}
                      />
                      <span>
                        <bdi>{col.name}</bdi>
                      </span>
                    </label>
                  ))}
                </div>

                {showSidebarCollectionHint && (
                  <p className="field-hint" style={{ marginBlockStart: '8px' }}>
                    {fa.chat.selectCollectionHint}
                  </p>
                )}
              </>
            )}
          </section>

          <section className="card">
            <div className="chat-sidebar-head">
              <h2 className="section-heading" style={{ margin: 0, fontSize: '1rem' }}>
                {fa.chat.conversationsHeading}
              </h2>
              <button
                type="button"
                className="btn btn-secondary btn-sm"
                onClick={handleNewConversation}
              >
                <PlusIcon />
                <span>{fa.chat.newConversation}</span>
              </button>
            </div>

            {conversations.length === 0 ? (
              <p className="field-hint">{fa.chat.emptyConversations}</p>
            ) : (
              <ul className="conversation-list">
                {conversations.map((conv) => (
                  <li key={conv.id} className="conversation-item">
                    <button
                      type="button"
                      className="conversation-select-btn"
                      aria-current={activeConversationId === conv.id ? 'true' : undefined}
                      onClick={() => void handleSelectConversation(conv.id)}
                    >
                      <bdi>{conv.title || fa.chat.newConversation}</bdi>
                    </button>
                    <button
                      type="button"
                      className="btn btn-ghost btn-sm"
                      aria-label={`${fa.chat.deleteConversation}: ${conv.title ?? ''}`}
                      onClick={() => setConfirmDeleteConv(conv)}
                    >
                      <TrashIcon />
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </section>
        </aside>

        <section className="chat-main" aria-label={fa.chat.pageTitle}>
          {error !== null && (
            <ErrorAlert error={error} context="chat" onDismiss={() => setError(null)} />
          )}
          {pdfError !== null && (
            <ErrorAlert error={pdfError} context="document" onDismiss={() => setPdfError(null)} />
          )}

          <div className="chat-thread" aria-live="polite">
            {messagesLoading ? (
              <div className="card" role="status">
                <p>{fa.common.loading}</p>
              </div>
            ) : messages.length === 0 ? (
              <div className="card empty-state">
                <h2 className="empty-state-title">{fa.chat.emptyThreadTitle}</h2>
                <p>{fa.chat.emptyThreadDescription}</p>
              </div>
            ) : (
              messages.map((msg) => (
                <article
                  key={msg.id}
                  className={`card message-card ${msg.role === 'user' ? 'message-card-user' : ''}`}
                >
                  <div className="message-header-row">
                    <span className="message-role">
                      {msg.role === 'assistant' && <SparklesIcon />}
                      <span>{msg.role === 'user' ? fa.chat.userLabel : fa.chat.assistantLabel}</span>
                    </span>

                    {msg.role === 'assistant' && !msg.notFound && msg.content && (
                      <button
                        type="button"
                        className="btn btn-ghost btn-sm message-copy-btn"
                        onClick={() => void handleCopyAnswer(msg)}
                      >
                        {copiedMessageId === msg.id ? <CheckIcon /> : <CopyIcon />}
                        <span>
                          {copiedMessageId === msg.id ? fa.chat.copiedAnswer : fa.chat.copyAnswer}
                        </span>
                      </button>
                    )}
                  </div>

                  {msg.role === 'user' ? (
                    <p className="answer-paragraph" dir="auto">
                      {msg.content}
                    </p>
                  ) : (
                    <>
                      {msg.degraded && <DegradedBanner />}

                      {msg.notFound && (
                        <div className="alert alert-neutral" role="status">
                          <div className="alert-content">
                            <p className="alert-title">{fa.chat.notFoundTitle}</p>
                            <p className="alert-detail">{fa.chat.notFoundDescription}</p>
                          </div>
                        </div>
                      )}

                      {!msg.notFound && msg.content && (
                        <CitationText
                          text={msg.content}
                          sources={msg.sources}
                          activeSourceIndex={
                            activeCitation?.messageId === msg.id ? activeCitation.index : null
                          }
                          onSelectSource={(idx) => handleSelectCitation(msg.id, idx)}
                        />
                      )}

                      {msg.sources.length > 0 && (
                        <div className="sources-section">
                          <h3 className="sources-heading">
                            {fa.chat.sourcesHeading(msg.sources.length)}
                          </h3>
                          <div className="sources-grid">
                            {msg.sources.map((src) => {
                              const sourceKey = `${msg.id}-${src.index}`;
                              const isActive =
                                activeCitation?.messageId === msg.id &&
                                activeCitation.index === src.index;
                              return (
                                <SourceCard
                                  key={sourceKey}
                                  source={src}
                                  elementId={`source-${sourceKey}`}
                                  isActive={isActive}
                                  isDownloading={downloadingSourceKey === sourceKey}
                                  onOpenPdf={(docId, fileName) =>
                                    void handleOpenSourcePdf(sourceKey, docId, fileName)
                                  }
                                />
                              );
                            })}
                          </div>
                        </div>
                      )}
                    </>
                  )}
                </article>
              ))
            )}

            {isAsking && (
              <article className="card message-card" role="status" aria-live="polite">
                <span className="message-role">
                  <SparklesIcon />
                  <span>{fa.chat.assistantLabel}</span>
                </span>
                <div className="thinking-dots" aria-hidden="true">
                  <span />
                  <span />
                  <span />
                </div>
                <span className="sr-only">{fa.chat.sendingButton}</span>
              </article>
            )}
          </div>

          <form className="card form-stack" onSubmit={handleFormSubmit} noValidate>
            <div className="field">
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                <label className="field-label" htmlFor={questionId}>
                  {fa.chat.questionLabel}
                </label>
                <span className="field-hint">
                  <bdi dir="ltr">{formatPersianNumber(question.trim().length)} / ۲۰۰۰</bdi>
                </span>
              </div>
              <textarea
                id={questionId}
                dir={getComposerDirection(question)}
                className="textarea chat-textarea"
                placeholder={fa.chat.questionPlaceholder}
                value={question}
                onChange={(e) => {
                  setQuestion(e.target.value);
                  if (validationError) setValidationError(null);
                }}
                onKeyDown={handleTextareaKeyDown}
                maxLength={2000}
                aria-invalid={Boolean(validationError)}
              />
            </div>

            <div className="chat-composer-footer">
              {validationError ? (
                <span className="field-error" role="alert">
                  {validationError}
                </span>
              ) : (
                <span className="field-hint">{fa.chat.questionLengthHint}</span>
              )}

              <button
                type="submit"
                className="btn btn-primary"
                disabled={isAsking || collections.length === 0}
              >
                <SendIcon />
                <span>{isAsking ? fa.chat.sendingButton : fa.chat.sendButton}</span>
              </button>
            </div>
          </form>
        </section>
      </div>

      {confirmDeleteConv && (
        <ConfirmDialog
          open={confirmDeleteConv !== null}
          title={fa.chat.confirmDeleteConversationTitle}
          description={fa.chat.confirmDeleteConversation(confirmDeleteConv.title ?? '')}
          confirmLabel={fa.chat.deleteConversation}
          variant="danger"
          isBusy={isDeletingConv}
          onConfirm={executeDeleteConversation}
          onCancel={() => {
            if (!isDeletingConv) {
              setConfirmDeleteConv(null);
            }
          }}
        />
      )}
    </Layout>
  );
}
