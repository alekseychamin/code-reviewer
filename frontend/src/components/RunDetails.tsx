import { MarkdownBlock } from './MarkdownBlock';
import { MermaidDiagram } from './MermaidDiagram';
import type { ReviewRun } from '../lib/types';

interface RunDetailsProps {
  run: ReviewRun | null;
}

export function RunDetails({ run }: RunDetailsProps) {
  if (!run) {
    return (
      <section className="panel">
        <div className="panel-header">
          <div>
            <p className="eyebrow">Results</p>
            <h2>Report, findings, and publish drafts</h2>
          </div>
        </div>
        <div className="empty-state">Start a review to see change description, findings, markdown report, and inline drafts.</div>
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
        <span className="secondary-chip">{run.findings.length} findings</span>
      </div>

      <div className="results-grid">
        <article className="result-card">
          <h3>Change description</h3>
          <MarkdownBlock
            content={run.changeDescription}
            emptyText="Description will appear here once phase 1 completes."
          />
        </article>

        <article className="result-card">
          <h3>Summary comment draft</h3>
          <MarkdownBlock
            content={run.summaryComment}
            emptyText="Summary comment draft will appear here."
          />
        </article>

        <article className="result-card span-2">
          <h3>Markdown report</h3>
          <MarkdownBlock
            content={run.markdownReport}
            emptyText="Final synthesized report will appear here."
          />
        </article>
      </div>

      <div className="subsection">
        <h3>Change mindmap</h3>
        {run.changeDiagramMermaid ? (
          <div className="diagram-card">
            <MermaidDiagram chart={run.changeDiagramMermaid} />
          </div>
        ) : (
          <div className="empty-state">Mindmap will appear here when phase 1 returns a Mermaid diagram.</div>
        )}
      </div>

      <div className="subsection">
        <h3>Changed files</h3>
        <div className="chip-list">
          {run.changedFiles.map((file) => (
            <span className="file-chip" key={file}>
              {file}
            </span>
          ))}
        </div>
      </div>

      <div className="subsection">
        <h3>Normalized findings</h3>
        <div className="findings-list">
          {run.findings.length === 0 ? (
            <div className="empty-state">No findings stored for this run yet.</div>
          ) : (
            run.findings.map((finding, index) => (
              <article className="finding-card" key={`${finding.file}-${index}`}>
                <header>
                  <span className={`severity severity-${finding.severity.toLowerCase()}`}>{finding.severity}</span>
                  <strong>{finding.title}</strong>
                  <code>{finding.file}</code>
                </header>
                <MarkdownBlock content={finding.description} emptyText="" />
                <small>
                  {finding.category} · {finding.lineHint}
                </small>
              </article>
            ))
          )}
        </div>
      </div>

      <div className="subsection">
        <h3>Inline comment drafts</h3>
        <div className="findings-list">
          {run.inlineComments.length === 0 ? (
            <div className="empty-state">No inline drafts could be mapped to diff lines.</div>
          ) : (
            run.inlineComments.map((comment, index) => (
              <article className="finding-card" key={`${comment.filePath}-${comment.lineNumber}-${index}`}>
                <header>
                  <span className="secondary-chip">Line {comment.lineNumber}</span>
                  <code>{comment.filePath}</code>
                </header>
                <MarkdownBlock content={comment.content} emptyText="" />
              </article>
            ))
          )}
        </div>
      </div>
    </section>
  );
}
