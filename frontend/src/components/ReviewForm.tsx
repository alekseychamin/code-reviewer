import { useEffect, useRef, useState } from 'react';
import { getBranchRepositorySuggestions, getBranchSourceSuggestions } from '../lib/api';
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
  branchHistory: ReviewHistory | null;
  branchHistoryError: string | null;
  branchHistoryLoading: boolean;
  branchHistoryDeleting: boolean;
  onStartPullRequestReview: (payload: PullRequestReviewPayload) => Promise<void>;
  onStartBranchReview: (payload: BranchReviewPayload) => Promise<void>;
  onDeletePullRequestHistory: (url: string) => Promise<void>;
  onDeleteBranchHistory: (repositoryName: string, sourceBranch: string, targetBranch: string) => Promise<void>;
  onBranchContextChange: (repositoryName: string, sourceBranch: string, targetBranch: string) => void;
  onPullRequestUrlChange: (url: string) => void;
  onModeChange: (mode: ReviewMode) => void;
}

export function ReviewForm({
  profiles,
  pullRequestHistory,
  pullRequestHistoryError,
  pullRequestHistoryLoading,
  pullRequestHistoryDeleting,
  branchHistory,
  branchHistoryError,
  branchHistoryLoading,
  branchHistoryDeleting,
  onStartPullRequestReview,
  onStartBranchReview,
  onDeletePullRequestHistory,
  onDeleteBranchHistory,
  onBranchContextChange,
  onPullRequestUrlChange,
  onModeChange
}: ReviewFormProps) {
  const [mode, setMode] = useState<ReviewMode>('pullRequest');
  const [providerProfileId, setProviderProfileId] = useState('');
  const [pullRequestUrl, setPullRequestUrl] = useState('');
  const [repositoryName, setRepositoryName] = useState('');
  const [sourceBranch, setSourceBranch] = useState('');
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [repositorySuggestions, setRepositorySuggestions] = useState<string[]>([]);
  const [repositorySuggestionsLoading, setRepositorySuggestionsLoading] = useState(false);
  const [repositorySuggestionsOpen, setRepositorySuggestionsOpen] = useState(false);
  const [sourceBranchSuggestions, setSourceBranchSuggestions] = useState<string[]>([]);
  const [sourceBranchSuggestionsLoading, setSourceBranchSuggestionsLoading] = useState(false);
  const [sourceBranchSuggestionsOpen, setSourceBranchSuggestionsOpen] = useState(false);
  const repositorySuggestionsRequestIdRef = useRef(0);
  const sourceBranchSuggestionsRequestIdRef = useRef(0);
  const committedRepositoryRef = useRef('');
  const committedSourceBranchRef = useRef('');
  const branchCacheRef = useRef<Record<string, string[]>>({});
  const targetBranch = 'master';

  function validate(): string | null {
    if (mode === 'pullRequest') {
      if (!pullRequestUrl.trim()) {
        return 'Укажи ссылку на pull request.';
      }

      return null;
    }

    if (!repositoryName.trim()) {
      return 'Укажи название папки репозитория внутри настроенного корня проектов.';
    }

    if (!sourceBranch.trim()) {
      return 'Укажи исходную ветку для сравнения.';
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
          repositoryName,
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

  useEffect(() => {
    if (mode !== 'branches') {
      return;
    }

    onBranchContextChange(repositoryName, sourceBranch, targetBranch);
  }, [mode, onBranchContextChange, repositoryName, sourceBranch, targetBranch]);

  useEffect(() => {
    if (mode !== 'branches') {
      repositorySuggestionsRequestIdRef.current += 1;
      setRepositorySuggestions([]);
      setRepositorySuggestionsLoading(false);
      setRepositorySuggestionsOpen(false);
      return;
    }

    const normalizedQuery = repositoryName.trim();
    if (!normalizedQuery) {
      committedRepositoryRef.current = '';
      repositorySuggestionsRequestIdRef.current += 1;
      setRepositorySuggestions([]);
      setRepositorySuggestionsLoading(false);
      setRepositorySuggestionsOpen(false);
      return;
    }

    if (committedRepositoryRef.current === normalizedQuery) {
      repositorySuggestionsRequestIdRef.current += 1;
      setRepositorySuggestionsLoading(false);
      setRepositorySuggestionsOpen(false);
      return;
    }

    repositorySuggestionsRequestIdRef.current += 1;
    const requestId = repositorySuggestionsRequestIdRef.current;
    setRepositorySuggestionsLoading(true);

    const timeoutId = window.setTimeout(() => {
      void getBranchRepositorySuggestions(normalizedQuery)
        .then((items) => {
          if (repositorySuggestionsRequestIdRef.current !== requestId) {
            return;
          }

          setRepositorySuggestions(items);
          const hasExactMatch = items.some((item) => item === normalizedQuery);
          setRepositorySuggestionsOpen(items.length > 0 && !hasExactMatch);
        })
        .catch(() => {
          if (repositorySuggestionsRequestIdRef.current !== requestId) {
            return;
          }

          setRepositorySuggestions([]);
          setRepositorySuggestionsOpen(false);
        })
        .finally(() => {
          if (repositorySuggestionsRequestIdRef.current === requestId) {
            setRepositorySuggestionsLoading(false);
          }
        });
    }, 200);

    return () => {
      window.clearTimeout(timeoutId);
    };
  }, [mode, repositoryName]);

  useEffect(() => {
    if (mode !== 'branches') {
      sourceBranchSuggestionsRequestIdRef.current += 1;
      setSourceBranchSuggestions([]);
      setSourceBranchSuggestionsLoading(false);
      setSourceBranchSuggestionsOpen(false);
      return;
    }

    const normalizedRepositoryName = repositoryName.trim();
    const normalizedQuery = sourceBranch.trim();
    if (!normalizedRepositoryName) {
      committedSourceBranchRef.current = '';
      sourceBranchSuggestionsRequestIdRef.current += 1;
      setSourceBranchSuggestions([]);
      setSourceBranchSuggestionsLoading(false);
      setSourceBranchSuggestionsOpen(false);
      return;
    }

    const cachedBranches = branchCacheRef.current[normalizedRepositoryName];
    if (cachedBranches) {
      const filtered = cachedBranches
        .filter((branch) => normalizedQuery.length === 0 || branch.includes(normalizedQuery))
        .sort((left, right) => {
          const leftStarts = normalizedQuery.length > 0 && left.startsWith(normalizedQuery);
          const rightStarts = normalizedQuery.length > 0 && right.startsWith(normalizedQuery);
          if (leftStarts !== rightStarts) {
            return leftStarts ? -1 : 1;
          }

          return left.localeCompare(right, 'en', { sensitivity: 'base' });
        })
        .slice(0, 20);

      setSourceBranchSuggestions(filtered);
      const hasExactMatch = filtered.some((item) => item === normalizedQuery);
      setSourceBranchSuggestionsLoading(false);
      setSourceBranchSuggestionsOpen(filtered.length > 0 && !hasExactMatch);
      return;
    }

    if (!normalizedQuery) {
      committedSourceBranchRef.current = '';
      sourceBranchSuggestionsRequestIdRef.current += 1;
      setSourceBranchSuggestions([]);
      setSourceBranchSuggestionsLoading(false);
      setSourceBranchSuggestionsOpen(false);
      return;
    }

    if (committedSourceBranchRef.current === normalizedQuery) {
      sourceBranchSuggestionsRequestIdRef.current += 1;
      setSourceBranchSuggestionsLoading(false);
      setSourceBranchSuggestionsOpen(false);
      return;
    }

    sourceBranchSuggestionsRequestIdRef.current += 1;
    const requestId = sourceBranchSuggestionsRequestIdRef.current;
    setSourceBranchSuggestionsLoading(true);

    const timeoutId = window.setTimeout(() => {
      void getBranchSourceSuggestions(normalizedRepositoryName, normalizedQuery)
        .then((items) => {
          if (sourceBranchSuggestionsRequestIdRef.current !== requestId) {
            return;
          }

          branchCacheRef.current[normalizedRepositoryName] = items;
          setSourceBranchSuggestions(items);
          const hasExactMatch = items.some((item) => item === normalizedQuery);
          setSourceBranchSuggestionsOpen(items.length > 0 && !hasExactMatch);
        })
        .catch(() => {
          if (sourceBranchSuggestionsRequestIdRef.current !== requestId) {
            return;
          }

          setSourceBranchSuggestions([]);
          setSourceBranchSuggestionsOpen(false);
        })
        .finally(() => {
          if (sourceBranchSuggestionsRequestIdRef.current === requestId) {
            setSourceBranchSuggestionsLoading(false);
          }
        });
    }, 200);

    return () => {
      window.clearTimeout(timeoutId);
    };
  }, [mode, repositoryName, sourceBranch]);

  useEffect(() => {
    if (mode !== 'branches') {
      return;
    }

    const normalizedRepositoryName = repositoryName.trim();
    if (!normalizedRepositoryName || branchCacheRef.current[normalizedRepositoryName]) {
      return;
    }

    let cancelled = false;
    void getBranchSourceSuggestions(normalizedRepositoryName, '')
      .then((items) => {
        if (cancelled) {
          return;
        }

        branchCacheRef.current[normalizedRepositoryName] = items;
      })
      .catch(() => {
      });

    return () => {
      cancelled = true;
    };
  }, [mode, repositoryName]);

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
            onClick={() => {
              setMode('pullRequest');
              onModeChange('pullRequest');
            }}
            type="button"
          >
            Pull request
          </button>
          <button
            className={mode === 'branches' ? 'active' : ''}
            onClick={() => {
              setMode('branches');
              onModeChange('branches');
            }}
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
                placeholder="https://tfs.example.local/.../_git/repo/pullrequest/42 или https://github.com/org/repo/pull/42"
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
              Название репозитория
              <div className="form-field-with-hint">
                <input
                  value={repositoryName}
                  onBlur={() => {
                    window.setTimeout(() => setRepositorySuggestionsOpen(false), 120);
                  }}
                  onChange={(event) => {
                    const value = event.target.value;
                    setRepositoryName(value);
                    committedRepositoryRef.current = '';
                    setSourceBranch('');
                    committedSourceBranchRef.current = '';
                    if (!value.trim()) {
                      setSourceBranchSuggestions([]);
                    }
                    setRepositorySuggestionsOpen(true);
                    setSourceBranchSuggestions([]);
                    setSourceBranchSuggestionsOpen(false);
                  }}
                  onFocus={() => {
                    if (repositorySuggestions.length > 0) {
                      setRepositorySuggestionsOpen(true);
                    }
                  }}
                  placeholder="Например: Tele2_Crm_SubscriberProductInventoryService"
                />
                {repositorySuggestionsOpen || repositorySuggestionsLoading ? (
                  <div className="suggestions-panel">
                    {repositorySuggestionsLoading ? (
                      <div className="suggestions-empty">Ищем репозитории...</div>
                    ) : repositorySuggestions.length > 0 ? (
                      repositorySuggestions.map((suggestion) => (
                        <button
                          key={suggestion}
                          className="suggestion-item"
                          onMouseDown={(event) => {
                            event.preventDefault();
                            setRepositoryName(suggestion);
                            committedRepositoryRef.current = suggestion;
                            setSourceBranch('');
                            committedSourceBranchRef.current = '';
                            setRepositorySuggestionsOpen(false);
                            setSourceBranchSuggestions([]);
                            setSourceBranchSuggestionsOpen(false);
                          }}
                          type="button"
                        >
                          {suggestion}
                        </button>
                      ))
                    ) : (
                      <div className="suggestions-empty">Совпадений не найдено.</div>
                    )}
                  </div>
                ) : null}
                <span className="form-hint">Репозиторий ищется внутри корня, настроенного в `.env`.</span>
              </div>
            </label>
            <label className="form-field">
              Целевая ветка
              <div className="form-field-with-hint">
                <input readOnly value={targetBranch} />
                <span className="form-hint">Для сравнения веток целевая ветка фиксирована: `master`.</span>
              </div>
            </label>
            <label className="form-field">
              Исходная ветка
              <div className="form-field-with-hint">
                <input
                  value={sourceBranch}
                  onBlur={() => {
                    window.setTimeout(() => setSourceBranchSuggestionsOpen(false), 120);
                  }}
                  onChange={(event) => {
                    const value = event.target.value;
                    setSourceBranch(value);
                    committedSourceBranchRef.current = '';
                    setSourceBranchSuggestionsOpen(true);
                  }}
                  onFocus={() => {
                    if (sourceBranchSuggestions.length > 0) {
                      setSourceBranchSuggestionsOpen(true);
                    }
                  }}
                  placeholder="Например: release/26.03.04"
                />
                {sourceBranchSuggestionsOpen || sourceBranchSuggestionsLoading ? (
                  <div className="suggestions-panel">
                    {sourceBranchSuggestionsLoading ? (
                      <div className="suggestions-empty">Ищем ветки...</div>
                    ) : sourceBranchSuggestions.length > 0 ? (
                      sourceBranchSuggestions.map((suggestion) => (
                        <button
                          key={suggestion}
                          className="suggestion-item"
                          onMouseDown={(event) => {
                            event.preventDefault();
                            setSourceBranch(suggestion);
                            committedSourceBranchRef.current = suggestion;
                            setSourceBranchSuggestionsOpen(false);
                          }}
                          type="button"
                        >
                          {suggestion}
                        </button>
                      ))
                    ) : (
                      <div className="suggestions-empty">Совпадений не найдено.</div>
                    )}
                  </div>
                ) : null}
                <span className="form-hint">Ветки читаются из выбранного репозитория.</span>
              </div>
            </label>
            <BranchHistoryPanel
              history={branchHistory}
              isDeleting={branchHistoryDeleting}
              isLoading={branchHistoryLoading}
              error={branchHistoryError}
              onDeleteHistory={onDeleteBranchHistory}
              repositoryName={repositoryName}
              sourceBranch={sourceBranch}
              targetBranch={targetBranch}
            />
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

interface BranchHistoryPanelProps {
  history: ReviewHistory | null;
  isDeleting: boolean;
  isLoading: boolean;
  error: string | null;
  onDeleteHistory: (repositoryName: string, sourceBranch: string, targetBranch: string) => Promise<void>;
  repositoryName: string;
  sourceBranch: string;
  targetBranch: string;
}

function BranchHistoryPanel({
  history,
  isDeleting,
  isLoading,
  error,
  onDeleteHistory,
  repositoryName,
  sourceBranch,
  targetBranch
}: BranchHistoryPanelProps) {
  const hasSelection = repositoryName.trim().length > 0 && sourceBranch.trim().length > 0;

  if (!hasSelection && !isLoading && !error && !history) {
    return null;
  }

  return (
    <div className="history-panel form-stack-full">
      <div className="history-panel-header">
        <div>
          <p className="eyebrow">История ревью</p>
          <h3>Предыдущие запуски для этого сравнения веток</h3>
        </div>
        <div className="history-actions">
          {isLoading ? <span className="secondary-chip">Загрузка...</span> : null}
          {history && history.items.length > 0 ? (
            <button
              className="secondary-button"
              disabled={isDeleting}
              onClick={() => {
                if (!hasSelection) {
                  return;
                }

                if (!window.confirm('Удалить всю сохранённую историю ревью для этого сравнения веток?')) {
                  return;
                }

                void onDeleteHistory(repositoryName, sourceBranch, targetBranch);
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

      {!error && !isLoading && history && history.items.length === 0 && hasSelection ? (
        <p className="history-note">Для этого сравнения веток история ревью в БД пока не найдена.</p>
      ) : null}
    </div>
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
