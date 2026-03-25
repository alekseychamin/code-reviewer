import { useEffect, useMemo, useRef, useState } from 'react';
import type { InlineComment, InlineDiscussionStructuredContent, ReviewCommentMessage, ReviewedFile } from '../lib/types';
import { MarkdownBlock } from './MarkdownBlock';

interface ReviewedFilesWorkspaceProps {
  files: ReviewedFile[];
  onPublishInlineComment: (commentId: string) => Promise<void>;
  onAskInlineQuestion: (commentId: string, message: string) => Promise<void>;
}

export function ReviewedFilesWorkspace({
  files,
  onPublishInlineComment,
  onAskInlineQuestion
}: ReviewedFilesWorkspaceProps) {
  const [expandedFilePath, setExpandedFilePath] = useState<string>('');
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState<Record<string, boolean>>({});
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [expandedRemarks, setExpandedRemarks] = useState<Record<string, boolean>>({});
  const [expandedContexts, setExpandedContexts] = useState<Record<string, boolean>>({});
  const [pendingScrollFilePath, setPendingScrollFilePath] = useState<string>('');
  const fileCardRefs = useRef<Record<string, HTMLElement | null>>({});

  const filesWithRemarks = useMemo(
    () =>
      [...files]
        .filter((file) => (file.inlineThreads?.length ?? 0) > 0)
        .sort((left, right) => {
          const severityDelta = getFileSeverityRank(right) - getFileSeverityRank(left);
          if (severityDelta !== 0) {
            return severityDelta;
          }

          const severeCountDelta = getSevereThreadCount(right) - getSevereThreadCount(left);
          if (severeCountDelta !== 0) {
            return severeCountDelta;
          }

          const threadDelta = (right.inlineThreads?.length ?? 0) - (left.inlineThreads?.length ?? 0);
          if (threadDelta !== 0) {
            return threadDelta;
          }

          return left.filePath.localeCompare(right.filePath);
        }),
    [files]
  );

  useEffect(() => {
    if (!filesWithRemarks.length) {
      setExpandedFilePath('');
      return;
    }

    if (expandedFilePath && !filesWithRemarks.some((file) => file.filePath === expandedFilePath)) {
      setExpandedFilePath('');
    }
  }, [expandedFilePath, filesWithRemarks]);

  useEffect(() => {
    if (!pendingScrollFilePath || expandedFilePath !== pendingScrollFilePath) {
      return;
    }

    const frameId = window.requestAnimationFrame(() => {
      const element = fileCardRefs.current[pendingScrollFilePath];
      if (!element) {
        return;
      }

      element.scrollIntoView({
        behavior: 'smooth',
        block: 'start'
      });
      setPendingScrollFilePath('');
    });

    return () => window.cancelAnimationFrame(frameId);
  }, [expandedFilePath, pendingScrollFilePath]);

  async function handlePublish(commentId: string): Promise<void> {
    setBusy((current) => ({ ...current, [commentId]: true }));
    setErrors((current) => ({ ...current, [commentId]: '' }));

    try {
      await onPublishInlineComment(commentId);
    } catch (error) {
      setErrors((current) => ({
        ...current,
        [commentId]: error instanceof Error ? error.message : String(error)
      }));
    } finally {
      setBusy((current) => ({ ...current, [commentId]: false }));
    }
  }

  async function handleAsk(commentId: string): Promise<void> {
    const message = drafts[commentId]?.trim();
    if (!message) {
      return;
    }

    setBusy((current) => ({ ...current, [commentId]: true }));
    setErrors((current) => ({ ...current, [commentId]: '' }));

    try {
      await onAskInlineQuestion(commentId, message);
      setDrafts((current) => ({ ...current, [commentId]: '' }));
    } catch (error) {
      setErrors((current) => ({
        ...current,
        [commentId]: error instanceof Error ? error.message : String(error)
      }));
    } finally {
      setBusy((current) => ({ ...current, [commentId]: false }));
    }
  }

  function toggleFile(filePath: string): void {
    setExpandedFilePath((current) => {
      const nextFilePath = current === filePath ? '' : filePath;

      if (nextFilePath) {
        expandFirstRemark(nextFilePath, filesWithRemarks, setExpandedRemarks);
        setPendingScrollFilePath(nextFilePath);
      } else {
        setPendingScrollFilePath('');
      }

      return nextFilePath;
    });
  }

  function toggleRemark(commentId: string): void {
    setExpandedRemarks((current) => ({
      ...current,
      [commentId]: !current[commentId]
    }));
  }

  function toggleContext(commentId: string): void {
    setExpandedContexts((current) => ({
      ...current,
      [commentId]: !current[commentId]
    }));
  }

  if (!filesWithRemarks.length) {
    return (
      <div className="empty-state">
        Файлы с замечаниями появятся после завершения chunk review и нормализации находок.
      </div>
    );
  }

  return (
    <div className="reviewed-files-accordion">
      <div className="subsection-header">
        <div>
          <h3>Файлы с замечаниями</h3>
          <p>{filesWithRemarks.length} файлов с замечаниями</p>
        </div>
      </div>

      <div className="file-accordion-list">
        {filesWithRemarks.map((file) => {
          const isOpen = expandedFilePath === file.filePath;
          const previewRemark = getPreviewRemark(file);
          const sortedThreads = sortThreads(file.inlineThreads);

          return (
            <article
              className={`file-accordion-card${isOpen ? ' open' : ''}`}
              key={file.filePath}
              ref={(element) => {
                fileCardRefs.current[file.filePath] = element;
              }}
            >
              <button className="file-accordion-toggle" onClick={() => toggleFile(file.filePath)} type="button">
                <div className="file-accordion-main">
                  <div className="file-accordion-title-row">
                    <strong>{file.displayName}</strong>
                    {getHighestSeverity(file) ? (
                      <span className={`severity severity-${getSeverityClass(getHighestSeverity(file))}`}>
                        {getSeverityLabel(getHighestSeverity(file))}
                      </span>
                    ) : null}
                  </div>
                  <p className="file-accordion-path">{file.filePath}</p>
                  {previewRemark ? (
                    <p className="file-accordion-preview">
                      {getSeverityLabel(previewRemark.severity)}: {previewRemark.title}
                    </p>
                  ) : null}
                </div>

                <div className="file-accordion-side">
                  <div className="file-list-meta">
                    <small>
                      {translateChangeType(file.changeType)} · <span className="added-count">+{file.addedLines}</span>{' '}
                      <span className="deleted-count">-{file.deletedLines}</span>
                    </small>
                    <small className="thread-count-label">{file.inlineThreads.length} замечаний</small>
                  </div>
                  <span className="file-accordion-caret">{isOpen ? '−' : '+'}</span>
                </div>
              </button>

              {isOpen ? (
                <div className="file-accordion-body">
                  <div className="file-review-header">
                    <div>
                      <h3>{file.displayName}</h3>
                      <p>{file.filePath}</p>
                    </div>
                    <div className="file-stats">
                      <span className="secondary-chip">{translateChangeType(file.changeType)}</span>
                      <span className="secondary-chip added">+{file.addedLines}</span>
                      <span className="secondary-chip deleted">-{file.deletedLines}</span>
                      <span className="secondary-chip">{sortedThreads.length} замечаний</span>
                    </div>
                  </div>

                  <div className="thread-list">
                    {sortedThreads.map((thread) => (
                      <InlineThreadCard
                        busy={!!busy[thread.id]}
                        contextExpanded={!!expandedContexts[thread.id]}
                        draft={drafts[thread.id] ?? ''}
                        error={errors[thread.id] ?? ''}
                        expanded={!!expandedRemarks[thread.id]}
                        key={thread.id}
                        onDraftChange={(value) =>
                          setDrafts((current) => ({
                            ...current,
                            [thread.id]: value
                          }))
                        }
                        onPublish={() => void handlePublish(thread.id)}
                        onSendQuestion={() => void handleAsk(thread.id)}
                        onToggleContext={() => toggleContext(thread.id)}
                        onToggleRemark={() => toggleRemark(thread.id)}
                        thread={thread}
                      />
                    ))}
                  </div>
                </div>
              ) : null}
            </article>
          );
        })}
      </div>
    </div>
  );
}

interface InlineThreadCardProps {
  thread: InlineComment;
  draft: string;
  error: string;
  busy: boolean;
  expanded: boolean;
  contextExpanded: boolean;
  onDraftChange: (value: string) => void;
  onPublish: () => void;
  onSendQuestion: () => void;
  onToggleRemark: () => void;
  onToggleContext: () => void;
}

function InlineThreadCard({
  thread,
  draft,
  error,
  busy,
  expanded,
  contextExpanded,
  onDraftChange,
  onPublish,
  onSendQuestion,
  onToggleRemark,
  onToggleContext
}: InlineThreadCardProps) {
  const hasContext = Boolean(
    thread.contextBlock?.trim() ||
      thread.existingCode?.trim() ||
      thread.suggestion?.trim()
  );

  return (
    <article className={`inline-thread-card simplified accordion${expanded ? ' open' : ''}`}>
      <button className="thread-accordion-toggle" onClick={onToggleRemark} type="button">
        <div className="thread-accordion-title">
          <span className={`severity severity-${getSeverityClass(thread.severity)}`}>
            {getSeverityLabel(thread.severity)}
          </span>
          <strong>{thread.title}</strong>
        </div>
        <div className="thread-meta">
          <span className="secondary-chip">
            {thread.lineNumber > 0 ? `Строка ${thread.lineNumber}` : 'Уровень файла'}
          </span>
          {thread.publishedToTfs ? <span className="secondary-chip success">Отправлено в TFS</span> : null}
          <span className="thread-accordion-caret">{expanded ? '−' : '+'}</span>
        </div>
      </button>

      {expanded ? (
        <div className="thread-accordion-body">
          <MarkdownBlock content={thread.content} emptyText="" />

          {hasContext ? (
            <div className="thread-context-actions">
              <button className="secondary-button" onClick={onToggleContext} type="button">
                {contextExpanded ? 'Скрыть контекст' : 'Показать контекст'}
              </button>
            </div>
          ) : null}

          {contextExpanded ? (
            <div className="thread-context-block">
              {thread.contextBlock ? (
                <div className="thread-snippet">
                  <p>
                    Контекст блока
                    {thread.contextStartLine > 0 && thread.contextEndLine > 0
                      ? ` (${thread.contextStartLine}-${thread.contextEndLine})`
                      : ''}
                  </p>
                  <pre>{thread.contextBlock}</pre>
                </div>
              ) : null}

              {thread.existingCode ? (
                <div className="thread-snippet">
                  <p>Проблемный фрагмент</p>
                  <pre>{thread.existingCode}</pre>
                </div>
              ) : null}

              {thread.suggestion ? (
                <div className="thread-snippet">
                  <p>Предлагаемое исправление</p>
                  <pre>{thread.suggestion}</pre>
                </div>
              ) : null}
            </div>
          ) : null}

          <div className="thread-messages">
            {thread.messages.map((message, index) => (
              <div className={`thread-message ${message.role}`} key={`${thread.id}-${index}`}>
                <strong>{message.role === 'assistant' ? 'LLM' : 'Вы'}</strong>
                <ThreadMessageBody message={message} />
              </div>
            ))}
          </div>

          <div className="thread-actions">
            <button
              className="secondary-button"
              disabled={thread.publishedToTfs || busy}
              onClick={onPublish}
              type="button"
            >
              {thread.publishedToTfs ? 'Отправлено в TFS' : 'Отправить в TFS'}
            </button>
          </div>

          <label className="thread-chat-box">
            <span>Продолжить диалог по замечанию</span>
            <textarea
              onChange={(event) => onDraftChange(event.target.value)}
              placeholder="Уточни контекст, спроси про исправление или попроси аргументацию..."
              rows={3}
              value={draft}
            />
          </label>

          <div className="thread-actions">
            <button className="primary" disabled={busy || !draft.trim()} onClick={onSendQuestion} type="button">
              {busy ? 'Думаю...' : 'Спросить LLM'}
            </button>
          </div>

          {error ? <div className="thread-error">{error}</div> : null}
        </div>
      ) : (
        <div className="thread-accordion-preview">
          <MarkdownBlock content={thread.content} emptyText="" />
        </div>
      )}

      {error && !expanded ? <div className="thread-error">{error}</div> : null}
    </article>
  );
}

function ThreadMessageBody({ message }: { message: ReviewCommentMessage }) {
  if (message.role === 'assistant' && message.structuredContent) {
    return <StructuredInlineDiscussion content={message.structuredContent} />;
  }

  return <MarkdownBlock content={message.content} emptyText="" />;
}

function StructuredInlineDiscussion({ content }: { content: InlineDiscussionStructuredContent }) {
  const hasPublishDecision =
    typeof content.shouldPublishToTfs === 'boolean' || Boolean(content.publishToTfsReason?.trim());

  return (
    <div className="structured-discussion">
      {content.summary?.trim() ? (
        <section className="structured-discussion-section">
          <h4>Пояснение</h4>
          <p>{content.summary}</p>
        </section>
      ) : null}

      {content.problems?.length ? (
        <section className="structured-discussion-section">
          <h4>Проблема</h4>
          <ol>
            {content.problems.map((problem, index) => (
              <li key={`problem-${index}`}>{problem}</li>
            ))}
          </ol>
        </section>
      ) : null}

      {content.risk?.trim() ? (
        <section className="structured-discussion-section">
          <h4>Риск</h4>
          <p>{content.risk}</p>
        </section>
      ) : null}

      {content.recommendations?.length ? (
        <section className="structured-discussion-section">
          <h4>Рекомендации</h4>
          <ol>
            {content.recommendations.map((recommendation, index) => (
              <li key={`recommendation-${index}`}>{recommendation}</li>
            ))}
          </ol>
        </section>
      ) : null}

      {hasPublishDecision ? (
        <section className="structured-discussion-section">
          <h4>Публикация в TFS</h4>
          {typeof content.shouldPublishToTfs === 'boolean' ? (
            <p>{content.shouldPublishToTfs ? 'Да' : 'Нет'}</p>
          ) : null}
          {content.publishToTfsReason?.trim() ? <p>{content.publishToTfsReason}</p> : null}
        </section>
      ) : null}

      {content.exampleCode?.trim() ? (
        <section className="structured-discussion-section">
          <h4>Пример кода</h4>
          <pre>
            <code>{content.exampleCode}</code>
          </pre>
        </section>
      ) : null}
    </div>
  );
}

function getHighestSeverity(file: ReviewedFile): string | undefined {
  return [...file.inlineThreads]
    .sort((left, right) => getSeverityRank(right.severity) - getSeverityRank(left.severity))[0]
    ?.severity;
}

function getPreviewRemark(file: ReviewedFile): InlineComment | undefined {
  return sortThreads(file.inlineThreads)[0];
}

function expandFirstRemark(
  filePath: string,
  filesWithRemarks: ReviewedFile[],
  setExpandedRemarks: React.Dispatch<React.SetStateAction<Record<string, boolean>>>
): void {
  const file = filesWithRemarks.find((item) => item.filePath === filePath);
  if (!file) {
    return;
  }

  const firstRemark = sortThreads(file.inlineThreads)[0];
  if (!firstRemark) {
    return;
  }

  setExpandedRemarks((current) =>
    current[firstRemark.id]
      ? current
      : {
          ...current,
          [firstRemark.id]: true
        });
}

function sortThreads(threads: InlineComment[]): InlineComment[] {
  return [...threads].sort((left, right) => {
    const severityDelta = getSeverityRank(right.severity) - getSeverityRank(left.severity);
    if (severityDelta !== 0) {
      return severityDelta;
    }

    const leftLine = left.lineNumber > 0 ? left.lineNumber : Number.MAX_SAFE_INTEGER;
    const rightLine = right.lineNumber > 0 ? right.lineNumber : Number.MAX_SAFE_INTEGER;
    return leftLine - rightLine;
  });
}

function getFileSeverityRank(file: ReviewedFile): number {
  return Math.max(0, ...file.inlineThreads.map((thread) => getSeverityRank(thread.severity)));
}

function getSevereThreadCount(file: ReviewedFile): number {
  return file.inlineThreads.filter((thread) => getSeverityRank(thread.severity) >= 3).length;
}

function getSeverityRank(value: string | undefined): number {
  switch (value?.trim().toLowerCase()) {
    case 'critical':
      return 4;
    case 'high':
      return 3;
    case 'medium':
      return 2;
    case 'low':
      return 1;
    default:
      return 0;
  }
}

function getSeverityLabel(value: string | undefined): string {
  if (typeof value !== 'string' || !value.trim()) {
    return 'Инфо';
  }

  switch (value.trim().toLowerCase()) {
    case 'critical':
      return 'Критично';
    case 'high':
      return 'Высокий';
    case 'medium':
      return 'Средний';
    case 'low':
      return 'Низкий';
    default:
      return value;
  }
}

function getSeverityClass(value: string | undefined): string {
  switch (value?.trim().toLowerCase()) {
    case 'critical':
      return 'critical';
    case 'high':
      return 'high';
    case 'medium':
      return 'medium';
    case 'low':
      return 'low';
    default:
      return 'low';
  }
}

function translateChangeType(value: string): string {
  switch (value.trim().toLowerCase()) {
    case 'added':
      return 'Добавлен';
    case 'modified':
      return 'Изменён';
    case 'deleted':
      return 'Удалён';
    case 'renamed':
      return 'Переименован';
    default:
      return value;
  }
}
