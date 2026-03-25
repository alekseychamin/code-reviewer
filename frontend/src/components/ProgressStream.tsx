import type { ReviewProgressEvent, ReviewRun } from '../lib/types';

interface ProgressStreamProps {
  run: ReviewRun | null;
  events: ReviewProgressEvent[];
}

export function ProgressStream({ run, events }: ProgressStreamProps) {
  const statusLabel = toRussianStatus(run?.status);
  const statusClass = typeof run?.status === 'string' ? run.status.toLowerCase() : 'unknown';

  return (
    <section className="panel">
      <div className="panel-header">
        <div>
          <p className="eyebrow">Прогресс</p>
          <h2>Ход выполнения пайплайна</h2>
        </div>
        {run ? <span className={`status-pill status-${statusClass}`}>{statusLabel}</span> : null}
      </div>

      <div className="progress-shell">
        <div className="progress-bar">
          <div style={{ width: `${run?.progressPercent ?? 0}%` }} />
        </div>
        <div className="progress-meta">
          <strong>{run?.progressPercent ?? 0}%</strong>
          <span>{translateProgressMessage(run?.currentMessage) ?? 'Ревью ещё не запускалось.'}</span>
        </div>
      </div>

      <div className="timeline">
        {events.length === 0 ? (
          <div className="timeline-item muted">Ожидание первого запуска ревью.</div>
        ) : (
          events.map((event, index) => (
            <div className="timeline-item" key={`${event.runId}-${index}`}>
              <span className="timeline-stage">{toRussianStage(event.stage) ?? toRussianStatus(event.status)}</span>
              <span>{translateProgressMessage(event.message)}</span>
              <time>{new Date(event.timestamp).toLocaleTimeString()}</time>
            </div>
          ))
        )}
      </div>
    </section>
  );
}

function toRussianStatus(status: string | undefined): string {
  switch (status) {
    case 'Pending':
      return 'В очереди';
    case 'Running':
      return 'В работе';
    case 'Completed':
      return 'Завершено';
    case 'Failed':
      return 'Ошибка';
    default:
      return status ?? '';
  }
}

function toRussianStage(stage: string | undefined): string | undefined {
  switch (stage) {
    case 'DiffAcquisition':
      return 'Получение diff';
    case 'Preprocessing':
      return 'Предобработка';
    case 'ChangeDescription':
      return 'Описание изменений';
    case 'ChunkReview':
      return 'Ревью чанков';
    case 'FindingsNormalization':
      return 'Нормализация замечаний';
    case 'FinalSynthesis':
      return 'Финальная сборка';
    case 'Publish':
      return 'Публикация';
    default:
      return stage;
  }
}

function translateProgressMessage(message: string | undefined): string {
  if (!message) {
    return '';
  }

  const directMap: Record<string, string> = {
    Queued: 'В очереди',
    'Review started': 'Ревью запущено',
    'Diff acquired': 'Diff получен',
    'Change description generated': 'Описание изменений подготовлено',
    'Raw findings collected': 'Черновые замечания собраны',
    'File review workspace generated': 'Рабочее пространство по файлам подготовлено',
    'Final report generated': 'Финальный отчёт сформирован',
    'Publishing review comments': 'Публикация комментариев ревью',
    'Review completed': 'Ревью завершено'
  };

  if (directMap[message]) {
    return directMap[message];
  }

  const preparedMatch = message.match(/^Prepared (\d+) changed files across (\d+) chunks$/);
  if (preparedMatch) {
    return `Подготовлено ${preparedMatch[1]} изменённых файлов в ${preparedMatch[2]} чанках`;
  }

  const reviewingChunkMatch = message.match(/^Reviewing chunk (\d+) of (\d+)$/);
  if (reviewingChunkMatch) {
    return `Ревью чанка ${reviewingChunkMatch[1]} из ${reviewingChunkMatch[2]}`;
  }

  const normalizedMatch = message.match(/^Normalized (\d+) findings$/);
  if (normalizedMatch) {
    return `Нормализовано ${normalizedMatch[1]} замечаний`;
  }

  return message;
}
