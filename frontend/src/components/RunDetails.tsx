import { useEffect, useState } from 'react';
import { MarkdownBlock } from './MarkdownBlock';
import { MermaidDiagram } from './MermaidDiagram';
import { ReviewedFilesWorkspace, ThreadMessageBody } from './ReviewedFilesWorkspace';
import type { ChangeDescriptionStructuredContent, ReviewRun } from '../lib/types';

interface RunDetailsProps {
  run: ReviewRun | null;
  diffDownloadUrl?: string;
  reportDownloadUrl?: string;
  onPublishReport: () => Promise<void>;
  onPublishInlineComment: (commentId: string) => Promise<void>;
  onSetInlineCommentRelevance: (commentId: string, isRelevant: boolean) => Promise<void>;
  onAskInlineQuestion: (commentId: string, message: string) => Promise<void>;
  onAskReviewQuestion: (message: string) => Promise<void>;
}

export function RunDetails({
  run,
  diffDownloadUrl,
  reportDownloadUrl,
  onPublishReport,
  onPublishInlineComment,
  onSetInlineCommentRelevance,
  onAskInlineQuestion,
  onAskReviewQuestion
}: RunDetailsProps) {
  const [isDiagramCollapsed, setIsDiagramCollapsed] = useState(true);
  const [isDescriptionCollapsed, setIsDescriptionCollapsed] = useState(true);
  const [isDiscussionCollapsed, setIsDiscussionCollapsed] = useState(true);
  const [isReportCollapsed, setIsReportCollapsed] = useState(true);
  const [discussionDraft, setDiscussionDraft] = useState('');
  const [discussionBusy, setDiscussionBusy] = useState(false);
  const [discussionError, setDiscussionError] = useState('');
  const publishTargetLabel = getPublishTargetLabel(run?.pullRequestUrl);

  useEffect(() => {
    setIsDiagramCollapsed(true);
    setIsDescriptionCollapsed(true);
    setIsDiscussionCollapsed(true);
    setIsReportCollapsed(true);
    setDiscussionDraft('');
    setDiscussionBusy(false);
    setDiscussionError('');
  }, [run?.id]);

  async function handleAskReviewQuestion(): Promise<void> {
    const message = discussionDraft.trim();
    if (!message) {
      return;
    }

    setDiscussionBusy(true);
    setDiscussionError('');
    try {
      await onAskReviewQuestion(message);
      setDiscussionDraft('');
    } catch (error) {
      setDiscussionError(error instanceof Error ? error.message : String(error));
    } finally {
      setDiscussionBusy(false);
    }
  }

  if (!run) {
    return (
      <section className="panel">
        <div className="panel-header">
          <div>
            <p className="eyebrow">Результаты</p>
            <h2>Диаграмма, описание и замечания по файлам</h2>
          </div>
        </div>
        <div className="empty-state">Запусти ревью, чтобы увидеть диаграмму изменений, описание и замечания по файлам.</div>
      </section>
    );
  }

  return (
    <section className="panel">
      <div className="panel-header">
        <div>
          <p className="eyebrow">Результаты</p>
          <h2>
            {run.targetKind === 'PullRequest' && run.pullRequestUrl ? (
              <a className="result-title-link" href={run.pullRequestUrl} target="_blank" rel="noopener noreferrer">
                {run.title}
              </a>
            ) : (
              run.title
            )}
          </h2>
        </div>
        <div className="result-toolbar">
          <button
            className="secondary-button"
            disabled={!run.hasDiffArtifact || !diffDownloadUrl}
            onClick={() => {
              if (diffDownloadUrl) {
                window.open(diffDownloadUrl, '_blank', 'noopener,noreferrer');
              }
            }}
            type="button"
          >
              Скачать diff.txt
          </button>
          <button
            className="secondary-button"
            disabled={!run.hasMarkdownReportArtifact || !reportDownloadUrl}
            onClick={() => {
              if (reportDownloadUrl) {
                window.open(reportDownloadUrl, '_blank', 'noopener,noreferrer');
              }
            }}
            type="button"
          >
              Скачать report.md
          </button>
          <span className="secondary-chip">{run.findings.length} замечаний</span>
        </div>
      </div>

      <div className="results-stack">
        <article className="result-card">
          <div className="subsection-header">
            <h3>Диаграмма изменений</h3>
            <div className="result-toolbar">
              <button
                aria-expanded={!isDiagramCollapsed}
                className="secondary-button"
                disabled={!run.changeDiagramMermaid}
                onClick={() => setIsDiagramCollapsed((value) => !value)}
                type="button"
              >
                {isDiagramCollapsed ? 'Развернуть диаграмму' : 'Свернуть диаграмму'}
              </button>
            </div>
          </div>
          {!isDiagramCollapsed ? (
            run.changeDiagramMermaid ? (
              <div className="diagram-card large">
                <MermaidDiagram chart={run.changeDiagramMermaid} />
              </div>
            ) : (
              <div className="empty-state">Диаграмма появится здесь, как только этап описания изменений её вернёт.</div>
            )
          ) : null}
        </article>

        <article className="result-card">
          <div className="subsection-header">
            <h3>Описание изменений</h3>
            <div className="result-toolbar">
              <button
                aria-expanded={!isDescriptionCollapsed}
                className="secondary-button"
                disabled={!run.changeDescriptionStructured && !run.changeDescription}
                onClick={() => setIsDescriptionCollapsed((value) => !value)}
                type="button"
              >
                {isDescriptionCollapsed ? 'Развернуть описание' : 'Свернуть описание'}
              </button>
            </div>
          </div>
          {!isDescriptionCollapsed ? (
            <ChangeDescriptionBlock
              content={run.changeDescriptionStructured}
              fallback={run.changeDescription}
            />
          ) : null}
        </article>
      </div>

      <div className="subsection">
        <h3>Замечания по файлам</h3>
        <ReviewedFilesWorkspace
          files={run.reviewedFiles}
          publishTargetLabel={publishTargetLabel}
          onAskInlineQuestion={onAskInlineQuestion}
          onPublishInlineComment={onPublishInlineComment}
          onSetInlineCommentRelevance={onSetInlineCommentRelevance}
        />
      </div>

      <div className="subsection">
        <div className="subsection-header">
          <div>
            <h3>Спросить LLM по ревью</h3>
            <p>Вопрос будет задан по уже подготовленному diff, описанию изменений, диаграмме и текущим замечаниям.</p>
          </div>
          <div className="result-toolbar">
            <button
              aria-expanded={!isDiscussionCollapsed}
              className="secondary-button"
              onClick={() => setIsDiscussionCollapsed((value) => !value)}
              type="button"
            >
              {isDiscussionCollapsed ? 'Развернуть диалог' : 'Свернуть диалог'}
            </button>
          </div>
        </div>
        {!isDiscussionCollapsed ? (
          <article className="result-card">
            {run.reviewDiscussionMessages.length > 0 ? (
              <div className="thread-messages">
                {run.reviewDiscussionMessages.map((message, index) => (
                  <div className={`thread-message ${message.role}`} key={`review-discussion-${index}`}>
                    <strong>{message.role === 'assistant' ? 'LLM' : 'Вы'}</strong>
                    <ThreadMessageBody message={message} />
                  </div>
                ))}
              </div>
            ) : (
              <div className="empty-state compact">
                Здесь можно задать уточняющий вопрос по ревью. Если LLM найдёт новые дефекты или риски, они будут добавлены в список замечаний.
              </div>
            )}

            <label className="thread-chat-box review-chat-box">
              <span>Вопрос по ревью</span>
              <textarea
                onChange={(event) => setDiscussionDraft(event.target.value)}
                placeholder="Например: проверь ещё раз безопасность, найди дополнительные race condition или оцени риски null-handling..."
                rows={4}
                value={discussionDraft}
              />
            </label>

            <div className="thread-actions">
              <button
                className="primary"
                disabled={discussionBusy || !discussionDraft.trim()}
                onClick={() => void handleAskReviewQuestion()}
                type="button"
              >
                {discussionBusy ? 'Думаю...' : 'Спросить LLM по ревью'}
              </button>
            </div>

            {discussionError ? <div className="thread-error">{discussionError}</div> : null}
          </article>
        ) : null}
      </div>

      <div className="subsection">
        <div className="subsection-header">
          <h3>Итоговый отчёт</h3>
          <div className="result-toolbar">
            <button
              aria-expanded={!isReportCollapsed}
              className="secondary-button"
              disabled={!run.hasMarkdownReportArtifact}
              onClick={() => setIsReportCollapsed((value) => !value)}
              type="button"
            >
              {isReportCollapsed ? 'Развернуть отчёт' : 'Свернуть отчёт'}
            </button>
            {run.targetKind === 'PullRequest' ? (
              <button
                className="secondary-button"
                disabled={!run.hasMarkdownReportArtifact || run.publishSucceeded}
                onClick={() => void onPublishReport()}
                type="button"
              >
                {run.publishSucceeded
                  ? `Отправлено в ${publishTargetLabel}`
                  : `Отправить отчёт в ${publishTargetLabel}`}
              </button>
            ) : null}
          </div>
        </div>
        {!isReportCollapsed ? (
          <article className="result-card report-card">
            <MarkdownBlock
              content={run.markdownReport}
              emptyText="Итоговый отчёт появится здесь после завершения синтеза."
              normalize={false}
            />
          </article>
        ) : null}
      </div>
    </section>
  );
}

function getPublishTargetLabel(pullRequestUrl?: string): string {
  if (!pullRequestUrl) {
    return 'pull request';
  }

  if (/\/merge_requests\//i.test(pullRequestUrl)) {
    return 'GitLab';
  }

  return /\/pull\//i.test(pullRequestUrl) ? 'GitHub' : 'TFS';
}

function ChangeDescriptionBlock({
  content,
  fallback
}: {
  content?: ChangeDescriptionStructuredContent;
  fallback: string;
}) {
  if (content) {
    return (
      <div className="structured-change-description">
        {content.category ? (
          <section className="structured-change-section">
            <h4>Категория</h4>
            <p>{content.category}</p>
          </section>
        ) : null}

        {typeof content.estimatedReviewEffort === 'number' ? (
          <section className="structured-change-section">
            <h4>Сложность ревью</h4>
            <p>{renderReviewEffort(content.estimatedReviewEffort)}</p>
          </section>
        ) : null}

        {typeof content.qualityScore === 'number' ? (
          <section className="structured-change-section">
            <h4>Оценка качества PR</h4>
            <p>{content.qualityScore}/100</p>
          </section>
        ) : null}

        {content.summary ? (
          <section className="structured-change-section">
            <h4>Краткое описание</h4>
            <p>{content.summary}</p>
          </section>
        ) : null}

        {content.impactedModules?.length ? (
          <section className="structured-change-section">
            <h4>Затронутые модули</h4>
            <ul>
              {content.impactedModules.map((module, index) => (
                <li key={`module-${index}`}>{module}</li>
              ))}
            </ul>
          </section>
        ) : null}

        {content.risks?.length ? (
          <section className="structured-change-section">
            <h4>Риски и точки внимания</h4>
            <ul>
              {content.risks.map((risk, index) => (
                <li key={`risk-${index}`}>{risk}</li>
              ))}
            </ul>
          </section>
        ) : null}
      </div>
    );
  }

  return (
    <MarkdownBlock
      content={fallback}
      emptyText="Описание появится здесь после завершения первого этапа."
    />
  );
}

function renderReviewEffort(value: number): string {
  const normalized = Math.max(1, Math.min(5, value));
  return `${normalized}/5 ${'●'.repeat(normalized)}${'○'.repeat(5 - normalized)}`;
}
