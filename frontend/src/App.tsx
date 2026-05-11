import { useCallback, useEffect, useRef, useState } from 'react';
import { ProgressStream } from './components/ProgressStream';
import { ReviewForm } from './components/ReviewForm';
import { RunDetails } from './components/RunDetails';
import {
  buildDiffDownloadUrl,
  buildEventsUrl,
  buildReportDownloadUrl,
  continueReviewDiscussion,
  continueInlineDiscussion,
  deleteReviewRun,
  deleteBranchReviewHistory,
  deletePullRequestReviewHistory,
  fetchProviderProfiles,
  getBranchReviewHistory,
  getPullRequestReviewHistory,
  getServiceReviewHistory,
  getReviewRun,
  publishInlineComment,
  publishReport,
  regenerateReviewArtifacts,
  setInlineCommentRelevance,
  stopReviewRun,
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
  const [activeRun, setActiveRun] = useState<ReviewRun | null>(null);
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
  const [serviceHistoryQuery, setServiceHistoryQuery] = useState('');
  const [serviceHistory, setServiceHistory] = useState<ReviewHistory | null>(null);
  const [serviceHistoryLoading, setServiceHistoryLoading] = useState(false);
  const [serviceHistoryError, setServiceHistoryError] = useState<string | null>(null);
  const [serviceHistorySelectedRunId, setServiceHistorySelectedRunId] = useState<string | undefined>();
  const [selectedBaselineRunId, setSelectedBaselineRunId] = useState<string | undefined>();
  const [branchSelection, setBranchSelection] = useState<BranchHistorySelection>({
    repositoryName: '',
    sourceBranch: '',
    targetBranch: 'master'
  });
  const [reviewMode, setReviewMode] = useState<ReviewMode>('pullRequest');
  const historyRequestIdRef = useRef(0);
  const branchHistoryRequestIdRef = useRef(0);
  const serviceHistoryRequestIdRef = useRef(0);
  const displayedRun = currentRun?.id === serviceHistorySelectedRunId
    ? currentRun
    : shouldHideRun(currentRun, reviewMode, pullRequestUrl, branchSelection) ? null : currentRun;
  const progressRun = shouldHideRun(activeRun, reviewMode, pullRequestUrl, branchSelection) ? null : activeRun;
  const liveProgressRun = progressRun?.status === 'Running' || progressRun?.status === 'Pending'
    ? progressRun
    : null;
  const progressDisplayRun = liveProgressRun ?? displayedRun;
  const progressEvents = liveProgressRun
    ? events
    : displayedRun?.progressUpdates ?? [];

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

        setSelectedBaselineRunId(undefined);
        setServiceHistorySelectedRunId(undefined);
        return { repositoryName, sourceBranch, targetBranch };
      });
    },
    []
  );

  async function refreshRun(runId: string): Promise<ReviewRun | null> {
    try {
      const run = await getReviewRun(runId);
      setCurrentRun((existing) => (existing?.id === runId ? run : existing));
      setActiveRun((existing) => (existing?.id === runId ? run : existing));
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

      await refreshServiceHistory();
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

    await refreshServiceHistory();
  }

  async function refreshServiceHistory(query = serviceHistoryQuery.trim()): Promise<void> {
    if (!query) {
      return;
    }

    try {
      const history = await getServiceReviewHistory(query);
      setServiceHistory(history);
      setServiceHistoryError(null);
    } catch (reason) {
      setServiceHistoryError(reason instanceof Error ? reason.message : String(reason));
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
      setSelectedBaselineRunId(undefined);
      setServiceHistorySelectedRunId(undefined);
      setPullRequestHistory(null);
      setPullRequestHistoryError(null);
      setPullRequestHistoryLoading(false);
      setCurrentRun((existing) => {
        if (!shouldHideRun(existing, 'pullRequest', normalizedUrl, branchSelection)) {
          return existing;
        }

        return null;
      });
      setActiveRun((existing) => {
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
    setSelectedBaselineRunId(undefined);
    setServiceHistorySelectedRunId(undefined);
    setPullRequestHistoryError(null);
    setPullRequestHistoryLoading(false);
    setCurrentRun((existing) => {
      if (!shouldHideRun(existing, 'pullRequest', normalizedUrl, branchSelection)) {
        return existing;
      }

      return null;
    });
    setActiveRun((existing) => {
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
      setSelectedBaselineRunId(undefined);
      setServiceHistorySelectedRunId(undefined);
      setBranchHistory(null);
      setBranchHistoryError(null);
      setBranchHistoryLoading(false);
      setCurrentRun((existing) => {
        if (!shouldHideRun(existing, 'branches', pullRequestUrl, branchSelection)) {
          return existing;
        }

        return null;
      });
      setActiveRun((existing) => {
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
    setSelectedBaselineRunId(undefined);
    setServiceHistorySelectedRunId(undefined);
    setBranchHistoryError(null);
    setBranchHistoryLoading(false);
    setCurrentRun((existing) => {
      if (!shouldHideRun(existing, 'branches', pullRequestUrl, branchSelection)) {
        return existing;
      }

      return null;
    });
    setActiveRun((existing) => {
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
    const normalizedQuery = serviceHistoryQuery.trim();
    serviceHistoryRequestIdRef.current += 1;
    const requestId = serviceHistoryRequestIdRef.current;

    if (!normalizedQuery) {
      setServiceHistory(null);
      setServiceHistoryError(null);
      setServiceHistoryLoading(false);
      setServiceHistorySelectedRunId(undefined);
      return;
    }

    setServiceHistoryError(null);
    setServiceHistoryLoading(false);
    setServiceHistory(null);

    const timeoutId = window.setTimeout(() => {
      if (serviceHistoryRequestIdRef.current !== requestId) {
        return;
      }

      setServiceHistoryLoading(true);
      void getServiceReviewHistory(normalizedQuery)
        .then((history) => {
          if (serviceHistoryRequestIdRef.current !== requestId) {
            return;
          }

          setServiceHistory(history);
        })
        .catch((reason) => {
          if (serviceHistoryRequestIdRef.current !== requestId) {
            return;
          }

          setServiceHistory(null);
          setServiceHistoryError(reason instanceof Error ? reason.message : String(reason));
        })
        .finally(() => {
          if (serviceHistoryRequestIdRef.current === requestId) {
            setServiceHistoryLoading(false);
          }
        });
    }, 300);

    return () => {
      window.clearTimeout(timeoutId);
    };
  }, [serviceHistoryQuery]);

  useEffect(() => {
    if (!activeRun) {
      return;
    }

    const subscribedRunId = activeRun.id;
    let active = true;
    const eventSource = new EventSource(buildEventsUrl(subscribedRunId));

    eventSource.addEventListener('snapshot', (event) => {
      if (!active) {
        return;
      }

      const run = JSON.parse(event.data) as ReviewRun;
      setActiveRun((existing) => (existing?.id === subscribedRunId ? run : existing));
      setCurrentRun((existing) => (existing?.id === subscribedRunId ? run : existing));
      setEvents([]);
    });

    const onProgress = async (event: MessageEvent<string>) => {
      const progressEvent = JSON.parse(event.data) as ReviewProgressEvent;
      if (!active || progressEvent.runId !== subscribedRunId) {
        return;
      }

      setEvents((existing) => [...existing, progressEvent]);
      setActiveRun((existing) =>
        existing?.id === subscribedRunId
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
      setCurrentRun((existing) =>
        existing?.id === subscribedRunId
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
      if (!active || progressEvent.runId !== subscribedRunId) {
        return;
      }

      setEvents((existing) => [...existing, progressEvent]);
      const run = await refreshRun(progressEvent.runId);
      if (active && run) {
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
      active = false;
      eventSource.removeEventListener('progress', progressListener);
      eventSource.removeEventListener('completed', completedListener);
      eventSource.close();
    };
  }, [activeRun?.id]);

  async function handleStartPullRequestReview(payload: PullRequestReviewPayload): Promise<void> {
    setError(null);
    setEvents([]);
    setServiceHistorySelectedRunId(undefined);
    const run = await startPullRequestReview({
      ...payload,
      baselineRunId: payload.baselineRunId || selectedBaselineRunId
    });
    setCurrentRun(run);
    setActiveRun(run);
    await refreshHistoryForRun(run);
  }

  async function handleStartBranchReview(payload: BranchReviewPayload): Promise<void> {
    setError(null);
    setEvents([]);
    setServiceHistorySelectedRunId(undefined);
    const run = await startBranchReview({
      ...payload,
      baselineRunId: payload.baselineRunId || selectedBaselineRunId
    });
    setCurrentRun(run);
    setActiveRun(run);
    await refreshHistoryForRun(run);
  }

  async function handleSelectHistoryRun(runId: string): Promise<void> {
    setError(null);
    setServiceHistorySelectedRunId(undefined);
    const run = await getReviewRun(runId);
    setCurrentRun(run);
    if (run.status === 'Running' || run.status === 'Pending') {
      setActiveRun(run);
      setEvents((existing) => (activeRun?.id === run.id ? existing : []));
    }
  }

  async function handleSelectServiceHistoryRun(runId: string): Promise<void> {
    setError(null);
    const run = await getReviewRun(runId);
    setCurrentRun(run);
    setServiceHistorySelectedRunId(run.id);
    if (run.status === 'Running' || run.status === 'Pending') {
      setActiveRun(run);
      setEvents((existing) => (activeRun?.id === run.id ? existing : []));
    }
  }

  async function handleDeleteHistoryRun(runId: string): Promise<void> {
    setError(null);
    try {
      await deleteReviewRun(runId);
      setPullRequestHistory((existing) =>
        existing
          ? {
              baselineRunId: existing.baselineRunId === runId ? undefined : existing.baselineRunId,
              items: existing.items.filter((item) => item.id !== runId)
            }
          : existing
      );
      setBranchHistory((existing) =>
        existing
          ? {
              baselineRunId: existing.baselineRunId === runId ? undefined : existing.baselineRunId,
              items: existing.items.filter((item) => item.id !== runId)
            }
          : existing
      );
      setServiceHistory((existing) =>
        existing
          ? {
              baselineRunId: existing.baselineRunId === runId ? undefined : existing.baselineRunId,
              items: existing.items.filter((item) => item.id !== runId)
            }
          : existing
      );
      setSelectedBaselineRunId((existing) => (existing === runId ? undefined : existing));
      setServiceHistorySelectedRunId((existing) => (existing === runId ? undefined : existing));
      setCurrentRun((existing) => (existing?.id === runId ? null : existing));
      setActiveRun((existing) => (existing?.id === runId ? null : existing));
      setEvents((existing) => (activeRun?.id === runId ? [] : existing));
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason));
    }
  }

  async function handleStopReviewRun(runId: string): Promise<void> {
    setError(null);
    try {
      const run = await stopReviewRun(runId);
      setCurrentRun((existing) => (existing?.id === runId ? run : existing));
      setActiveRun((existing) => (existing?.id === runId ? run : existing));
      await refreshHistoryForRun(run);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason));
    }
  }

  async function handleRegenerateReviewArtifacts(
    regenerateDescription: boolean,
    regenerateDiagram: boolean
  ): Promise<void> {
    if (!currentRun) {
      return;
    }

    setError(null);
    const run = await regenerateReviewArtifacts(currentRun.id, { regenerateDescription, regenerateDiagram });
    setCurrentRun(run);
    await refreshHistoryForRun(run);
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

  async function handleAskReviewQuestion(message: string): Promise<void> {
    if (!currentRun) {
      return;
    }

    setError(null);
    const run = await continueReviewDiscussion(currentRun.id, message);
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
      setServiceHistory((existing) =>
        existing
          ? {
              baselineRunId: existing.items.some((item) =>
                item.id === existing.baselineRunId &&
                item.targetKind === 'PullRequest' &&
                item.pullRequestUrl?.trim() === normalizedUrl)
                ? undefined
                : existing.baselineRunId,
              items: existing.items.filter((item) =>
                item.targetKind !== 'PullRequest' ||
                item.pullRequestUrl?.trim() !== normalizedUrl)
            }
          : existing
      );
      setSelectedBaselineRunId(undefined);
      setPullRequestHistoryError(null);
      setEvents([]);
      setActiveRun((existing) => {
        if (!existing) {
          return existing;
        }

        return existing.targetKind === 'PullRequest' &&
          existing.pullRequestUrl?.trim() === normalizedUrl
          ? null
          : existing;
      });
      setCurrentRun((existing) => {
        if (!existing) {
          return existing;
        }

        return existing.targetKind === 'PullRequest' &&
          existing.pullRequestUrl?.trim() === normalizedUrl
          ? null
          : existing;
      });
    } catch (reason) {
      setPullRequestHistoryError(reason instanceof Error ? reason.message : String(reason));
    } finally {
      setPullRequestHistoryDeleting(false);
    }
  }

  function handlePullRequestUrlChange(url: string): void {
    setPullRequestUrl(url);
    setSelectedBaselineRunId(undefined);
    setServiceHistorySelectedRunId(undefined);
  }

  function handleSelectBaselineRun(runId: string): void {
    setSelectedBaselineRunId(runId);
  }

  function handleModeChange(mode: ReviewMode): void {
    setReviewMode(mode);
    setSelectedBaselineRunId(undefined);
    setServiceHistorySelectedRunId(undefined);
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
      setServiceHistory((existing) =>
        existing
          ? {
              baselineRunId: existing.items.some((item) =>
                item.id === existing.baselineRunId &&
                item.targetKind === 'BranchComparison' &&
                item.repositoryName?.trim() === normalizedRepositoryName &&
                item.sourceBranch?.trim() === normalizedSourceBranch &&
                item.targetBranch?.trim() === normalizedTargetBranch)
                ? undefined
                : existing.baselineRunId,
              items: existing.items.filter((item) =>
                item.targetKind !== 'BranchComparison' ||
                item.repositoryName?.trim() !== normalizedRepositoryName ||
                item.sourceBranch?.trim() !== normalizedSourceBranch ||
                item.targetBranch?.trim() !== normalizedTargetBranch)
            }
          : existing
      );
      setSelectedBaselineRunId(undefined);
      setBranchHistoryError(null);
      setEvents([]);
      setActiveRun((existing) => {
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
          onDeleteHistoryRun={handleDeleteHistoryRun}
          onStopReviewRun={handleStopReviewRun}
          pullRequestHistoryDeleting={pullRequestHistoryDeleting}
          pullRequestHistory={pullRequestHistory}
          pullRequestHistoryError={pullRequestHistoryError}
          pullRequestHistoryLoading={pullRequestHistoryLoading}
          profiles={profiles}
          serviceHistory={serviceHistory}
          serviceHistoryError={serviceHistoryError}
          serviceHistoryLoading={serviceHistoryLoading}
          serviceHistoryQuery={serviceHistoryQuery}
          onDeletePullRequestHistory={handleDeletePullRequestHistory}
          onSelectHistoryRun={handleSelectHistoryRun}
          onSelectServiceHistoryRun={handleSelectServiceHistoryRun}
          onModeChange={handleModeChange}
          onPullRequestUrlChange={handlePullRequestUrlChange}
          onServiceHistoryQueryChange={setServiceHistoryQuery}
          onSelectBaselineRun={handleSelectBaselineRun}
          onStartPullRequestReview={handleStartPullRequestReview}
          onStartBranchReview={handleStartBranchReview}
          selectedBaselineRunId={selectedBaselineRunId}
          activeRunId={liveProgressRun?.id}
          selectedRunId={displayedRun?.id}
        />
        <ProgressStream events={progressEvents} run={progressDisplayRun} />
      </div>

      <RunDetails
        diffDownloadUrl={displayedRun ? buildDiffDownloadUrl(displayedRun.id) : undefined}
        onAskInlineQuestion={handleAskInlineQuestion}
        onAskReviewQuestion={handleAskReviewQuestion}
        onPublishReport={handlePublishReport}
        onPublishInlineComment={handlePublishInlineComment}
        onRegenerateArtifacts={handleRegenerateReviewArtifacts}
        onSetInlineCommentRelevance={handleSetInlineCommentRelevance}
        onStopReviewRun={handleStopReviewRun}
        reportDownloadUrl={displayedRun ? buildReportDownloadUrl(displayedRun.id) : undefined}
        run={displayedRun}
      />
    </main>
  );
}
