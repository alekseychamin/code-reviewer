import type { ReviewProgressEvent, ReviewRun } from '../lib/types';

interface ProgressStreamProps {
  run: ReviewRun | null;
  events: ReviewProgressEvent[];
}

export function ProgressStream({ run, events }: ProgressStreamProps) {
  const statusLabel = typeof run?.status === 'string' ? run.status : String(run?.status ?? '');
  const statusClass = statusLabel ? statusLabel.toLowerCase() : 'unknown';

  return (
    <section className="panel">
      <div className="panel-header">
        <div>
          <p className="eyebrow">Progress</p>
          <h2>Live pipeline telemetry</h2>
        </div>
        {run ? <span className={`status-pill status-${statusClass}`}>{statusLabel}</span> : null}
      </div>

      <div className="progress-shell">
        <div className="progress-bar">
          <div style={{ width: `${run?.progressPercent ?? 0}%` }} />
        </div>
        <div className="progress-meta">
          <strong>{run?.progressPercent ?? 0}%</strong>
          <span>{run?.currentMessage ?? 'No review in progress yet.'}</span>
        </div>
      </div>

      <div className="timeline">
        {events.length === 0 ? (
          <div className="timeline-item muted">Waiting for the first review run.</div>
        ) : (
          events.map((event, index) => (
            <div className="timeline-item" key={`${event.runId}-${index}`}>
              <span className="timeline-stage">{event.stage ?? event.status}</span>
              <span>{event.message}</span>
              <time>{new Date(event.timestamp).toLocaleTimeString()}</time>
            </div>
          ))
        )}
      </div>
    </section>
  );
}
