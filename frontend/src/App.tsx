import { useCallback, useEffect, useRef, useState } from 'react';
import { ProgressStream } from './components/ProgressStream';
import { ReviewForm } from './components/ReviewForm';
import { RunDetails } from './components/RunDetails';
import {
  buildDiffDownloadUrl,
  buildEventsUrl,
  buildReportDownloadUrl,
  continueInlineDiscussion,
  deleteBranchReviewHistory,
  deletePullRequestReviewHistory,
  fetchProviderProfiles,
  getBranchReviewHistory,
  getPullRequestReviewHistory,
  getReviewRun,
  publishInlineComment,
  publishReport,
  setInlineCommentRelevance,
  startBranchReview,
  startPullRequestReview
} from './lib/api';
import type {
  BranchReviewPayload,
  ProviderProfile,
  PullRequestReviewPayload,
  ReviewHistory,
  ReviewProgressEvent,
  ReviewRun
} from './lib/types';

type ReviewMode = 'pullRequest' | 'branches';

interface BranchHistorySelection {
  repositoryName: string;
  sourceBranch: string;
  targetBranch: string;
}

function matchesRunSelection(
  run: ReviewRun | null,
  mode: ReviewMode,
  activePullRequestUrl: string,
  activeBranchSelection: BranchHistorySelection
): boolean {
  if (!run) {
    return false;
  }

  return !shouldHideRun(run, mode, activePullRequestUrl, activeBranchSelection);
}

function shouldHideRun(
  run: ReviewRun | null,
  mode: ReviewMode,
  activePullRequestUrl: string,
  activeBranchSelection: BranchHistorySelection
): boolean {
  if (!run || run.status === 'Running') {
    return false;
  }

  if (mode === 'branches') {
    if (run.targetKind !== 'BranchComparison') {
      return true;
    }

    const normalizedRepositoryName = activeBranchSelection.repositoryName.trim();
    const normalizedSourceBranch = activeBranchSelection.sourceBranch.trim();
    const normalizedTargetBranch = activeBranchSelection.targetBranch.trim();
    if (!normalizedRepositoryName || !normalizedSourceBranch || !normalizedTargetBranch) {
      return false;
    }

    return run.repositoryName?.trim() !== normalizedRepositoryName ||
      run.sourceBranch?.trim() !== normalizedSourceBranch ||
      run.targetBranch?.trim() !== normalizedTargetBranch;
  }

  if (run.targetKind !== 'PullRequest') {
    return true;
  }

  const normalizedActiveUrl = activePullRequestUrl.trim();
  if (!normalizedActiveUrl) {
    return false;
  }

  return run.pullRequestUrl?.trim() !== normalizedActiveUrl;
}

export default function App() {
  const [profiles, setProfiles] = useState<ProviderProfile[]>([]);
  const [currentRun, setCurrentRun] = useState<ReviewRun | null>(null);
  const [events, setEvents] = useState<ReviewProgressEvent[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [pullRequestUrl, setPullRequestUrl] = useState('');
  const [pullRequestHistory, setPullRequestHistory] = useState<ReviewHistory | null>(null);
  const [pullRequestHistoryLoading, setPullRequestHistoryLoading] = useState(false);
  const [pullRequestHistoryError, setPullRequestHistoryError] = useState<string | null>(null);
  const [pullRequestHistoryDeleting, setPullRequestHistoryDeleting] = useState(false);
  const [branchHistory, setBranchHistory] = useState<ReviewHistory | null>(null);
  const [branchHistoryLoading, setBranchHistoryLoading] = useState(false);
  const [branchHistoryError, setBranchHistoryError] = useState<string | null>(null);
  const [branchHistoryDeleting, setBranchHistoryDeleting] = useState(false);
  const [branchSelection, setBranchSelection] = useState<BranchHistorySelection>({
    repositoryName: '',
    sourceBranch: '',
    targetBranch: 'master'
  });
  const [reviewMode, setReviewMode] = useState<ReviewMode>('pullRequest');
  const historyRequestIdRef = useRef(0);
  const branchHistoryRequestIdRef = useRef(0);
  const displayedRun = shouldHideRun(currentRun, reviewMode, pullRequestUrl, branchSelection) ? null : currentRun;

  const handleBranchContextChange = useCallback(
    (repositoryName: string, sourceBranch: string, targetBranch: string) => {
      setBranchSelection((current) => {
        if (
          current.repositoryName === repositoryName &&
          current.sourceBranch === sourceBranch &&
          current.targetBranch === targetBranch
        ) {
          return current;
        }

        return { repositoryName, sourceBranch, targetBranch };
      });
    },
    []
  );

  async function refreshRun(runId: string): Promise<ReviewRun | null> {
    try {
      const run = await getReviewRun(runId);
      setCurrentRun(run);
      return run;
    } catch {
      return null;
    }
  }

  async function refreshHistoryForRun(run: ReviewRun): Promise<void> {
    if (run.targetKind === 'PullRequest' && run.pullRequestUrl) {
      try {
        const history = await getPullRequestReviewHistory(run.pullRequestUrl);
        setPullRequestHistory(history);
        setPullRequestHistoryError(null);
      } catch (reason) {
        setPullRequestHistoryError(reason instanceof Error ? reason.message : String(reason));
      }

      return;
    }

    if (run.targetKind === 'BranchComparison' && run.repositoryName && run.sourceBranch && run.targetBranch) {
      try {
        const history = await getBranchReviewHistory(run.repositoryName, run.sourceBranch, run.targetBranch);
        setBranchHistory(history);
        setBranchHistoryError(null);
      } catch (reason) {
        setBranchHistoryError(reason instanceof Error ? reason.message : String(reason));
      }
    }
  }

  useEffect(() => {
    document.title = 'Code Reviewer';
  }, []);

  useEffect(() => {
    void fetchProviderProfiles()
      .then(setProfiles)
      .catch((reason) => setError(reason instanceof Error ? reason.message : String(reason)));
  }, []);

  useEffect(() => {
    if (reviewMode !== 'pullRequest') {
      return;
    }

    const normalizedUrl = pullRequestUrl.trim();
    if (!normalizedUrl) {
      historyRequestIdRef.current += 1;
      setPullRequestHistory(null);
      setPullRequestHistoryError(null);
      setPullRequestHistoryLoading(false);
      setCurrentRun((existing) => {
        if (!shouldHideRun(existing, 'pullRequest', normalizedUrl, branchSelection)) {
          return existing;
        }

        return null;
      });
      return;
    }

    historyRequestIdRef.current += 1;
    const requestId = historyRequestIdRef.current;

    setPullRequestHistory(null);
    setPullRequestHistoryError(null);
    setPullRequestHistoryLoading(false);
    setCurrentRun((existing) => {
      if (!shouldHideRun(existing, 'pullRequest', normalizedUrl, branchSelection)) {
        return existing;
      }

      return null;
    });

    const timeoutId = window.setTimeout(() => {
      if (historyRequestIdRef.current !== requestId) {
        return;
      }

      setPullRequestHistoryLoading(true);
      setPullRequestHistoryError(null);

      void getPullRequestReviewHistory(normalizedUrl)
        .then(async (history) => {
          if (historyRequestIdRef.current !== requestId) {
            return;
          }

          setPullRequestHistory(history);
          if (!history.baselineRunId) {
            setCurrentRun((existing) => (shouldHideRun(existing, 'pullRequest', normalizedUrl, branchSelection) ? null : existing));
          }

          if (history.baselineRunId && !matchesRunSelection(currentRun, 'pullRequest', normalizedUrl, branchSelection)) {
            try {
              const baselineRun = await getReviewRun(history.baselineRunId);
              if (historyRequestIdRef.current !== requestId) {
                return;
              }

              setCurrentRun((existing) =>
                matchesRunSelection(existing, 'pullRequest', normalizedUrl, branchSelection)
                  ? existing
                  : baselineRun
              );
            } catch {
            }
          }
        })
        .catch((reason) => {
          if (historyRequestIdRef.current !== requestId) {
            return;
          }

          setPullRequestHistory(null);
          setPullRequestHistoryError(reason instanceof Error ? reason.message : String(reason));
        })
        .finally(() => {
          if (historyRequestIdRef.current === requestId) {
            setPullRequestHistoryLoading(false);
          }
        });
    }, 350);

    return () => {
      window.clearTimeout(timeoutId);
    };
  }, [reviewMode, pullRequestUrl, branchSelection]);

  useEffect(() => {
    if (reviewMode !== 'branches') {
      return;
    }

    const normalizedRepositoryName = branchSelection.repositoryName.trim();
    const normalizedSourceBranch = branchSelection.sourceBranch.trim();
    const normalizedTargetBranch = branchSelection.targetBranch.trim();
    if (!normalizedRepositoryName || !normalizedSourceBranch || !normalizedTargetBranch) {
      branchHistoryRequestIdRef.current += 1;
      setBranchHistory(null);
      setBranchHistoryError(null);
      setBranchHistoryLoading(false);
      setCurrentRun((existing) => {
        if (!shouldHideRun(existing, 'branches', pullRequestUrl, branchSelection)) {
          return existing;
        }

        return null;
      });
      return;
    }

    branchHistoryRequestIdRef.current += 1;
    const requestId = branchHistoryRequestIdRef.current;

    setBranchHistory(null);
    setBranchHistoryError(null);
    setBranchHistoryLoading(false);
    setCurrentRun((existing) => {
      if (!shouldHideRun(existing, 'branches', pullRequestUrl, branchSelection)) {
        return existing;
      }

      return null;
    });

    const timeoutId = window.setTimeout(() => {
      if (branchHistoryRequestIdRef.current !== requestId) {
        return;
      }

      setBranchHistoryLoading(true);
      setBranchHistoryError(null);

      void getBranchReviewHistory(normalizedRepositoryName, normalizedSourceBranch, normalizedTargetBranch)
        .then(async (history) => {
          if (branchHistoryRequestIdRef.current !== requestId) {
            return;
          }

          setBranchHistory(history);
          if (!history.baselineRunId) {
            setCurrentRun((existing) =>
              shouldHideRun(existing, 'branches', pullRequestUrl, branchSelection) ? null : existing
            );
          }

          if (history.baselineRunId &&
            !matchesRunSelection(
              currentRun,
              'branches',
              pullRequestUrl,
              {
                repositoryName: normalizedRepositoryName,
                sourceBranch: normalizedSourceBranch,
                targetBranch: normalizedTargetBranch
              })) {
            try {
              const baselineRun = await getReviewRun(history.baselineRunId);
              if (branchHistoryRequestIdRef.current !== requestId) {
                return;
              }

              setCurrentRun((existing) => {
                if (matchesRunSelection(
                  existing,
                  'branches',
                  pullRequestUrl,
                  {
                    repositoryName: normalizedRepositoryName,
                    sourceBranch: normalizedSourceBranch,
                    targetBranch: normalizedTargetBranch
                  })) {
                  return existing;
                }

                return baselineRun;
              });
            } catch {
            }
          }
        })
        .catch((reason) => {
          if (branchHistoryRequestIdRef.current !== requestId) {
            return;
          }

          setBranchHistory(null);
          setBranchHistoryError(reason instanceof Error ? reason.message : String(reason));
        })
        .finally(() => {
          if (branchHistoryRequestIdRef.current === requestId) {
            setBranchHistoryLoading(false);
          }
        });
    }, 350);

    return () => {
      window.clearTimeout(timeoutId);
    };
  }, [reviewMode, branchSelection, pullRequestUrl]);

  useEffect(() => {
    if (!currentRun) {
      return;
    }

    const eventSource = new EventSource(buildEventsUrl(currentRun.id));

    eventSource.addEventListener('snapshot', (event) => {
      const run = JSON.parse(event.data) as ReviewRun;
      setCurrentRun(run);
      setEvents([]);
    });

    const onProgress = async (event: MessageEvent<string>) => {
      const progressEvent = JSON.parse(event.data) as ReviewProgressEvent;
      setEvents((existing) => [...existing, progressEvent]);
      setCurrentRun((existing) =>
        existing
          ? {
              ...existing,
              status: progressEvent.status,
              currentStage: progressEvent.stage,
              progressPercent: progressEvent.progressPercent,
              currentMessage: progressEvent.message,
              updatedAt: progressEvent.timestamp
            }
          : existing
      );
      await refreshRun(progressEvent.runId);
    };

    const onCompleted = async (event: MessageEvent<string>) => {
      const progressEvent = JSON.parse(event.data) as ReviewProgressEvent;
      setEvents((existing) => [...existing, progressEvent]);
      const run = await refreshRun(progressEvent.runId);
      if (run) {
        await refreshHistoryForRun(run);
      }
      eventSource.close();
    };

    const progressListener: EventListener = (event) => {
      void onProgress(event as MessageEvent<string>);
    };
    const completedListener: EventListener = (event) => {
      void onCompleted(event as MessageEvent<string>);
    };

    eventSource.addEventListener('progress', progressListener);
    eventSource.addEventListener('completed', completedListener);
    eventSource.onerror = () => {
      eventSource.close();
    };

    return () => {
      eventSource.removeEventListener('progress', progressListener);
      eventSource.removeEventListener('completed', completedListener);
      eventSource.close();
    };
  }, [currentRun?.id]);

  async function handleStartPullRequestReview(payload: PullRequestReviewPayload): Promise<void> {
    setError(null);
    setEvents([]);
    const run = await startPullRequestReview(payload);
    setCurrentRun(run);
  }

  async function handleStartBranchReview(payload: BranchReviewPayload): Promise<void> {
    setError(null);
    setEvents([]);
    const run = await startBranchReview(payload);
    setCurrentRun(run);
  }

  async function handlePublishInlineComment(commentId: string): Promise<void> {
    if (!currentRun) {
      return;
    }

    setError(null);
    const run = await publishInlineComment(currentRun.id, commentId);
    setCurrentRun(run);
  }

  async function handleAskInlineQuestion(commentId: string, message: string): Promise<void> {
    if (!currentRun) {
      return;
    }

    setError(null);
    const run = await continueInlineDiscussion(currentRun.id, commentId, message);
    setCurrentRun(run);
  }

  async function handlePublishReport(): Promise<void> {
    if (!currentRun) {
      return;
    }

    setError(null);
    const run = await publishReport(currentRun.id);
    setCurrentRun(run);
  }

  async function handleSetInlineCommentRelevance(commentId: string, isRelevant: boolean): Promise<void> {
    if (!currentRun) {
      return;
    }

    setError(null);
    const run = await setInlineCommentRelevance(currentRun.id, commentId, isRelevant);
    setCurrentRun(run);
  }

  async function handleDeletePullRequestHistory(url: string): Promise<void> {
    const normalizedUrl = url.trim();
    if (!normalizedUrl) {
      return;
    }

    setPullRequestHistoryDeleting(true);
    setError(null);
    try {
      await deletePullRequestReviewHistory(normalizedUrl);
      setPullRequestHistory({ baselineRunId: undefined, items: [] });
      setPullRequestHistoryError(null);
      setCurrentRun((existing) => {
        if (!existing) {
          return existing;
        }

        return existing.targetKind === 'PullRequest' && existing.title === normalizedUrl ? null : existing;
      });
    } catch (reason) {
      setPullRequestHistoryError(reason instanceof Error ? reason.message : String(reason));
    } finally {
      setPullRequestHistoryDeleting(false);
    }
  }

  async function handleDeleteBranchHistory(
    repositoryName: string,
    sourceBranch: string,
    targetBranch: string
  ): Promise<void> {
    const normalizedRepositoryName = repositoryName.trim();
    const normalizedSourceBranch = sourceBranch.trim();
    const normalizedTargetBranch = targetBranch.trim();
    if (!normalizedRepositoryName || !normalizedSourceBranch || !normalizedTargetBranch) {
      return;
    }

    setBranchHistoryDeleting(true);
    setError(null);
    try {
      await deleteBranchReviewHistory(normalizedRepositoryName, normalizedSourceBranch, normalizedTargetBranch);
      setBranchHistory({ baselineRunId: undefined, items: [] });
      setBranchHistoryError(null);
      setCurrentRun((existing) => {
        if (!existing) {
          return existing;
        }

        return existing.targetKind === 'BranchComparison' &&
          existing.repositoryName?.trim() === normalizedRepositoryName &&
          existing.sourceBranch?.trim() === normalizedSourceBranch &&
          existing.targetBranch?.trim() === normalizedTargetBranch
          ? null
          : existing;
      });
    } catch (reason) {
      setBranchHistoryError(reason instanceof Error ? reason.message : String(reason));
    } finally {
      setBranchHistoryDeleting(false);
    }
  }

  return (
    <main className="app-shell">
      <section className="hero">
        <div>
          <p className="eyebrow">AI Reviewer</p>
          <h1>🕵️‍♂️ Code Reviewer</h1>
        </div>
        <p className="hero-copy">
          Запускайте ревью по ссылке на pull request или по сравнению веток, отслеживайте прогресс по SSE и
          публикуйте итоговый комментарий и inline-замечания обратно в GitHub, GitLab или Azure DevOps/TFS.
        </p>
      </section>

      {error ? <div className="error-banner">{error}</div> : null}

      <div className="layout-grid">
        <ReviewForm
          branchHistory={branchHistory}
          branchHistoryDeleting={branchHistoryDeleting}
          branchHistoryError={branchHistoryError}
          branchHistoryLoading={branchHistoryLoading}
          onBranchContextChange={handleBranchContextChange}
          onDeleteBranchHistory={handleDeleteBranchHistory}
          pullRequestHistoryDeleting={pullRequestHistoryDeleting}
          pullRequestHistory={pullRequestHistory}
          pullRequestHistoryError={pullRequestHistoryError}
          pullRequestHistoryLoading={pullRequestHistoryLoading}
          profiles={profiles}
          onDeletePullRequestHistory={handleDeletePullRequestHistory}
          onModeChange={setReviewMode}
          onPullRequestUrlChange={setPullRequestUrl}
          onStartPullRequestReview={handleStartPullRequestReview}
          onStartBranchReview={handleStartBranchReview}
        />
        <ProgressStream events={events} run={displayedRun} />
      </div>

      <RunDetails
        diffDownloadUrl={displayedRun ? buildDiffDownloadUrl(displayedRun.id) : undefined}
        onAskInlineQuestion={handleAskInlineQuestion}
        onPublishReport={handlePublishReport}
        onPublishInlineComment={handlePublishInlineComment}
        onSetInlineCommentRelevance={handleSetInlineCommentRelevance}
        reportDownloadUrl={displayedRun ? buildReportDownloadUrl(displayedRun.id) : undefined}
        run={displayedRun}
      />
    </main>
  );
}
