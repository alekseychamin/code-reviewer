import { useState } from 'react';
import type { ReviewProgressEvent, ReviewRun } from '../lib/types';

interface ProgressStreamProps {
  run: ReviewRun | null;
  events: ReviewProgressEvent[];
}

export function ProgressStream({ run, events }: ProgressStreamProps) {
  const [isTimelineCollapsed, setIsTimelineCollapsed] = useState(false);
  const statusLabel = toRussianStatus(run?.status);
  const statusClass = typeof run?.status === 'string' ? run.status.toLowerCase() : 'unknown';

  return (
    <section className="panel">
      <div className="panel-header">
        <div>
          <p className="eyebrow">Прогресс</p>
          <h2>Ход выполнения пайплайна</h2>
        </div>
        <div className="result-toolbar">
          <button
            aria-expanded={!isTimelineCollapsed}
            className="secondary-button"
            onClick={() => setIsTimelineCollapsed((value) => !value)}
            type="button"
          >
            {isTimelineCollapsed ? 'Развернуть ленту' : 'Свернуть ленту'}
          </button>
          {run ? <span className={`status-pill status-${statusClass}`}>{statusLabel}</span> : null}
        </div>
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

      {!isTimelineCollapsed ? (
        <div className="timeline">
          {events.length === 0 && run ? (
            <div className="timeline-item">
              <span className="timeline-stage">{toRussianStage(run.currentStage) ?? toRussianStatus(run.status)}</span>
              <span>{translateProgressMessage(run.currentMessage) || toRussianStatus(run.status)}</span>
              <time>
                {new Date(run.updatedAt).toLocaleTimeString('ru-RU', {
                  hour: '2-digit',
                  minute: '2-digit',
                  second: '2-digit',
                  hour12: false
                })}
              </time>
            </div>
          ) : events.length === 0 ? (
            <div className="timeline-item muted">Ожидание первого запуска ревью.</div>
          ) : (
            events.map((event, index) => (
              <div className="timeline-item" key={`${event.runId}-${index}`}>
                <span className="timeline-stage">{toRussianStage(event.stage) ?? toRussianStatus(event.status)}</span>
                <span>{translateProgressMessage(event.message)}</span>
                <time>
                  {new Date(event.timestamp).toLocaleTimeString('ru-RU', {
                    hour: '2-digit',
                    minute: '2-digit',
                    second: '2-digit',
                    hour12: false
                  })}
                </time>
              </div>
            ))
          )}
        </div>
      ) : null}
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
    case 'Cancelled':
      return 'Остановлено';
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
    'Review completed': 'Ревью завершено',
    'Остановка ревью запрошена пользователем.': 'Остановка ревью запрошена пользователем.',
    'Ревью остановлено пользователем.': 'Ревью остановлено пользователем.'
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
