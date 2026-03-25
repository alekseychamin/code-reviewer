import { MarkdownBlock } from './MarkdownBlock';
import { MermaidDiagram } from './MermaidDiagram';
import { ReviewedFilesWorkspace } from './ReviewedFilesWorkspace';
import type { ReviewRun } from '../lib/types';

interface RunDetailsProps {
  run: ReviewRun | null;
  diffDownloadUrl?: string;
  reportDownloadUrl?: string;
  onPublishInlineComment: (commentId: string) => Promise<void>;
  onAskInlineQuestion: (commentId: string, message: string) => Promise<void>;
}

export function RunDetails({
  run,
  diffDownloadUrl,
  reportDownloadUrl,
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
          <MarkdownBlock
            content={run.changeDescription}
            emptyText="Описание появится здесь после завершения первого этапа."
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
    </section>
  );
}
