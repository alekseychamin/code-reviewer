import { useState } from 'react';
import type {
  BranchReviewPayload,
  ProviderProfile,
  PublishMode,
  PullRequestReviewPayload
} from '../lib/types';

type ReviewMode = 'pullRequest' | 'branches';

interface ReviewFormProps {
  profiles: ProviderProfile[];
  onStartPullRequestReview: (payload: PullRequestReviewPayload) => Promise<void>;
  onStartBranchReview: (payload: BranchReviewPayload) => Promise<void>;
}

const publishModes: PublishMode[] = ['None', 'SummaryOnly', 'SummaryAndInline'];

export function ReviewForm({
  profiles,
  onStartPullRequestReview,
  onStartBranchReview
}: ReviewFormProps) {
  const [mode, setMode] = useState<ReviewMode>('pullRequest');
  const [providerProfileId, setProviderProfileId] = useState('');
  const [localOnlyMode, setLocalOnlyMode] = useState(false);
  const [publishMode, setPublishMode] = useState<PublishMode>('None');
  const [pullRequestUrl, setPullRequestUrl] = useState('');
  const [repositoryPath, setRepositoryPath] = useState('');
  const [repositoryName, setRepositoryName] = useState('');
  const [targetBranch, setTargetBranch] = useState('main');
  const [sourceBranch, setSourceBranch] = useState('');
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);

  const filteredProfiles = localOnlyMode
    ? profiles.filter((profile) => profile.localOnly)
    : profiles;

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
          localOnlyMode,
          publishMode,
          stageOverrides: []
        });
      } else {
        await onStartBranchReview({
          repositoryPath,
          repositoryName: repositoryName || undefined,
          targetBranch,
          sourceBranch,
          providerProfileId: providerProfileId || undefined,
          localOnlyMode,
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
          <>
            <label>
              Ссылка на pull request
              <input
                value={pullRequestUrl}
                onChange={(event) => setPullRequestUrl(event.target.value)}
                placeholder="https://tfs.example.local/.../_git/repo/pullrequest/42"
              />
            </label>
          </>
        ) : (
          <>
            <label>
              Путь к репозиторию
              <input
                value={repositoryPath}
                onChange={(event) => setRepositoryPath(event.target.value)}
                placeholder="/Users/alex/Documents/Projects/programs/your-repo"
              />
            </label>
            <label>
              Название репозитория
              <input
                value={repositoryName}
                onChange={(event) => setRepositoryName(event.target.value)}
                placeholder="Необязательное отображаемое имя"
              />
            </label>
            <label>
              Целевая ветка
              <input value={targetBranch} onChange={(event) => setTargetBranch(event.target.value)} />
            </label>
            <label>
              Исходная ветка
              <input value={sourceBranch} onChange={(event) => setSourceBranch(event.target.value)} />
            </label>
          </>
        )}

        <label>
          Профиль провайдера
          <select value={providerProfileId} onChange={(event) => setProviderProfileId(event.target.value)}>
            <option value="">Использовать маршрут по умолчанию</option>
            {filteredProfiles.map((profile) => (
              <option key={profile.id} value={profile.id}>
                {profile.name} · {profile.defaultModel}
              </option>
            ))}
          </select>
        </label>

        <label>
          Режим публикации
          <select
            disabled={mode === 'branches'}
            value={mode === 'branches' ? 'None' : publishMode}
            onChange={(event) => setPublishMode(event.target.value as PublishMode)}
          >
            {publishModes.map((modeValue) => (
              <option key={modeValue} value={modeValue}>
                {translatePublishMode(modeValue)}
              </option>
            ))}
          </select>
        </label>
      </div>

      <label className="checkbox">
        <input
          checked={localOnlyMode}
          onChange={(event) => {
            setLocalOnlyMode(event.target.checked);
            if (event.target.checked) {
              setPublishMode('None');
            }
          }}
          type="checkbox"
        />
        Локальный режим только через Ollama
      </label>

      <div className="panel-footer">
        <p>
          Тонкие контроллеры, stage-based маршрутизация LLM, in-memory persistence, SSE-прогресс и публикация в
          Azure DevOps/TFS работают через один API. PAT для Azure DevOps/TFS backend читает из
          `AZURE_DEVOPS_TOKEN` в `.env`.
        </p>
        <button className="primary" disabled={isSubmitting} onClick={() => void handleSubmit()} type="button">
          {isSubmitting ? 'Запуск ревью...' : 'Запустить ревью'}
        </button>
      </div>

      {submitError ? <div className="inline-error">{submitError}</div> : null}
    </section>
  );
}

function translatePublishMode(mode: PublishMode): string {
  switch (mode) {
    case 'None':
      return 'Не публиковать';
    case 'SummaryOnly':
      return 'Только summary';
    case 'SummaryAndInline':
      return 'Summary и inline';
    default:
      return mode;
  }
}
