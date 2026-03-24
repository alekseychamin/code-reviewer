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
          <pre>{run.changeDescription || 'Description will appear here once phase 3 completes.'}</pre>
        </article>

        <article className="result-card">
          <h3>Summary comment draft</h3>
          <pre>{run.summaryComment || 'Summary comment draft will appear here.'}</pre>
        </article>

        <article className="result-card span-2">
          <h3>Markdown report</h3>
          <pre>{run.markdownReport || 'Final synthesized report will appear here.'}</pre>
        </article>
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
                <p>{finding.description}</p>
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
                <pre>{comment.content}</pre>
              </article>
            ))
          )}
        </div>
      </div>
    </section>
  );
}
