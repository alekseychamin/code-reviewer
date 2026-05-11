import { useEffect, useRef, useState } from 'react';
import { getBranchRepositorySuggestions, getBranchSourceSuggestions } from '../lib/api';
import type {
  BranchReviewPayload,
  ProviderProfile,
  ReviewHistory,
  ReviewHistoryItem,
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
  serviceHistory: ReviewHistory | null;
  serviceHistoryError: string | null;
  serviceHistoryLoading: boolean;
  serviceHistoryQuery: string;
  selectedBaselineRunId?: string;
  onStartPullRequestReview: (payload: PullRequestReviewPayload) => Promise<void>;
  onStartBranchReview: (payload: BranchReviewPayload) => Promise<void>;
  onDeletePullRequestHistory: (url: string) => Promise<void>;
  onDeleteBranchHistory: (repositoryName: string, sourceBranch: string, targetBranch: string) => Promise<void>;
  onDeleteHistoryRun: (runId: string) => Promise<void>;
  onStopReviewRun: (runId: string) => Promise<void>;
  onSelectHistoryRun: (runId: string) => Promise<void>;
  onSelectServiceHistoryRun: (runId: string) => Promise<void>;
  onSelectBaselineRun: (runId: string) => void;
  onBranchContextChange: (repositoryName: string, sourceBranch: string, targetBranch: string) => void;
  onPullRequestUrlChange: (url: string) => void;
  onServiceHistoryQueryChange: (query: string) => void;
  onModeChange: (mode: ReviewMode) => void;
  activeRunId?: string;
  selectedRunId?: string;
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
  serviceHistory,
  serviceHistoryError,
  serviceHistoryLoading,
  serviceHistoryQuery,
  selectedBaselineRunId,
  onStartPullRequestReview,
  onStartBranchReview,
  onDeletePullRequestHistory,
  onDeleteBranchHistory,
  onDeleteHistoryRun,
  onStopReviewRun,
  onSelectHistoryRun,
  onSelectServiceHistoryRun,
  onSelectBaselineRun,
  onBranchContextChange,
  onPullRequestUrlChange,
  onServiceHistoryQueryChange,
  onModeChange,
  activeRunId,
  selectedRunId
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

  async function handleSubmit(forceRerun = false): Promise<void> {
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
          forceRerun,
          baselineRunId: selectedBaselineRunId || undefined,
          stageOverrides: []
        });
      } else {
        await onStartBranchReview({
          repositoryName,
          targetBranch,
          sourceBranch,
          providerProfileId: providerProfileId || undefined,
          publishMode: 'None',
          forceRerun,
          baselineRunId: selectedBaselineRunId || undefined,
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
                placeholder="https://tfs.example.local/.../_git/repo/pullrequest/42, https://github.com/org/repo/pull/42 или https://gitlab.example.com/group/repo/-/merge_requests/42"
              />
            </label>
            <PullRequestHistoryPanel
              history={pullRequestHistory}
              isDeleting={pullRequestHistoryDeleting}
              isLoading={pullRequestHistoryLoading}
              error={pullRequestHistoryError}
              onDeleteHistory={onDeletePullRequestHistory}
              onDeleteRun={onDeleteHistoryRun}
              onStopRun={onStopReviewRun}
              onSelectRun={onSelectHistoryRun}
              onSelectBaselineRun={onSelectBaselineRun}
              pullRequestUrl={pullRequestUrl}
              activeRunId={activeRunId}
              selectedBaselineRunId={selectedBaselineRunId}
              selectedRunId={selectedRunId}
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
              onDeleteRun={onDeleteHistoryRun}
              onStopRun={onStopReviewRun}
              onSelectRun={onSelectHistoryRun}
              onSelectBaselineRun={onSelectBaselineRun}
              repositoryName={repositoryName}
              activeRunId={activeRunId}
              sourceBranch={sourceBranch}
              selectedBaselineRunId={selectedBaselineRunId}
              selectedRunId={selectedRunId}
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

        <ServiceHistoryPanel
          history={serviceHistory}
          isLoading={serviceHistoryLoading}
          error={serviceHistoryError}
          query={serviceHistoryQuery}
          activeRunId={activeRunId}
          selectedRunId={selectedRunId}
          onDeleteRun={onDeleteHistoryRun}
          onQueryChange={onServiceHistoryQueryChange}
          onSelectRun={onSelectServiceHistoryRun}
          onStopRun={onStopReviewRun}
        />
      </div>

      <div className="panel-footer">
        <button className="primary" disabled={isSubmitting} onClick={() => void handleSubmit()} type="button">
          {isSubmitting ? 'Запуск ревью...' : 'Запустить ревью'}
        </button>
        <button className="secondary-button" disabled={isSubmitting} onClick={() => void handleSubmit(true)} type="button">
          {isSubmitting ? 'Запуск...' : 'Rerun ревью'}
        </button>
      </div>

      {submitError ? <div className="inline-error">{submitError}</div> : null}
    </section>
  );
}

interface ServiceHistoryPanelProps {
  history: ReviewHistory | null;
  isLoading: boolean;
  error: string | null;
  query: string;
  onQueryChange: (query: string) => void;
  onDeleteRun: (runId: string) => Promise<void>;
  onStopRun: (runId: string) => Promise<void>;
  onSelectRun: (runId: string) => Promise<void>;
  activeRunId?: string;
  selectedRunId?: string;
}

function ServiceHistoryPanel({
  history,
  isLoading,
  error,
  query,
  onQueryChange,
  onDeleteRun,
  onStopRun,
  onSelectRun,
  activeRunId,
  selectedRunId
}: ServiceHistoryPanelProps) {
  const normalizedQuery = query.trim();
  const [isCollapsed, setIsCollapsed] = useState(false);

  return (
    <div className="history-panel service-history-panel form-stack-full">
      <div className="history-panel-header">
        <div>
          <p className="eyebrow">История по сервису</p>
          <h3>Поиск сохранённых ревью</h3>
          <p className="history-selected-summary">
            Поиск идёт по вхождению в имя сервиса, репозиторий, заголовок PR, URL и ветки.
          </p>
        </div>
        <div className="history-actions">
          {isLoading ? <span className="secondary-chip">Ищем...</span> : null}
          {history && history.items.length > 0 ? (
            <button
              aria-expanded={!isCollapsed}
              className="secondary-button"
              onClick={() => setIsCollapsed((value) => !value)}
              type="button"
            >
              {isCollapsed ? 'Показать' : 'Свернуть'}
            </button>
          ) : null}
        </div>
      </div>

      <label className="form-field service-history-search">
        Имя сервиса или часть названия
        <input
          value={query}
          onChange={(event) => onQueryChange(event.target.value)}
          placeholder="Например: Broadband, Marker, Casper или часть URL PR"
        />
      </label>

      {error ? <div className="inline-error">{error}</div> : null}

      {!normalizedQuery && !error ? (
        <p className="history-note">Начни вводить фрагмент имени, чтобы быстро открыть прошлый запуск ревью.</p>
      ) : null}

      {!isCollapsed && !error && !isLoading && history && history.items.length > 0 ? (
        <div className="history-list">
          {history.items.map((item) => {
            const isSelected = item.id === selectedRunId;
            const isActive = item.id === activeRunId;
            const canStopRun = item.status === 'Running' || item.status === 'Pending';
            const canDeleteRun = item.status !== 'Running' && item.status !== 'Pending';
            return (
              <article className={`history-item ${isSelected ? 'selected' : ''} ${isActive ? 'active' : ''}`} key={item.id}>
                <button
                  className="history-item-main"
                  onClick={() => {
                    void onSelectRun(item.id);
                  }}
                  type="button"
                >
                  <div className="history-item-row">
                    <strong>{item.serviceName || item.repositoryName || item.title}</strong>
                    <span className={`status-pill status-${String(item.status).toLowerCase()}`}>{translateStatus(item.status)}</span>
                  </div>
                  <div className="history-meta">{renderHistoryTarget(item)}</div>
                  <div className="history-item-row">
                    <span className="history-meta">{formatTimestamp(item.createdAt)}</span>
                    {isActive ? <span className="secondary-chip">Текущий прогресс</span> : null}
                    {isSelected ? <span className="secondary-chip">Открыт запуск</span> : null}
                  </div>
                  <div className="history-meta">
                    Найдено: {item.findingsCount}
                    {item.criticalCount > 0 ? ` · critical ${item.criticalCount}` : ''}
                    {item.highCount > 0 ? ` · high ${item.highCount}` : ''}
                    {item.authorName ? ` · ${item.authorName}` : ''}
                  </div>
                  <div className="history-meta">Клик откроет результат. Baseline текущего PR не меняется.</div>
                </button>
                <div className="history-item-actions">
                  {canStopRun ? (
                    <button
                      className="secondary-button history-stop-button"
                      onClick={() => {
                        if (!window.confirm('Остановить это ревью?')) {
                          return;
                        }

                        void onStopRun(item.id);
                      }}
                      type="button"
                    >
                      Остановить
                    </button>
                  ) : null}
                  <button
                    className="secondary-button history-delete-button"
                    disabled={!canDeleteRun}
                    onClick={() => {
                      if (!window.confirm('Удалить этот запуск ревью из истории?')) {
                        return;
                      }

                      void onDeleteRun(item.id);
                    }}
                    type="button"
                  >
                    Удалить
                  </button>
                </div>
              </article>
            );
          })}
        </div>
      ) : null}

      {!error && !isLoading && normalizedQuery && history && history.items.length === 0 ? (
        <p className="history-note">По этому фрагменту история ревью пока не найдена.</p>
      ) : null}
    </div>
  );
}

interface BranchHistoryPanelProps {
  history: ReviewHistory | null;
  isDeleting: boolean;
  isLoading: boolean;
  error: string | null;
  onDeleteHistory: (repositoryName: string, sourceBranch: string, targetBranch: string) => Promise<void>;
  onDeleteRun: (runId: string) => Promise<void>;
  onStopRun: (runId: string) => Promise<void>;
  onSelectRun: (runId: string) => Promise<void>;
  onSelectBaselineRun: (runId: string) => void;
  repositoryName: string;
  activeRunId?: string;
  selectedBaselineRunId?: string;
  selectedRunId?: string;
  sourceBranch: string;
  targetBranch: string;
}

function BranchHistoryPanel({
  history,
  isDeleting,
  isLoading,
  error,
  onDeleteHistory,
  onDeleteRun,
  onStopRun,
  onSelectRun,
  onSelectBaselineRun,
  repositoryName,
  activeRunId,
  selectedBaselineRunId,
  selectedRunId,
  sourceBranch,
  targetBranch
}: BranchHistoryPanelProps) {
  const hasSelection = repositoryName.trim().length > 0 && sourceBranch.trim().length > 0;
  const [isCollapsed, setIsCollapsed] = useState(false);
  const selectedItem = getSelectedBaselineHistoryItem(history, selectedBaselineRunId);

  if (!hasSelection && !isLoading && !error && !history) {
    return null;
  }

  return (
    <div className="history-panel form-stack-full">
      <div className="history-panel-header">
        <div>
          <p className="eyebrow">История ревью</p>
          <h3>Предыдущие запуски для этого сравнения веток</h3>
          <p className="history-selected-summary">{renderSelectedBaselineSummary(selectedItem)}</p>
        </div>
        <div className="history-actions">
          {isLoading ? <span className="secondary-chip">Загрузка...</span> : null}
          {history && history.items.length > 0 ? (
            <button
              aria-expanded={!isCollapsed}
              className="secondary-button"
              onClick={() => setIsCollapsed((value) => !value)}
              type="button"
            >
              {isCollapsed ? 'Развернуть историю' : 'Свернуть историю'}
            </button>
          ) : null}
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

      {!isCollapsed && !error && !isLoading && history && history.items.length > 0 ? (
        <>
          {history.baselineRunId ? (
            <p className="history-note">
              Если baseline не выбран вручную, последний завершённый запуск из этого списка будет базой для следующего ревью и блока
              <span className="history-highlight"> Delta Since Previous Review</span>.
            </p>
          ) : (
            <p className="history-note">Завершённых запусков пока нет, поэтому следующее ревью начнётся без baseline.</p>
          )}
          <div className="history-list">
            {history.items.map((item) => {
              const isBaseline = item.id === (selectedBaselineRunId || history.baselineRunId);
              const isSelected = item.id === selectedRunId;
              const isActive = item.id === activeRunId;
              const isDefaultBaseline = item.id === history.baselineRunId;
              const canUseAsBaseline = item.status === 'Completed';
              const canStopRun = item.status === 'Running' || item.status === 'Pending';
              const canDeleteRun = item.status !== 'Running' && item.status !== 'Pending';
              return (
                <article className={`history-item ${isBaseline ? 'baseline' : ''} ${isSelected ? 'selected' : ''} ${isActive ? 'active' : ''}`} key={item.id}>
                  <button
                    className="history-item-main"
                    onClick={() => {
                      if (canUseAsBaseline) {
                        onSelectBaselineRun(item.id);
                      }

                      void onSelectRun(item.id);
                    }}
                    type="button"
                  >
                    <div className="history-item-row">
                      <strong>{item.serviceName || item.title}</strong>
                      <span className={`status-pill status-${String(item.status).toLowerCase()}`}>{translateStatus(item.status)}</span>
                    </div>
                    <div className="history-item-row">
                      <span className="history-meta">{formatTimestamp(item.createdAt)}</span>
                      {isActive ? <span className="secondary-chip">Текущий прогресс</span> : null}
                      {isSelected ? <span className="secondary-chip">Открыт запуск</span> : null}
                      {isBaseline ? <span className="secondary-chip">Выбран baseline</span> : null}
                      {isDefaultBaseline && !selectedBaselineRunId ? <span className="secondary-chip">По умолчанию</span> : null}
                    </div>
                    <div className="history-meta">
                      Найдено: {item.findingsCount}
                      {item.criticalCount > 0 ? ` · critical ${item.criticalCount}` : ''}
                      {item.highCount > 0 ? ` · high ${item.highCount}` : ''}
                      {item.authorName ? ` · ${item.authorName}` : ''}
                    </div>
                    <div className="history-meta">
                      {canUseAsBaseline
                        ? 'Клик: открыть результат и использовать как baseline.'
                        : 'Клик: открыть текущий результат. Baseline доступен после завершения.'}
                    </div>
                  </button>
                  <div className="history-item-actions">
                    {canStopRun ? (
                      <button
                        className="secondary-button history-stop-button"
                        onClick={() => {
                          if (!window.confirm('Остановить это ревью?')) {
                            return;
                          }

                          void onStopRun(item.id);
                        }}
                        type="button"
                      >
                        Остановить
                      </button>
                    ) : null}
                    <button
                      className="secondary-button history-delete-button"
                      disabled={!canDeleteRun}
                      onClick={() => {
                        if (!window.confirm('Удалить этот запуск ревью из истории?')) {
                          return;
                        }

                        void onDeleteRun(item.id);
                      }}
                      type="button"
                    >
                      Удалить
                    </button>
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
  onDeleteRun: (runId: string) => Promise<void>;
  onStopRun: (runId: string) => Promise<void>;
  onSelectRun: (runId: string) => Promise<void>;
  onSelectBaselineRun: (runId: string) => void;
  pullRequestUrl: string;
  activeRunId?: string;
  selectedBaselineRunId?: string;
  selectedRunId?: string;
}

function PullRequestHistoryPanel({
  history,
  isDeleting,
  isLoading,
  error,
  onDeleteHistory,
  onDeleteRun,
  onStopRun,
  onSelectRun,
  onSelectBaselineRun,
  pullRequestUrl,
  activeRunId,
  selectedBaselineRunId,
  selectedRunId
}: PullRequestHistoryPanelProps) {
  const hasUrl = pullRequestUrl.trim().length > 0;
  const [isCollapsed, setIsCollapsed] = useState(false);
  const selectedItem = getSelectedBaselineHistoryItem(history, selectedBaselineRunId);

  if (!hasUrl && !isLoading && !error && !history) {
    return null;
  }

  return (
    <div className="history-panel">
      <div className="history-panel-header">
        <div>
          <p className="eyebrow">История ревью</p>
          <h3>Предыдущие запуски по этому PR</h3>
          <p className="history-selected-summary">{renderSelectedBaselineSummary(selectedItem)}</p>
        </div>
        <div className="history-actions">
          {isLoading ? <span className="secondary-chip">Загрузка...</span> : null}
          {history && history.items.length > 0 ? (
            <button
              aria-expanded={!isCollapsed}
              className="secondary-button"
              onClick={() => setIsCollapsed((value) => !value)}
              type="button"
            >
              {isCollapsed ? 'Развернуть историю' : 'Свернуть историю'}
            </button>
          ) : null}
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

      {!isCollapsed && !error && !isLoading && history && history.items.length > 0 ? (
        <>
          {history.baselineRunId ? (
            <p className="history-note">
              Если baseline не выбран вручную, последний завершённый запуск из этого списка будет базой для следующего ревью и блока
              <span className="history-highlight"> Delta Since Previous Review</span>.
            </p>
          ) : (
            <p className="history-note">Завершённых запусков пока нет, поэтому следующее ревью начнётся без baseline.</p>
          )}
          <div className="history-list">
            {history.items.map((item) => {
              const isBaseline = item.id === (selectedBaselineRunId || history.baselineRunId);
              const isSelected = item.id === selectedRunId;
              const isActive = item.id === activeRunId;
              const isDefaultBaseline = item.id === history.baselineRunId;
              const canUseAsBaseline = item.status === 'Completed';
              const canStopRun = item.status === 'Running' || item.status === 'Pending';
              const canDeleteRun = item.status !== 'Running' && item.status !== 'Pending';
              return (
                <article className={`history-item ${isBaseline ? 'baseline' : ''} ${isSelected ? 'selected' : ''} ${isActive ? 'active' : ''}`} key={item.id}>
                  <button
                    className="history-item-main"
                    onClick={() => {
                      if (canUseAsBaseline) {
                        onSelectBaselineRun(item.id);
                      }

                      void onSelectRun(item.id);
                    }}
                    type="button"
                  >
                    <div className="history-item-row">
                      <strong>{item.serviceName || item.title}</strong>
                      <span className={`status-pill status-${String(item.status).toLowerCase()}`}>{translateStatus(item.status)}</span>
                    </div>
                    <div className="history-item-row">
                      <span className="history-meta">{formatTimestamp(item.createdAt)}</span>
                      {isActive ? <span className="secondary-chip">Текущий прогресс</span> : null}
                      {isSelected ? <span className="secondary-chip">Открыт запуск</span> : null}
                      {isBaseline ? <span className="secondary-chip">Выбран baseline</span> : null}
                      {isDefaultBaseline && !selectedBaselineRunId ? <span className="secondary-chip">По умолчанию</span> : null}
                    </div>
                    <div className="history-meta">
                      Найдено: {item.findingsCount}
                      {item.criticalCount > 0 ? ` · critical ${item.criticalCount}` : ''}
                      {item.highCount > 0 ? ` · high ${item.highCount}` : ''}
                      {item.authorName ? ` · ${item.authorName}` : ''}
                    </div>
                    <div className="history-meta">
                      {canUseAsBaseline
                        ? 'Клик: открыть результат и использовать как baseline.'
                        : 'Клик: открыть текущий результат. Baseline доступен после завершения.'}
                    </div>
                  </button>
                  <div className="history-item-actions">
                    {canStopRun ? (
                      <button
                        className="secondary-button history-stop-button"
                        onClick={() => {
                          if (!window.confirm('Остановить это ревью?')) {
                            return;
                          }

                          void onStopRun(item.id);
                        }}
                        type="button"
                      >
                        Остановить
                      </button>
                    ) : null}
                    <button
                      className="secondary-button history-delete-button"
                      disabled={!canDeleteRun}
                      onClick={() => {
                        if (!window.confirm('Удалить этот запуск ревью из истории?')) {
                          return;
                        }

                        void onDeleteRun(item.id);
                      }}
                      type="button"
                    >
                      Удалить
                    </button>
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

function getSelectedBaselineHistoryItem(
  history: ReviewHistory | null,
  selectedBaselineRunId?: string
): ReviewHistoryItem | undefined {
  if (!history) {
    return undefined;
  }

  const baselineId = selectedBaselineRunId || history.baselineRunId;
  return baselineId ? history.items.find((item) => item.id === baselineId) : undefined;
}

function renderSelectedBaselineSummary(item: ReviewHistoryItem | undefined): string {
  if (!item) {
    return 'Baseline не выбран: будет использован последний завершённый запуск, если он есть.';
  }

  const title = item.serviceName || item.title;
  return `Baseline: ${title} · ${formatTimestamp(item.createdAt)} · ${item.findingsCount} замечаний`;
}

function renderHistoryTarget(item: ReviewHistoryItem): string {
  if (item.targetKind === 'BranchComparison') {
    const repositoryName = item.repositoryName || item.serviceName || item.title;
    const sourceBranch = item.sourceBranch || '?';
    const targetBranch = item.targetBranch || '?';
    return `Ветки: ${repositoryName} · ${sourceBranch} -> ${targetBranch}`;
  }

  return item.pullRequestUrl
    ? `PR: ${item.pullRequestUrl}`
    : `PR: ${item.title}`;
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
    case 'Cancelled':
      return 'Остановлено';
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
