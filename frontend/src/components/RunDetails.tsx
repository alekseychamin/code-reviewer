import { useEffect, useState } from 'react';
import { MarkdownBlock } from './MarkdownBlock';
import { MermaidDiagram } from './MermaidDiagram';
import { ReviewedFilesWorkspace, ThreadMessageBody } from './ReviewedFilesWorkspace';
import type {
  ChangeDescriptionStructuredContent,
  ExternalReview,
  InlineDiscussionOpportunityItem,
  ReviewOpportunityItem,
  ReviewRun
} from '../lib/types';

interface RunDetailsProps {
  run: ReviewRun | null;
  diffDownloadUrl?: string;
  reportDownloadUrl?: string;
  onPublishReport: () => Promise<void>;
  onPublishInlineComment: (commentId: string) => Promise<void>;
  onRegenerateArtifacts: (regenerateDescription: boolean, regenerateDiagram: boolean) => Promise<void>;
  onStopReviewRun: (runId: string) => Promise<void>;
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
  onRegenerateArtifacts,
  onStopReviewRun,
  onSetInlineCommentRelevance,
  onAskInlineQuestion,
  onAskReviewQuestion
}: RunDetailsProps) {
  const [isDiagramCollapsed, setIsDiagramCollapsed] = useState(true);
  const [isDescriptionCollapsed, setIsDescriptionCollapsed] = useState(true);
  const [isSemanticContextCollapsed, setIsSemanticContextCollapsed] = useState(true);
  const [isDiscussionCollapsed, setIsDiscussionCollapsed] = useState(true);
  const [isPrimaryOpportunitiesCollapsed, setIsPrimaryOpportunitiesCollapsed] = useState(true);
  const [isOpportunitiesCollapsed, setIsOpportunitiesCollapsed] = useState(true);
  const [isExternalReviewCollapsed, setIsExternalReviewCollapsed] = useState(true);
  const [isReportCollapsed, setIsReportCollapsed] = useState(true);
  const [discussionDraft, setDiscussionDraft] = useState('');
  const [discussionBusy, setDiscussionBusy] = useState(false);
  const [discussionError, setDiscussionError] = useState('');
  const [stopBusy, setStopBusy] = useState(false);
  const [artifactBusy, setArtifactBusy] = useState<'description' | null>(null);
  const publishTargetLabel = getPublishTargetLabel(run?.pullRequestUrl);
  const primaryOpportunities = run ? collectPrimaryOpportunities(run) : [];
  const followUpOpportunities = run ? collectFollowUpOpportunities(run) : [];
  const hasExternalReview = Boolean(
    run?.externalReview?.enabled ||
    run?.externalReview?.attempted ||
    run?.externalReview?.commands?.length
  );
  const isReviewRunning = run?.status === 'Running' || run?.status === 'Pending';
  const canRegenerateArtifacts = Boolean(run?.hasDiffArtifact) && !isReviewRunning;

  useEffect(() => {
    setIsDiagramCollapsed(true);
    setIsDescriptionCollapsed(true);
    setIsSemanticContextCollapsed(true);
    setIsDiscussionCollapsed(true);
    setIsPrimaryOpportunitiesCollapsed(true);
    setIsOpportunitiesCollapsed(true);
    setIsExternalReviewCollapsed(true);
    setIsReportCollapsed(true);
    setDiscussionDraft('');
    setDiscussionBusy(false);
    setDiscussionError('');
    setStopBusy(false);
    setArtifactBusy(null);
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

  async function handleStopReview(): Promise<void> {
    if (!run || !window.confirm('Остановить выбранное ревью?')) {
      return;
    }

    setStopBusy(true);
    try {
      await onStopReviewRun(run.id);
    } finally {
      setStopBusy(false);
    }
  }

  async function handleRegenerateDescriptionAndDiagram(): Promise<void> {
    setArtifactBusy('description');
    try {
      // Backend: RegenerateDescription runs GenerateChangeSummaryForRunAsync, which returns
      // description + diagram in one JSON — more reliable than RegenerateDiagram-only path.
      await onRegenerateArtifacts(true, false);
    } finally {
      setArtifactBusy(null);
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
          {run.status === 'Running' || run.status === 'Pending' ? (
            <button
              className="secondary-button danger-button"
              disabled={stopBusy}
              onClick={() => void handleStopReview()}
              type="button"
            >
              {stopBusy ? 'Останавливаем...' : 'Остановить ревью'}
            </button>
          ) : null}
          <span className="secondary-chip">{run.findings.length} замечаний</span>
        </div>
      </div>

      <div className="results-stack">
        <article className="result-card">
          <div className="subsection-header">
            <div>
              <h3>Контекст модели</h3>
              <p>{getSemanticContextSummary(run)}</p>
            </div>
            <div className="result-toolbar">
              <span className={`secondary-chip ${run.semanticCodeContext?.succeeded ? 'success' : 'muted'}`}>
                {getSemanticContextStatusLabel(run.semanticCodeContext?.status)}
              </span>
              <button
                aria-expanded={!isSemanticContextCollapsed}
                className="secondary-button"
                disabled={!run.semanticCodeContext?.enabled && !run.semanticCodeContext?.attempted}
                onClick={() => setIsSemanticContextCollapsed((value) => !value)}
                type="button"
              >
                {isSemanticContextCollapsed ? 'Развернуть контекст' : 'Свернуть контекст'}
              </button>
            </div>
          </div>
          {!isSemanticContextCollapsed ? <SemanticCodeContextBlock run={run} /> : null}
        </article>

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
              <button
                className="secondary-button"
                disabled={artifactBusy !== null || !canRegenerateArtifacts}
                onClick={() => void handleRegenerateDescriptionAndDiagram()}
                type="button"
                title={
                  isReviewRunning
                    ? 'Дождитесь завершения ревью.'
                    : 'Пересоздаёт диаграмму и описание из одного ответа LLM (этап описания изменений).'
                }
              >
                {artifactBusy === 'description' ? 'Обновляем...' : 'Пересоздать диаграмму и описание'}
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

      {hasExternalReview && run.externalReview ? (
        <div className="subsection">
          <div className="subsection-header">
            <div>
              <h3>{run.externalReview.engineName || 'Внешний ревьюер'}</h3>
              <p>Быстрый sidecar-обзор, который не смешивается автоматически с основными findings.</p>
            </div>
            <div className="result-toolbar">
              <span className={`external-review-status ${run.externalReview.status || 'not_attempted'}`}>
                {formatExternalReviewStatus(run.externalReview)}
              </span>
              <button
                aria-expanded={!isExternalReviewCollapsed}
                className="secondary-button"
                disabled={!run.externalReview.commands.length}
                onClick={() => setIsExternalReviewCollapsed((value) => !value)}
                type="button"
              >
                {isExternalReviewCollapsed ? 'Развернуть PR-Agent' : 'Свернуть PR-Agent'}
              </button>
            </div>
          </div>
          {!isExternalReviewCollapsed ? <ExternalReviewBlock review={run.externalReview} /> : null}
        </div>
      ) : null}

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

      {primaryOpportunities.length ? (
        <div className="subsection">
          <div className="subsection-header">
            <div>
              <h3>Возможности для улучшения из первичного ревью</h3>
              <p>Полезные улучшения, которые не попали в дефекты и риски.</p>
            </div>
            <div className="result-toolbar">
              <button
                aria-expanded={!isPrimaryOpportunitiesCollapsed}
                className="secondary-button"
                onClick={() => setIsPrimaryOpportunitiesCollapsed((value) => !value)}
                type="button"
              >
                {isPrimaryOpportunitiesCollapsed ? 'Развернуть улучшения' : 'Свернуть улучшения'}
              </button>
            </div>
          </div>
          {!isPrimaryOpportunitiesCollapsed ? <OpportunitiesOverview items={primaryOpportunities} run={run} /> : null}
        </div>
      ) : null}

      {followUpOpportunities.length ? (
        <div className="subsection">
          <div className="subsection-header">
            <div>
              <h3>Дополнительные возможности для улучшения</h3>
              <p>Полезные улучшения из блока «Спросить LLM по ревью», не добавленные в дефекты.</p>
            </div>
            <div className="result-toolbar">
              <button
                aria-expanded={!isOpportunitiesCollapsed}
                className="secondary-button"
                onClick={() => setIsOpportunitiesCollapsed((value) => !value)}
                type="button"
              >
                {isOpportunitiesCollapsed ? 'Развернуть улучшения' : 'Свернуть улучшения'}
              </button>
            </div>
          </div>
          {!isOpportunitiesCollapsed ? <OpportunitiesOverview items={followUpOpportunities} run={run} /> : null}
        </div>
      ) : null}

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
                Здесь можно задать уточняющий вопрос по ревью. Если LLM найдёт новые дефекты или риски, они будут добавлены в список замечаний. Полезные улучшения без явного дефекта будут показаны отдельно.
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

function ExternalReviewBlock({ review }: { review: ExternalReview }) {
  const completedAt = review.completedAt ? formatTimestamp(review.completedAt) : '';

  return (
    <article className="result-card external-review-card">
      <div className="external-review-summary">
        <span>{formatExternalReviewMessage(review)}</span>
        {review.elapsedMilliseconds > 0 ? <span>{formatDuration(review.elapsedMilliseconds)}</span> : null}
        {completedAt ? <span>{completedAt}</span> : null}
      </div>

      {review.commands.length ? (
        <div className="external-review-command-list">
          {review.commands.map((command) => (
            <section className="external-review-command" key={command.command}>
              <div className="external-review-command-header">
                <h4>{formatExternalReviewCommandTitle(command.command)}</h4>
                <span className={command.succeeded ? 'command-ok' : 'command-failed'}>
                  {command.succeeded ? 'готов' : 'ошибка'}
                  {command.elapsedMilliseconds > 0 ? ` · ${formatDuration(command.elapsedMilliseconds)}` : ''}
                </span>
              </div>
              {command.errorMessage ? <div className="thread-error">{command.errorMessage}</div> : null}
              <MarkdownBlock
                content={command.artifact}
                emptyText="PR-Agent не вернул markdown для этой команды."
                normalize={false}
              />
            </section>
          ))}
        </div>
      ) : (
        <div className="empty-state compact">PR-Agent результат пока недоступен.</div>
      )}
    </article>
  );
}

function formatExternalReviewMessage(review: ExternalReview): string {
  if (!review.message) {
    return formatExternalReviewStatus(review);
  }

  switch (review.message) {
    case 'PR-Agent sidecar completed.':
      return 'PR-Agent завершил быстрый обзор.';
    case 'PR-Agent sidecar completed with command errors.':
      return 'PR-Agent завершил обзор, часть команд вернулась с ошибкой.';
    default:
      if (review.message.startsWith('PR-Agent sidecar timed out after ')) {
        return review.message.replace('PR-Agent sidecar timed out after ', 'PR-Agent не успел завершиться за ');
      }

      return review.message;
  }
}

function formatExternalReviewCommandTitle(command: string): string {
  switch (command.trim().replace(/^\/+/, '').toLowerCase()) {
    case 'describe':
      return 'Описание изменений';
    case 'review':
      return 'Обзор рисков';
    case 'improve':
      return 'Идеи улучшений';
    default:
      return `Команда ${command}`;
  }
}

function formatExternalReviewStatus(review: ExternalReview): string {
  if (!review.enabled) {
    return 'выключен';
  }

  if (review.timedOut || review.status === 'timed_out') {
    return 'timeout';
  }

  if (review.succeeded || review.status === 'ready') {
    return 'готов';
  }

  if (review.status === 'running') {
    return 'в работе';
  }

  if (review.attempted) {
    return review.status === 'failed' ? 'ошибка' : 'частично';
  }

  return 'ожидает';
}

function OpportunitiesOverview({ items, run }: { items: CollectedOpportunity[]; run: ReviewRun }) {
  const groups = buildOpportunityGroups(items, run);

  return (
    <div className="opportunity-groups">
      {groups.map((group, index) => (
        <article className="opportunity-file-card" key={`${group.file}-${index}`}>
          <div className="opportunity-file-header">
            <div>
              <p className="entity-number">Файл №{index + 1}</p>
              <h4>{group.displayName}</h4>
              <p>{group.file}</p>
            </div>
            <span className="secondary-chip muted">{group.items.length} улучшений</span>
          </div>
          <div className="opportunity-list">
            {group.items.map((item, itemIndex) => (
              <article className="opportunity-card" key={`${group.file}-${item.title}-${itemIndex}`}>
                <div className="opportunity-card-header">
                  <div className="thread-accordion-title">
                    <span className="entity-number">Улучшение №{index + 1}.{itemIndex + 1}</span>
                    <strong>{item.title || item.description}</strong>
                  </div>
                  <div className="thread-meta">
                    {item.startLine > 0 ? (
                      <span className="secondary-chip muted">Строка {item.startLine}</span>
                    ) : item.lineHint ? (
                      <span className="secondary-chip muted">{item.lineHint}</span>
                    ) : null}
                  </div>
                </div>
                {item.description ? <p className="opportunity-description">{item.description}</p> : null}
                {item.suggestion ? <p className="opportunity-description"><strong>Предложение:</strong> {item.suggestion}</p> : null}
                {item.contextSnippet ? (
                  <div className="thread-snippet opportunity-snippet">
                    <p>Контекст кода ({item.contextStartLine}-{item.contextEndLine})</p>
                    <pre>
                      <code>{item.contextSnippet}</code>
                    </pre>
                  </div>
                ) : null}
                {item.exampleCode ? (
                  <div className="thread-snippet opportunity-snippet">
                    <p>Вариант оптимизации</p>
                    <pre>
                      <code>{item.exampleCode}</code>
                    </pre>
                  </div>
                ) : null}
              </article>
            ))}
          </div>
        </article>
      ))}
    </div>
  );
}

function SemanticCodeContextBlock({ run }: { run: ReviewRun }) {
  const context = run.semanticCodeContext;
  if (!context) {
    return <div className="empty-state compact">Диагностика semantic context пока не сохранена для этого запуска.</div>;
  }

  const metrics = [
    { label: 'В промпте', value: `${context.snippetCount} снипп.` },
    { label: 'Кандидаты', value: context.candidateCount },
    { label: 'Поисковые запросы', value: context.queryCount },
    { label: 'Время сбора', value: formatDuration(context.elapsedMilliseconds) },
    { label: 'Source индекс', value: `${context.sourceFilesSelected} файлов / ${context.sourceChunksIndexed} чанков` },
    { label: 'Target индекс', value: `${context.targetFilesSelected} файлов / ${context.targetChunksIndexed} чанков` }
  ];
  const diagnostics = buildSemanticDiagnostics(context);
  const message = getReadableSemanticContextMessage(context.message);

  return (
    <div className="semantic-context">
      <div className="semantic-context-grid">
        {metrics.map((metric) => (
          <div className="semantic-context-metric" key={metric.label}>
            <span>{metric.label}</span>
            <strong>{metric.value}</strong>
          </div>
        ))}
      </div>

      <div className="semantic-context-flags">
        <span className={`secondary-chip ${context.cacheReuseEnabled ? 'success' : 'muted'}`}>
          cache reuse {context.cacheReuseEnabled ? 'включён' : 'выключен'}
        </span>
        <span className={`secondary-chip ${context.sourceCacheHit ? 'success' : 'muted'}`}>
          source {context.sourceCacheHit ? 'из кэша' : 'проиндексирован'}
        </span>
        {context.targetCommitSha ? (
          <span className={`secondary-chip ${context.targetCacheHit ? 'success' : 'muted'}`}>
            target {context.targetCacheHit ? 'из кэша' : 'проиндексирован'}
          </span>
        ) : null}
        {context.sourceCommitSha ? (
          <span className="secondary-chip muted">source {shortSha(context.sourceCommitSha)}</span>
        ) : null}
        {context.targetCommitSha ? (
          <span className="secondary-chip muted">target {shortSha(context.targetCommitSha)}</span>
        ) : null}
      </div>

      {diagnostics.length ? (
        <div className="semantic-context-diagnostics">
          {diagnostics.map((item) => (
            <span className={`secondary-chip ${item.level}`} key={item.label}>
              {item.label}: {item.value}
            </span>
          ))}
        </div>
      ) : null}

      {message ? <p className="semantic-context-message">{message}</p> : null}

      {context.snippets.length ? (
        <section className="semantic-context-snippets" aria-label="Сниппеты semantic context">
          <div className="semantic-context-snippets-header">
            <strong>Сниппеты, переданные модели</strong>
            <span>{context.snippets.length} из {context.candidateCount} кандидатов</span>
          </div>
          {context.snippets.map((snippet, index) => (
            <article className="semantic-snippet-row" key={`${snippet.revisionKind}-${snippet.filePath}-${snippet.startLine}-${index}`}>
              <div className="semantic-snippet-main">
                <strong title={snippet.filePath}>{getFileName(snippet.filePath)}</strong>
                <span title={snippet.filePath}>{getParentPath(snippet.filePath)}</span>
              </div>
              <div className="semantic-snippet-meta">
                <span className="secondary-chip muted">{getRevisionKindLabel(snippet.revisionKind)} {shortSha(snippet.commitSha)}</span>
                <span>строки {snippet.startLine}-{snippet.endLine}</span>
                <span>score {snippet.score.toFixed(3)}</span>
              </div>
              {snippet.query ? <p title={snippet.query}>{snippet.query}</p> : null}
            </article>
          ))}
        </section>
      ) : (
        <div className="empty-state compact">В промпт не попали дополнительные semantic snippets.</div>
      )}
    </div>
  );
}

function buildSemanticDiagnostics(context: ReviewRun['semanticCodeContext']): Array<{ label: string; value: number; level: 'success' | 'muted' | '' }> {
  return [
    { label: 'Added в target пропущены', value: context.targetFilesSkippedAdded, level: 'muted' as const },
    { label: 'Baseline target файлов', value: context.targetBaselineFilesSelected, level: 'muted' as const },
    { label: 'Deleted из source пропущены', value: context.sourceFilesSkippedDeleted, level: 'muted' as const },
    { label: 'Target не найдено', value: context.targetFilesMissing, level: '' as const },
    { label: 'Target слишком большие', value: context.targetFilesTooLarge, level: '' as const },
    { label: 'Target пустые', value: context.targetFilesEmpty, level: '' as const },
    { label: 'Target без чанков', value: context.targetFilesWithoutChunks, level: '' as const },
    { label: 'Target read failed', value: context.targetFilesReadFailed, level: '' as const },
    { label: 'Source read failed', value: context.sourceFilesReadFailed, level: '' as const }
  ].filter((item) => item.value > 0);
}

function getReadableSemanticContextMessage(message: string): string {
  if (!message) {
    return '';
  }

  let result = message
    .replace('Semantic code context was added to the review prompt.', 'Semantic context добавлен в промпт ревью.')
    .replace('Semantic search completed but returned no snippets.', 'Semantic search завершился, но не нашёл сниппеты для промпта.')
    .replace(/Target changed-file context skipped (\d+) added files because they do not exist at the target revision\./, 'Target-контекст пропустил $1 новых файлов: их ещё нет в target-ветке.')
    .replace(/Target baseline context used (\d+) files from the target revision\./, 'Для target дополнительно взяли $1 baseline-файлов.')
    .replace('Target baseline context was not needed.', 'Baseline target-контекст не понадобился.')
    .replace('Target baseline context had no readable baseline files.', 'Для target не нашлось читаемых baseline-файлов.');

  return result.trim();
}

function getFileName(filePath: string): string {
  const normalized = filePath.replace(/\\/g, '/');
  const parts = normalized.split('/').filter(Boolean);
  return parts[parts.length - 1] || filePath;
}

function getParentPath(filePath: string): string {
  const normalized = filePath.replace(/\\/g, '/');
  const parts = normalized.split('/').filter(Boolean);
  if (parts.length <= 1) {
    return '';
  }

  return parts.slice(0, -1).join('/');
}

function getRevisionKindLabel(revisionKind: string): string {
  switch (revisionKind.toLowerCase()) {
    case 'source':
      return 'source';
    case 'target':
      return 'target';
    default:
      return revisionKind;
  }
}

type CollectedOpportunity = {
  file: string;
  lineHint: string;
  startLine: number;
  title: string;
  description: string;
  suggestion: string;
  exampleCode: string;
  exampleCodeLanguage: string;
};

function getSemanticContextSummary(run: ReviewRun): string {
  const context = run.semanticCodeContext;
  if (!context?.enabled) {
    return 'Semantic code context выключен или недоступен.';
  }

  if (!context.attempted) {
    return 'Semantic code context ещё не запускался для этого run-а.';
  }

  if (context.timedOut) {
    return `Semantic context не успел собраться за ${formatDuration(context.elapsedMilliseconds)}.`;
  }

  if (context.succeeded) {
    return `${context.snippetCount} сниппетов ушло модели из ${context.candidateCount} кандидатов за ${formatDuration(context.elapsedMilliseconds)}.`;
  }

  return context.message || 'Semantic context выполнен без дополнительных сниппетов.';
}

function getSemanticContextStatusLabel(status?: string): string {
  switch (status) {
    case 'ready':
      return 'готов';
    case 'empty':
      return 'пусто';
    case 'timeout':
      return 'timeout';
    case 'failed':
      return 'ошибка';
    case 'disabled':
      return 'выключен';
    case 'skipped_missing_repository_context':
      return 'нет repo';
    case 'skipped_source_commit_unresolved':
      return 'нет commit';
    case 'skipped_no_queries':
      return 'нет queries';
    default:
      return 'ожидает';
  }
}

function formatDuration(milliseconds: number): string {
  if (milliseconds < 1000) {
    return `${milliseconds} мс`;
  }

  return `${(milliseconds / 1000).toFixed(1)} с`;
}

function formatTimestamp(value: string): string {
  return new Intl.DateTimeFormat('ru-RU', {
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    month: '2-digit'
  }).format(new Date(value));
}

function shortSha(value?: string): string {
  return value ? value.slice(0, 8) : '';
}

function collectPrimaryOpportunities(run: ReviewRun): CollectedOpportunity[] {
  return dedupeOpportunities(
    run.primaryOpportunities.map((item) => toCollectedOpportunity(item))
  );
}

function collectFollowUpOpportunities(run: ReviewRun): CollectedOpportunity[] {
  const items = run.reviewDiscussionMessages.flatMap((message) => {
    const structured = message.structuredContent;
    if (!structured) {
      return [];
    }

    if (structured.addedOpportunityItems?.length) {
      const collectedItems = structured.addedOpportunityItems.map((item) => toCollectedOpportunity(item));

      return assignExampleCodeToBestOpportunity(
        collectedItems,
        structured.exampleCode ?? '',
        structured.exampleCodeLanguage ?? ''
      );
    }

    const collectedItems = (structured.addedOpportunities ?? []).map((summary) =>
      parseOpportunitySummary(summary)
    );

    return assignExampleCodeToBestOpportunity(
      collectedItems,
      structured.exampleCode ?? '',
      structured.exampleCodeLanguage ?? ''
    );
  });

  return dedupeOpportunities(items);
}

function parseOpportunitySummary(
  summary: string
): CollectedOpportunity {
  const trimmed = summary.trim();
  const fileMatch = trimmed.match(/^(.*?)(?:\s+\((строка\s+\d+|Line\s+\d+|[^)]+)\))?:\s+(.*)$/i);
  if (!fileMatch) {
      return {
        file: '',
        lineHint: '',
        startLine: 0,
        title: trimmed,
        description: '',
        suggestion: '',
        exampleCode: '',
        exampleCodeLanguage: ''
      };
  }

  const [, rawFile = '', rawLineHint = '', rawTitle = ''] = fileMatch;
  const lineNumberMatch = rawLineHint.match(/(\d+)/);

  return {
    file: rawFile.trim(),
    lineHint: rawLineHint.trim(),
    startLine: lineNumberMatch ? Number(lineNumberMatch[1]) : 0,
    title: rawTitle.trim(),
    description: '',
    suggestion: '',
    exampleCode: '',
    exampleCodeLanguage: ''
  };
}

function toCollectedOpportunity(item: InlineDiscussionOpportunityItem | ReviewOpportunityItem): CollectedOpportunity {
  return {
    file: item.file,
    lineHint: item.lineHint,
    startLine: item.startLine,
    title: item.title,
    description: item.description,
    suggestion: 'suggestion' in item ? item.suggestion ?? '' : '',
    exampleCode: '',
    exampleCodeLanguage: ''
  };
}

function assignExampleCodeToBestOpportunity(
  items: CollectedOpportunity[],
  exampleCode: string,
  exampleCodeLanguage: string
): CollectedOpportunity[] {
  if (!exampleCode.trim() || !items.length) {
    return items;
  }

  if (items.length === 1) {
    return items.map((item) => ({
      ...item,
      exampleCode,
      exampleCodeLanguage
    }));
  }

  let bestIndex = -1;
  let bestScore = 0;
  for (let index = 0; index < items.length; index += 1) {
    const score = computeOpportunityCodeScore(items[index], exampleCode);
    if (score > bestScore) {
      bestScore = score;
      bestIndex = index;
    }
  }

  return items.map((item, index) => ({
    ...item,
    exampleCode: index === bestIndex && bestScore >= 3 ? exampleCode : '',
    exampleCodeLanguage: index === bestIndex && bestScore >= 3 ? exampleCodeLanguage : ''
  }));
}

function computeOpportunityCodeScore(item: CollectedOpportunity, exampleCode: string): number {
  const normalizedCode = normalizeOpportunityPhrase(exampleCode);
  if (!normalizedCode) {
    return 0;
  }

  let score = 0;
  const significantIdentifiers = extractCodeIdentifiers(`${item.title} ${item.description}`);
  for (const identifier of significantIdentifiers) {
    if (normalizedCode.includes(identifier.toLowerCase())) {
      score += identifier.length >= 10 ? 5 : 3;
    }
  }

  const opportunityTokens = buildOpportunityTokens(`${item.title} ${item.description}`);
  const codeTokens = buildOpportunityTokens(exampleCode);
  const overlap = opportunityTokens.filter((token) => codeTokens.includes(token)).length;
  score += overlap;

  return score;
}

function extractCodeIdentifiers(value: string): string[] {
  const matches = value.match(/\b[A-Z][A-Za-z0-9_]{3,}\b/g) ?? [];
  return [...new Set(matches)];
}

function dedupeOpportunities(items: CollectedOpportunity[]): CollectedOpportunity[] {
  const result: CollectedOpportunity[] = [];

  for (const item of items) {
    const duplicateIndex = result.findIndex((existing) => areLikelyDuplicateOpportunities(existing, item));
    if (duplicateIndex < 0) {
      result.push(item);
      continue;
    }

    result[duplicateIndex] = mergeOpportunities(result[duplicateIndex], item);
  }

  return result;
}

type OpportunityPresentationItem = CollectedOpportunity & {
  contextSnippet: string;
  contextStartLine: number;
  contextEndLine: number;
};

function buildOpportunityGroups(items: CollectedOpportunity[], run: ReviewRun) {
  const grouped = new Map<string, CollectedOpportunity[]>();

  for (const item of items) {
    const file = item.file || 'Без привязки к файлу';
    const bucket = grouped.get(file) ?? [];
    bucket.push(item);
    grouped.set(file, bucket);
  }

  return [...grouped.entries()]
    .map(([file, groupItems]) => ({
      file,
      displayName: file.split('/').at(-1) ?? file,
      items: [...groupItems]
        .map((item) => enrichOpportunity(item, run))
        .sort((left, right) => {
        if (left.startLine !== right.startLine) {
          return left.startLine - right.startLine;
        }

        return (left.title || left.description).localeCompare(right.title || right.description);
      })
    }))
    .sort((left, right) => left.file.localeCompare(right.file));
}

function enrichOpportunity(item: CollectedOpportunity, run: ReviewRun): OpportunityPresentationItem {
  const file = run.reviewedFiles.find((reviewedFile) => reviewedFile.filePath === item.file);
  if (!file?.fullContent || item.startLine <= 0) {
    return {
      ...item,
      contextSnippet: '',
      contextStartLine: 0,
      contextEndLine: 0
    };
  }

  const lines = file.fullContent.replace(/\r\n/g, '\n').split('\n');
  const startLine = Math.max(1, item.startLine - 4);
  const endLine = Math.min(lines.length, item.startLine + 4);
  const snippet = lines
    .slice(startLine - 1, endLine)
    .map((line, index) => `${String(startLine + index).padStart(4, ' ')}: ${line}`)
    .join('\n');

  return {
    ...item,
    contextSnippet: snippet,
    contextStartLine: startLine,
    contextEndLine: endLine
  };
}

function areLikelyDuplicateOpportunities(left: CollectedOpportunity, right: CollectedOpportunity): boolean {
  if (left.file.trim().toLowerCase() !== right.file.trim().toLowerCase()) {
    return false;
  }

  const normalizedLeftCode = normalizeOpportunityPhrase(left.exampleCode);
  const normalizedRightCode = normalizeOpportunityPhrase(right.exampleCode);
  if (
    left.startLine > 0 &&
    right.startLine > 0 &&
    Math.abs(left.startLine - right.startLine) <= 2 &&
    normalizedLeftCode.length > 0 &&
    normalizedLeftCode === normalizedRightCode
  ) {
    return true;
  }

  if (left.startLine > 0 && right.startLine > 0 && Math.abs(left.startLine - right.startLine) > 2) {
    return false;
  }

  const leftTokens = buildOpportunityTokens(left.title || left.description);
  const rightTokens = buildOpportunityTokens(right.title || right.description);
  if (!leftTokens.length || !rightTokens.length) {
    return normalizeOpportunityPhrase(left.title || left.description) === normalizeOpportunityPhrase(right.title || right.description);
  }

  const overlap = leftTokens.filter((token) => rightTokens.includes(token)).length;
  const minSize = Math.min(leftTokens.length, rightTokens.length);

  return overlap >= Math.max(2, Math.ceil(minSize * 0.6));
}

function mergeOpportunities(left: CollectedOpportunity, right: CollectedOpportunity): CollectedOpportunity {
  return {
    ...left,
    lineHint: left.lineHint || right.lineHint,
    startLine: left.startLine || right.startLine,
    title: preferMoreSpecificTitle(left.title, right.title),
    description: preferLonger(left.description, right.description),
    suggestion: preferLonger(left.suggestion, right.suggestion),
    exampleCode: preferLonger(left.exampleCode, right.exampleCode),
    exampleCodeLanguage: left.exampleCodeLanguage || right.exampleCodeLanguage
  };
}

function preferMoreSpecificTitle(left: string, right: string): string {
  const leftNormalized = normalizeOpportunityPhrase(left);
  const rightNormalized = normalizeOpportunityPhrase(right);
  const leftScore =
    buildOpportunityTokens(left).length +
    (leftNormalized.includes('parsepersonaldataasync') ? 3 : 0) +
    (leftNormalized.includes('task whenall') ? 2 : 0);
  const rightScore =
    buildOpportunityTokens(right).length +
    (rightNormalized.includes('parsepersonaldataasync') ? 3 : 0) +
    (rightNormalized.includes('task whenall') ? 2 : 0);

  if (rightScore !== leftScore) {
    return rightScore > leftScore ? right : left;
  }

  return preferLonger(left, right);
}

function preferLonger(left: string, right: string): string {
  return left.trim().length >= right.trim().length ? left : right;
}

function buildOpportunityTokens(value: string): string[] {
  return normalizeOpportunityPhrase(value)
    .split(' ')
    .map((token) => token.trim())
    .map((token) => token.replace(/(ого|ему|ому|ыми|ими|ый|ий|ой|ая|яя|ое|ее|ые|ие|ого|ему|ам|ям|ах|ях|ов|ев|ей|а|я|ы|и|о|е|у|ю)$/u, ''))
    .filter((token) =>
      token.length > 2 &&
      !['для', 'в', 'на', 'по', 'при', 'and', 'the', 'или', 'еще', 'ещё', 'метод', 'оптимизац', 'параллельн'].includes(token)
    );
}

function normalizeOpportunityPhrase(value: string): string {
  return value
    .toLowerCase()
    .replace(/[^a-zа-я0-9]+/gi, ' ')
    .replace(/\s+/g, ' ')
    .trim();
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
