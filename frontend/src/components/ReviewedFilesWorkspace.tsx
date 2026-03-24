import { useEffect, useMemo, useState } from 'react';
import type { InlineComment, ReviewedFile } from '../lib/types';
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
  const [selectedPath, setSelectedPath] = useState<string>('');
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState<Record<string, boolean>>({});
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [expandedContexts, setExpandedContexts] = useState<Record<string, boolean>>({});

  const filesWithRemarks = useMemo(
    () =>
      [...files]
        .filter((file) => (file.inlineThreads?.length ?? 0) > 0)
        .sort((left, right) => {
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
      setSelectedPath('');
      return;
    }

    if (!selectedPath || !filesWithRemarks.some((file) => file.filePath === selectedPath)) {
      setSelectedPath(filesWithRemarks[0].filePath);
    }
  }, [filesWithRemarks, selectedPath]);

  const selectedFile =
    filesWithRemarks.find((file) => file.filePath === selectedPath) ?? filesWithRemarks[0] ?? null;
  const selectedFileThreads = [...(selectedFile?.inlineThreads ?? [])].sort(
    (left, right) => left.lineNumber - right.lineNumber
  );

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

  function toggleContext(commentId: string): void {
    setExpandedContexts((current) => ({
      ...current,
      [commentId]: !current[commentId]
    }));
  }

  if (!filesWithRemarks.length || !selectedFile) {
    return (
      <div className="empty-state">
        Файлы с замечаниями появятся после завершения chunk review и нормализации находок.
      </div>
    );
  }

  return (
    <div className="reviewed-files-layout simplified">
      <aside className="file-sidebar">
        <div className="subsection-header">
          <div>
            <h3>Файлы с замечаниями</h3>
            <p>{filesWithRemarks.length} files with remarks</p>
          </div>
        </div>

        <div className="file-sidebar-list">
          {filesWithRemarks.map((file) => (
            <button
              className={`file-nav-item${file.filePath === selectedFile.filePath ? ' active' : ''}`}
              key={file.filePath}
              onClick={() => setSelectedPath(file.filePath)}
              type="button"
            >
              <strong>{file.displayName}</strong>
              <span>{file.filePath}</span>
              <small>
                {file.changeType} · <span className="added-count">+{file.addedLines}</span>{' '}
                <span className="deleted-count">-{file.deletedLines}</span>
              </small>
              <small className="thread-count-label">{file.inlineThreads.length} remarks</small>
            </button>
          ))}
        </div>
      </aside>

      <section className="file-review-surface simplified">
        <div className="file-review-header">
          <div>
            <h3>{selectedFile.displayName}</h3>
            <p>{selectedFile.filePath}</p>
          </div>
          <div className="file-stats">
            <span className="secondary-chip">{selectedFile.changeType}</span>
            <span className="secondary-chip added">+{selectedFile.addedLines}</span>
            <span className="secondary-chip deleted">-{selectedFile.deletedLines}</span>
            <span className="secondary-chip">{selectedFileThreads.length} remarks</span>
          </div>
        </div>

        <div className="thread-list">
          {selectedFileThreads.map((thread) => (
            <InlineThreadCard
              busy={!!busy[thread.id]}
              draft={drafts[thread.id] ?? ''}
              error={errors[thread.id] ?? ''}
              expanded={!!expandedContexts[thread.id]}
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
              thread={thread}
            />
          ))}
        </div>
      </section>
    </div>
  );
}

interface InlineThreadCardProps {
  thread: InlineComment;
  draft: string;
  error: string;
  busy: boolean;
  expanded: boolean;
  onDraftChange: (value: string) => void;
  onPublish: () => void;
  onSendQuestion: () => void;
  onToggleContext: () => void;
}

function InlineThreadCard({
  thread,
  draft,
  error,
  busy,
  expanded,
  onDraftChange,
  onPublish,
  onSendQuestion,
  onToggleContext
}: InlineThreadCardProps) {
  const hasContext = Boolean(
    thread.contextBlock?.trim() ||
      thread.relevantDiffHunk?.trim() ||
      thread.existingCode?.trim() ||
      thread.suggestion?.trim()
  );

  return (
    <article className="inline-thread-card simplified">
      <header>
        <div>
          <span className={`severity severity-${getSeverityClass(thread.severity)}`}>
            {getSeverityLabel(thread.severity)}
          </span>
          <strong>{thread.title}</strong>
        </div>
        <div className="thread-meta">
          <span className="secondary-chip">
            {thread.lineNumber > 0 ? `Line ${thread.lineNumber}` : 'File-level'}
          </span>
          {thread.publishedToTfs ? <span className="secondary-chip success">Sent to TFS</span> : null}
        </div>
      </header>

      <MarkdownBlock content={thread.content} emptyText="" />

      {hasContext ? (
        <div className="thread-context-actions">
          <button className="secondary-button" onClick={onToggleContext} type="button">
            {expanded ? 'Скрыть контекст' : 'Показать контекст'}
          </button>
        </div>
      ) : null}

      {expanded ? (
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

          {thread.relevantDiffHunk ? (
            <div className="thread-snippet">
              <p>Relevant diff hunk</p>
              <pre>{thread.relevantDiffHunk}</pre>
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
            <strong>{message.role === 'assistant' ? 'LLM' : 'You'}</strong>
            <MarkdownBlock content={message.content} emptyText="" />
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
          {thread.publishedToTfs ? 'Sent to TFS' : 'Send to TFS'}
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
          {busy ? 'Thinking...' : 'Ask LLM'}
        </button>
      </div>

      {error ? <div className="thread-error">{error}</div> : null}
    </article>
  );
}

function getSeverityLabel(value: string | undefined): string {
  return typeof value === 'string' && value.trim() ? value : 'Info';
}

function getSeverityClass(value: string | undefined): string {
  return getSeverityLabel(value).toLowerCase();
}
