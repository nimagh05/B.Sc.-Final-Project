import {
  useEffect,
  useId,
  useRef,
  useState,
  type FormEvent,
  type KeyboardEvent,
} from 'react';
import { ApiError } from '../api/errors';
import type { SourceCitationDto } from '../api/types';
import { CitationText } from '../components/CitationText';
import { DegradedBanner } from '../components/DegradedBanner';
import { ErrorAlert } from '../components/ErrorAlert';
import {
  AlertTriangleIcon,
  CheckIcon,
  CopyIcon,
  PlusIcon,
  SendIcon,
  SparklesIcon,
} from '../components/Icons';
import { SourceCard } from '../components/SourceCard';
import { fa, formatPersianNumber } from '../i18n/fa';
import {
  normalizeApiBaseUrl,
  openPdfBlob,
  parseCollectionIds,
  widgetAsk,
  widgetDownloadDocument,
  type ParsedApiBaseUrl,
} from './widgetClient';

export interface DocSearchWidgetAppProps {
  apiUrl: string;
  token: string;
  collectionIds: string;
  widgetTitle: string;
  placeholder: string;
}

interface WidgetMessage {
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

function resolveConfigurationError(
  parsedApi: ParsedApiBaseUrl,
  collectionIds: string[],
  trimmedToken: string,
): string | null {
  if (!parsedApi.valid) {
    return fa.widget.missingApiUrl;
  }
  if (collectionIds.length === 0) {
    return fa.widget.missingCollectionIds;
  }
  if (collectionIds.length > 20) {
    return fa.widget.tooManyCollectionIds;
  }
  if (!trimmedToken) {
    return fa.widget.missingToken;
  }
  return null;
}

function resolveErrorContext(err: unknown): 'chat' | 'general' {
  if (err instanceof ApiError && err.status === 404 && err.detail === fa.errors.unknown) {
    return 'general';
  }
  return 'chat';
}

export function DocSearchWidgetApp({
  apiUrl,
  token,
  collectionIds,
  widgetTitle,
  placeholder,
}: DocSearchWidgetAppProps) {
  const instanceId = useId();
  const rootContainerRef = useRef<HTMLDivElement | null>(null);
  const messagesListRef = useRef<HTMLDivElement | null>(null);
  const askAbortRef = useRef<AbortController | null>(null);
  const pdfAbortRef = useRef<AbortController | null>(null);
  const copyTimerRef = useRef<number | null>(null);
  const messageSeqRef = useRef(0);

  const parsedApi = normalizeApiBaseUrl(apiUrl);
  const parsedCollectionIds = parseCollectionIds(collectionIds);
  const trimmedToken = token.trim();
  const configError = resolveConfigurationError(parsedApi, parsedCollectionIds, trimmedToken);

  const scopeKey = `${parsedApi.valid ? parsedApi.baseUrl : ''}|${parsedCollectionIds.join(',')}`;
  const prevScopeKeyRef = useRef(scopeKey);

  const [conversationId, setConversationId] = useState<string | null>(null);
  const [messages, setMessages] = useState<WidgetMessage[]>([]);
  const [question, setQuestion] = useState('');
  const [validationError, setValidationError] = useState<string | null>(null);
  const [isAsking, setIsAsking] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [pdfError, setPdfError] = useState<unknown>(null);
  const [activeCitation, setActiveCitation] = useState<{ messageId: string; index: number } | null>(
    null,
  );
  const [downloadingSourceKey, setDownloadingSourceKey] = useState<string | null>(null);
  const [copiedMessageId, setCopiedMessageId] = useState<string | null>(null);

  // Abort in-flight requests and reset conversation state when API URL or collection scope changes.
  useEffect(() => {
    if (prevScopeKeyRef.current !== scopeKey) {
      prevScopeKeyRef.current = scopeKey;
      askAbortRef.current?.abort();
      askAbortRef.current = null;
      pdfAbortRef.current?.abort();
      pdfAbortRef.current = null;
      setConversationId(null);
      setMessages([]);
      setIsAsking(false);
      setDownloadingSourceKey(null);
      setActiveCitation(null);
      setError(null);
      setPdfError(null);
      setValidationError(null);
    }
  }, [scopeKey]);

  // Preserve conversation/messages on token rotation; clear stale 401 auth errors if token refreshed.
  useEffect(() => {
    if (trimmedToken) {
      setError((prev: unknown) =>
        prev instanceof ApiError && prev.status === 401 ? null : prev,
      );
    }
  }, [trimmedToken]);

  useEffect(() => {
    return () => {
      askAbortRef.current?.abort();
      pdfAbortRef.current?.abort();
      if (copyTimerRef.current !== null) {
        window.clearTimeout(copyTimerRef.current);
      }
    };
  }, []);

  useEffect(() => {
    const listEl = messagesListRef.current;
    if (listEl) {
      listEl.scrollTop = listEl.scrollHeight;
    }
  }, [messages.length, isAsking]);

  const effectiveTitle = widgetTitle.trim() || fa.widget.defaultTitle;
  const effectivePlaceholder = placeholder.trim() || fa.chat.questionPlaceholder;

  const handleNewConversation = () => {
    askAbortRef.current?.abort();
    askAbortRef.current = null;
    pdfAbortRef.current?.abort();
    pdfAbortRef.current = null;
    setConversationId(null);
    setMessages([]);
    setQuestion('');
    setValidationError(null);
    setError(null);
    setPdfError(null);
    setActiveCitation(null);
    setIsAsking(false);
    setDownloadingSourceKey(null);
  };

  const handleCopyAnswer = async (msg: WidgetMessage) => {
    if (!msg.content) {
      return;
    }
    try {
      if (navigator.clipboard && typeof navigator.clipboard.writeText === 'function') {
        await navigator.clipboard.writeText(msg.content);
      }
      setCopiedMessageId(msg.id);
      if (copyTimerRef.current !== null) {
        window.clearTimeout(copyTimerRef.current);
      }
      copyTimerRef.current = window.setTimeout(() => {
        setCopiedMessageId((prev) => (prev === msg.id ? null : prev));
      }, 2000);
    } catch {
      // Clipboard API unavailable
    }
  };

  const handleSelectCitation = (messageId: string, index: number) => {
    setActiveCitation({ messageId, index });
    const targetId = `ds-source-${instanceId}-${messageId}-${index}`;
    const rootNode = rootContainerRef.current?.getRootNode();
    const target =
      rootNode && typeof (rootNode as ShadowRoot).getElementById === 'function'
        ? (rootNode as ShadowRoot).getElementById(targetId)
        : document.getElementById(targetId);

    if (target) {
      target.scrollIntoView?.({ block: 'nearest', behavior: 'smooth' });
      target.focus({ preventScroll: true });
    }
  };

  const handleOpenSourcePdf = async (sourceKey: string, documentId: string, fileName: string) => {
    if (!parsedApi.valid || !trimmedToken) {
      return;
    }

    pdfAbortRef.current?.abort();
    const controller = new AbortController();
    pdfAbortRef.current = controller;

    setPdfError(null);
    setDownloadingSourceKey(sourceKey);

    try {
      const blob = await widgetDownloadDocument({
        apiUrl: parsedApi.baseUrl,
        token: trimmedToken,
        documentId,
        signal: controller.signal,
      });
      openPdfBlob(blob, fileName);
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') {
        return;
      }
      setPdfError(err);
    } finally {
      if (pdfAbortRef.current === controller) {
        pdfAbortRef.current = null;
      }
      setDownloadingSourceKey((prev) => (prev === sourceKey ? null : prev));
    }
  };

  const submitQuestion = async () => {
    if (configError) {
      return;
    }

    const trimmed = question.trim();
    if (trimmed.length < 3 || trimmed.length > 2000) {
      setValidationError(fa.chat.validation.questionLength);
      return;
    }

    askAbortRef.current?.abort();
    const controller = new AbortController();
    askAbortRef.current = controller;

    setValidationError(null);
    setError(null);
    setPdfError(null);
    setIsAsking(true);

    const userMsgId = `u-${++messageSeqRef.current}`;
    setMessages((prev) => [
      ...prev,
      {
        id: userMsgId,
        role: 'user',
        content: trimmed,
        notFound: false,
        degraded: false,
        sources: [],
      },
    ]);
    setQuestion('');

    try {
      const response = await widgetAsk({
        apiUrl: parsedApi.baseUrl,
        token: trimmedToken,
        payload: {
          question: trimmed,
          collectionIds: parsedCollectionIds,
          conversationId,
        },
        signal: controller.signal,
      });

      if (response.conversationId) {
        setConversationId(response.conversationId);
      }

      const assistantMsgId = `a-${++messageSeqRef.current}`;
      setMessages((prev) => [
        ...prev,
        {
          id: assistantMsgId,
          role: 'assistant',
          content: response.answer ?? '',
          notFound: response.notFound,
          degraded: response.degraded,
          sources: response.sources ?? [],
        },
      ]);
      setActiveCitation(null);
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') {
        return;
      }
      setMessages((prev) => prev.filter((m) => m.id !== userMsgId));
      setQuestion(trimmed);
      setError(err);
    } finally {
      if (askAbortRef.current === controller) {
        askAbortRef.current = null;
      }
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

  const questionInputId = `ds-question-${instanceId}`;

  return (
    <div ref={rootContainerRef} className="ds-widget-shell">
      <header className="ds-widget-header">
        <div className="ds-widget-brand">
          <SparklesIcon />
          <h2 className="ds-widget-title">
            <bdi>{effectiveTitle}</bdi>
          </h2>
        </div>

        <button
          type="button"
          className="btn btn-secondary btn-sm"
          onClick={handleNewConversation}
        >
          <PlusIcon />
          <span>{fa.chat.newConversation}</span>
        </button>
      </header>

      <div className="ds-widget-body">
        {configError && (
          <div className="alert alert-warning" role="alert">
            <div className="alert-content">
              <div className="alert-lead">
                <AlertTriangleIcon />
                <p className="alert-detail">{configError}</p>
              </div>
            </div>
          </div>
        )}

        {error !== null && (
          <ErrorAlert
            error={error}
            context={resolveErrorContext(error)}
            onDismiss={() => setError(null)}
          />
        )}

        {pdfError !== null && (
          <ErrorAlert
            error={pdfError}
            context="document"
            onDismiss={() => setPdfError(null)}
          />
        )}

        <div ref={messagesListRef} className="ds-widget-messages" aria-live="polite">
          {messages.length === 0 && !isAsking ? (
            <div className="ds-widget-empty">
              <p className="ds-widget-empty-title">{fa.chat.emptyThreadTitle}</p>
              <p className="ds-widget-empty-desc">{fa.widget.emptyDescription}</p>
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
                                elementId={`ds-source-${instanceId}-${sourceKey}`}
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
            <div className="ds-widget-label-row">
              <label className="field-label" htmlFor={questionInputId}>
                {fa.chat.questionLabel}
              </label>
              <span className="field-hint">
                <bdi dir="ltr">{formatPersianNumber(question.trim().length)} / ۲۰۰۰</bdi>
              </span>
            </div>

            <textarea
              id={questionInputId}
              dir={getComposerDirection(question)}
              className="textarea chat-textarea"
              placeholder={effectivePlaceholder}
              value={question}
              onChange={(e) => {
                setQuestion(e.target.value);
                if (validationError) {
                  setValidationError(null);
                }
              }}
              onKeyDown={handleTextareaKeyDown}
              maxLength={2000}
              aria-invalid={Boolean(validationError)}
            />
          </div>

          <div className="ds-widget-composer-footer">
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
              disabled={isAsking}
            >
              <SendIcon />
              <span>{isAsking ? fa.chat.sendingButton : fa.chat.sendButton}</span>
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
