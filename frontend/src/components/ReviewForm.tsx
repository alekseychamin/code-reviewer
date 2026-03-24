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
  const [publishMode, setPublishMode] = useState<PublishMode>('SummaryOnly');
  const [pullRequestUrl, setPullRequestUrl] = useState('');
  const [azureDevOpsAccessToken, setAzureDevOpsAccessToken] = useState('');
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
        return 'Enter a pull request URL.';
      }

      if (!localOnlyMode && publishMode !== 'None' && !azureDevOpsAccessToken.trim()) {
        return 'Azure DevOps/TFS PAT is required when publish mode is enabled.';
      }

      return null;
    }

    if (!repositoryPath.trim()) {
      return 'Enter an absolute repository path for branch comparison.';
    }

    if (!sourceBranch.trim()) {
      return 'Enter the source branch to compare.';
    }

    if (!targetBranch.trim()) {
      return 'Enter the target branch to compare against.';
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
          azureDevOpsAccessToken: azureDevOpsAccessToken || undefined,
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
          <p className="eyebrow">Start Review</p>
          <h2>Trigger a production-style AI review run</h2>
        </div>
        <div className="segmented-control">
          <button
            className={mode === 'pullRequest' ? 'active' : ''}
            onClick={() => setMode('pullRequest')}
            type="button"
          >
            Pull request URL
          </button>
          <button
            className={mode === 'branches' ? 'active' : ''}
            onClick={() => setMode('branches')}
            type="button"
          >
            Branch comparison
          </button>
        </div>
      </div>

      <div className="form-grid">
        {mode === 'pullRequest' ? (
          <>
            <label>
              Pull request URL
              <input
                value={pullRequestUrl}
                onChange={(event) => setPullRequestUrl(event.target.value)}
                placeholder="https://tfs.example.local/.../_git/repo/pullrequest/42"
              />
            </label>
            <label>
              Azure DevOps/TFS PAT
              <input
                value={azureDevOpsAccessToken}
                onChange={(event) => setAzureDevOpsAccessToken(event.target.value)}
                type="password"
                placeholder="Used for diff acquisition and comment publishing"
              />
            </label>
          </>
        ) : (
          <>
            <label>
              Repository path
              <input
                value={repositoryPath}
                onChange={(event) => setRepositoryPath(event.target.value)}
                placeholder="/Users/alex/Documents/Projects/programs/your-repo"
              />
            </label>
            <label>
              Repository label
              <input
                value={repositoryName}
                onChange={(event) => setRepositoryName(event.target.value)}
                placeholder="Optional display name"
              />
            </label>
            <label>
              Target branch
              <input value={targetBranch} onChange={(event) => setTargetBranch(event.target.value)} />
            </label>
            <label>
              Source branch
              <input value={sourceBranch} onChange={(event) => setSourceBranch(event.target.value)} />
            </label>
          </>
        )}

        <label>
          Provider profile
          <select value={providerProfileId} onChange={(event) => setProviderProfileId(event.target.value)}>
            <option value="">Use routed default</option>
            {filteredProfiles.map((profile) => (
              <option key={profile.id} value={profile.id}>
                {profile.name} · {profile.defaultModel}
              </option>
            ))}
          </select>
        </label>

        <label>
          Publish mode
          <select
            disabled={mode === 'branches'}
            value={mode === 'branches' ? 'None' : publishMode}
            onChange={(event) => setPublishMode(event.target.value as PublishMode)}
          >
            {publishModes.map((modeValue) => (
              <option key={modeValue} value={modeValue}>
                {modeValue}
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
        Local-only mode with Ollama routing
      </label>

      <div className="panel-footer">
        <p>
          Thin controllers, stage-based LLM routing, in-memory persistence, SSE progress, and Azure DevOps/TFS
          publishing are wired from the same API.
        </p>
        <button className="primary" disabled={isSubmitting} onClick={() => void handleSubmit()} type="button">
          {isSubmitting ? 'Starting review...' : 'Start review run'}
        </button>
      </div>

      {submitError ? <div className="inline-error">{submitError}</div> : null}
    </section>
  );
}
