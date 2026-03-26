import { MarkdownBlock } from './MarkdownBlock';
import { MermaidDiagram } from './MermaidDiagram';
import { ReviewedFilesWorkspace } from './ReviewedFilesWorkspace';
import type { ChangeDescriptionStructuredContent, ReviewRun } from '../lib/types';

interface RunDetailsProps {
  run: ReviewRun | null;
  diffDownloadUrl?: string;
  reportDownloadUrl?: string;
  onPublishReport: () => Promise<void>;
  onPublishInlineComment: (commentId: string) => Promise<void>;
  onAskInlineQuestion: (commentId: string, message: string) => Promise<void>;
}

export function RunDetails({
  run,
  diffDownloadUrl,
  reportDownloadUrl,
  onPublishReport,
  onPublishInlineComment,
  onAskInlineQuestion
}: RunDetailsProps) {
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
          <h2>{run.title}</h2>
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
          <h3>Диаграмма изменений</h3>
          {run.changeDiagramMermaid ? (
            <div className="diagram-card large">
              <MermaidDiagram chart={run.changeDiagramMermaid} />
            </div>
          ) : (
            <div className="empty-state">Диаграмма появится здесь, как только этап описания изменений её вернёт.</div>
          )}
        </article>

        <article className="result-card">
          <h3>Описание изменений</h3>
          <ChangeDescriptionBlock
            content={run.changeDescriptionStructured}
            fallback={run.changeDescription}
          />
        </article>
      </div>

      <div className="subsection">
        <h3>Замечания по файлам</h3>
        <ReviewedFilesWorkspace
          files={run.reviewedFiles}
          onAskInlineQuestion={onAskInlineQuestion}
          onPublishInlineComment={onPublishInlineComment}
        />
      </div>

      <div className="subsection">
        <div className="subsection-header">
          <h3>Итоговый отчёт</h3>
          {run.targetKind === 'PullRequest' ? (
            <button
              className="secondary-button"
              disabled={!run.hasMarkdownReportArtifact || run.publishSucceeded}
              onClick={() => void onPublishReport()}
              type="button"
            >
              {run.publishSucceeded ? 'Отправлено в TFS' : 'Отправить отчёт в TFS'}
            </button>
          ) : null}
        </div>
        <article className="result-card report-card">
          <MarkdownBlock
            content={run.markdownReport}
            emptyText="Итоговый отчёт появится здесь после завершения синтеза."
            normalize={false}
          />
        </article>
      </div>
    </section>
  );
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
