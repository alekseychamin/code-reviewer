import { useState } from 'react';
import type {
  BranchReviewPayload,
  ProviderProfile,
  ReviewHistory,
  PullRequestReviewPayload
} from '../lib/types';

type ReviewMode = 'pullRequest' | 'branches';

interface ReviewFormProps {
  profiles: ProviderProfile[];
  pullRequestHistory: ReviewHistory | null;
  pullRequestHistoryError: string | null;
  pullRequestHistoryLoading: boolean;
  pullRequestHistoryDeleting: boolean;
  onStartPullRequestReview: (payload: PullRequestReviewPayload) => Promise<void>;
  onStartBranchReview: (payload: BranchReviewPayload) => Promise<void>;
  onDeletePullRequestHistory: (url: string) => Promise<void>;
  onPullRequestUrlChange: (url: string) => void;
}

export function ReviewForm({
  profiles,
  pullRequestHistory,
  pullRequestHistoryError,
  pullRequestHistoryLoading,
  pullRequestHistoryDeleting,
  onStartPullRequestReview,
  onStartBranchReview,
  onDeletePullRequestHistory,
  onPullRequestUrlChange
}: ReviewFormProps) {
  const [mode, setMode] = useState<ReviewMode>('pullRequest');
  const [providerProfileId, setProviderProfileId] = useState('');
  const [pullRequestUrl, setPullRequestUrl] = useState('');
  const [repositoryPath, setRepositoryPath] = useState('');
  const [repositoryName, setRepositoryName] = useState('');
  const [targetBranch, setTargetBranch] = useState('main');
  const [sourceBranch, setSourceBranch] = useState('');
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);

  function validate(): string | null {
    if (mode === 'pullRequest') {
      if (!pullRequestUrl.trim()) {
        return 'Укажи ссылку на pull request.';
      }

      return null;
    }

    if (!repositoryPath.trim()) {
      return 'Укажи абсолютный путь к репозиторию для сравнения веток.';
    }

    if (!sourceBranch.trim()) {
      return 'Укажи исходную ветку для сравнения.';
    }

    if (!targetBranch.trim()) {
      return 'Укажи целевую ветку для сравнения.';
    }

    return null;
  }

  async function handleSubmit(): Promise<void> {
    const validationError = validate();
    if (validationError) {
      setSubmitError(validationError);
      return;
    }

    setIsSubmitting(true);
    setSubmitError(null);

    try {
      if (mode === 'pullRequest') {
        await onStartPullRequestReview({
          pullRequestUrl,
          providerProfileId: providerProfileId || undefined,
          publishMode: 'None',
          stageOverrides: []
        });
      } else {
        await onStartBranchReview({
          repositoryPath,
          repositoryName: repositoryName || undefined,
          targetBranch,
          sourceBranch,
          providerProfileId: providerProfileId || undefined,
          publishMode: 'None',
          stageOverrides: []
        });
      }
    } catch (error) {
      setSubmitError(error instanceof Error ? error.message : String(error));
    } finally {
      setIsSubmitting(false);
    }
  }

  return (
    <section className="panel">
      <div className="panel-header">
        <div>
          <p className="eyebrow">Запуск ревью</p>
          <h2>Запуск AI-ревью</h2>
        </div>
        <div className="segmented-control">
          <button
            className={mode === 'pullRequest' ? 'active' : ''}
            onClick={() => setMode('pullRequest')}
            type="button"
          >
            Pull request
          </button>
          <button
            className={mode === 'branches' ? 'active' : ''}
            onClick={() => setMode('branches')}
            type="button"
          >
            Сравнение веток
          </button>
        </div>
      </div>

      <div className="form-grid">
        {mode === 'pullRequest' ? (
          <div className="form-stack form-stack-full">
            <label className="form-field">
              Ссылка на pull request
              <input
                value={pullRequestUrl}
                onChange={(event) => {
                  const value = event.target.value;
                  setPullRequestUrl(value);
                  onPullRequestUrlChange(value);
                }}
                placeholder="https://tfs.example.local/.../_git/repo/pullrequest/42"
              />
            </label>
            <PullRequestHistoryPanel
              history={pullRequestHistory}
              isDeleting={pullRequestHistoryDeleting}
              isLoading={pullRequestHistoryLoading}
              error={pullRequestHistoryError}
              onDeleteHistory={onDeletePullRequestHistory}
              pullRequestUrl={pullRequestUrl}
            />
          </div>
        ) : (
          <>
            <label className="form-field">
              Путь к репозиторию
              <input
                value={repositoryPath}
                onChange={(event) => setRepositoryPath(event.target.value)}
                placeholder="/Users/alex/Documents/Projects/programs/your-repo"
              />
            </label>
            <label className="form-field">
              Название репозитория
              <input
                value={repositoryName}
                onChange={(event) => setRepositoryName(event.target.value)}
                placeholder="Необязательное отображаемое имя"
              />
            </label>
            <label className="form-field">
              Целевая ветка
              <input value={targetBranch} onChange={(event) => setTargetBranch(event.target.value)} />
            </label>
            <label className="form-field">
              Исходная ветка
              <input value={sourceBranch} onChange={(event) => setSourceBranch(event.target.value)} />
            </label>
          </>
        )}

        <label className={`form-field ${mode === 'pullRequest' ? 'form-field-full' : ''}`}>
          Профиль провайдера
          <select value={providerProfileId} onChange={(event) => setProviderProfileId(event.target.value)}>
            <option value="">Использовать маршрут по умолчанию</option>
            {profiles.map((profile) => (
              <option key={profile.id} value={profile.id}>
                {profile.name} · {profile.defaultModel}
              </option>
            ))}
          </select>
        </label>
      </div>

      <div className="panel-footer">
        <button className="primary" disabled={isSubmitting} onClick={() => void handleSubmit()} type="button">
          {isSubmitting ? 'Запуск ревью...' : 'Запустить ревью'}
        </button>
      </div>

      {submitError ? <div className="inline-error">{submitError}</div> : null}
    </section>
  );
}

interface PullRequestHistoryPanelProps {
  history: ReviewHistory | null;
  isDeleting: boolean;
  isLoading: boolean;
  error: string | null;
  onDeleteHistory: (url: string) => Promise<void>;
  pullRequestUrl: string;
}

function PullRequestHistoryPanel({
  history,
  isDeleting,
  isLoading,
  error,
  onDeleteHistory,
  pullRequestUrl
}: PullRequestHistoryPanelProps) {
  const hasUrl = pullRequestUrl.trim().length > 0;

  if (!hasUrl && !isLoading && !error && !history) {
    return null;
  }

  return (
    <div className="history-panel">
      <div className="history-panel-header">
        <div>
          <p className="eyebrow">История ревью</p>
          <h3>Предыдущие запуски по этому PR</h3>
        </div>
        <div className="history-actions">
          {isLoading ? <span className="secondary-chip">Загрузка...</span> : null}
          {history && history.items.length > 0 ? (
            <button
              className="secondary-button"
              disabled={isDeleting}
              onClick={() => {
                if (!pullRequestUrl.trim()) {
                  return;
                }

                if (!window.confirm('Удалить всю сохранённую историю ревью для этого PR?')) {
                  return;
                }

                void onDeleteHistory(pullRequestUrl);
              }}
              type="button"
            >
              {isDeleting ? 'Удаляем...' : 'Удалить историю'}
            </button>
          ) : null}
        </div>
      </div>

      {error ? <div className="inline-error">{error}</div> : null}

      {!error && !isLoading && history && history.items.length > 0 ? (
        <>
          {history.baselineRunId ? (
            <p className="history-note">
              Последний завершённый запуск из этого списка будет использован как база для следующего ревью и блока
              <span className="history-highlight"> Delta Since Previous Review</span>.
            </p>
          ) : (
            <p className="history-note">Завершённых запусков пока нет, поэтому следующее ревью начнётся без baseline.</p>
          )}
          <div className="history-list">
            {history.items.map((item) => {
              const isBaseline = item.id === history.baselineRunId;
              return (
                <article className={`history-item ${isBaseline ? 'baseline' : ''}`} key={item.id}>
                  <div className="history-item-row">
                    <strong>{item.serviceName || item.title}</strong>
                    <span className={`status-pill status-${String(item.status).toLowerCase()}`}>{translateStatus(item.status)}</span>
                  </div>
                  <div className="history-item-row">
                    <span className="history-meta">{formatTimestamp(item.createdAt)}</span>
                    {isBaseline ? <span className="secondary-chip">Будет baseline</span> : null}
                  </div>
                  <div className="history-meta">
                    Найдено: {item.findingsCount}
                    {item.criticalCount > 0 ? ` · critical ${item.criticalCount}` : ''}
                    {item.highCount > 0 ? ` · high ${item.highCount}` : ''}
                    {item.authorName ? ` · ${item.authorName}` : ''}
                  </div>
                </article>
              );
            })}
          </div>
        </>
      ) : null}

      {!error && !isLoading && history && history.items.length === 0 && hasUrl ? (
        <p className="history-note">Для этого PR история ревью в БД пока не найдена.</p>
      ) : null}
    </div>
  );
}

function translateStatus(status: string): string {
  switch (status) {
    case 'Pending':
      return 'В очереди';
    case 'Running':
      return 'Выполняется';
    case 'Completed':
      return 'Завершено';
    case 'Failed':
      return 'Ошибка';
    default:
      return status;
  }
}

function formatTimestamp(value: string): string {
  return new Date(value).toLocaleString('ru-RU', {
    hour12: false,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit'
  });
}
