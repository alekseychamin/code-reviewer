import { useEffect, useState } from 'react';
import { ProgressStream } from './components/ProgressStream';
import { ReviewForm } from './components/ReviewForm';
import { RunDetails } from './components/RunDetails';
import {
  buildEventsUrl,
  fetchProviderProfiles,
  getReviewRun,
  startBranchReview,
  startPullRequestReview
} from './lib/api';
import type {
  BranchReviewPayload,
  ProviderProfile,
  PullRequestReviewPayload,
  ReviewProgressEvent,
  ReviewRun
} from './lib/types';

export default function App() {
  const [profiles, setProfiles] = useState<ProviderProfile[]>([]);
  const [currentRun, setCurrentRun] = useState<ReviewRun | null>(null);
  const [events, setEvents] = useState<ReviewProgressEvent[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    void fetchProviderProfiles()
      .then(setProfiles)
      .catch((reason) => setError(reason instanceof Error ? reason.message : String(reason)));
  }, []);

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

    const onProgress = (event: MessageEvent<string>) => {
      const progressEvent = JSON.parse(event.data) as ReviewProgressEvent;
      setEvents((existing) => [...existing, progressEvent]);
    };

    const onCompleted = async (event: MessageEvent<string>) => {
      const progressEvent = JSON.parse(event.data) as ReviewProgressEvent;
      setEvents((existing) => [...existing, progressEvent]);
      const run = await getReviewRun(currentRun.id);
      setCurrentRun(run);
      eventSource.close();
    };

    eventSource.addEventListener('progress', onProgress as EventListener);
    eventSource.addEventListener('completed', onCompleted as EventListener);
    eventSource.onerror = () => {
      eventSource.close();
    };

    return () => eventSource.close();
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

  return (
    <main className="app-shell">
      <section className="hero">
        <div>
          <p className="eyebrow">AI Code Review Platform</p>
          <h1>Structured review orchestration for Azure DevOps, TFS, OpenAI-compatible models, and Ollama.</h1>
        </div>
        <p className="hero-copy">
          Bootstrap review runs by pull request URL or branch comparison, stream pipeline progress over SSE, route
          stages across provider profiles, and publish summary plus inline comments back into Azure DevOps/TFS.
        </p>
      </section>

      {error ? <div className="error-banner">{error}</div> : null}

      <div className="layout-grid">
        <ReviewForm
          profiles={profiles}
          onStartPullRequestReview={handleStartPullRequestReview}
          onStartBranchReview={handleStartBranchReview}
        />
        <ProgressStream events={events} run={currentRun} />
      </div>

      <RunDetails run={currentRun} />
    </main>
  );
}
