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
            <p className="eyebrow">Results</p>
            <h2>Diagram, summary, and file review workspace</h2>
          </div>
        </div>
        <div className="empty-state">Start a review to see the change diagram, change summary, and file-by-file inline review threads.</div>
      </section>
    );
  }

  return (
    <section className="panel">
      <div className="panel-header">
        <div>
          <p className="eyebrow">Results</p>
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
              Export diff.txt
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
              Export report.md
          </button>
          <span className="secondary-chip">{run.findings.length} findings</span>
        </div>
      </div>

      <div className="results-stack">
        <article className="result-card">
          <h3>Change diagram</h3>
          {run.changeDiagramMermaid ? (
            <div className="diagram-card large">
              <MermaidDiagram chart={run.changeDiagramMermaid} />
            </div>
          ) : (
            <div className="empty-state">Diagram will appear here as soon as the change-description phase returns it.</div>
          )}
        </article>

        <article className="result-card">
          <h3>Change description</h3>
          <MarkdownBlock
            content={run.changeDescription}
            emptyText="Description will appear here once phase 1 completes."
          />
        </article>
      </div>

      <div className="subsection">
        <h3>File review workspace</h3>
        <ReviewedFilesWorkspace
          files={run.reviewedFiles}
          onAskInlineQuestion={onAskInlineQuestion}
          onPublishInlineComment={onPublishInlineComment}
        />
      </div>
    </section>
  );
}
