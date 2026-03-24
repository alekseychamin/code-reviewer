import type {
  BranchReviewPayload,
  ProviderProfile,
  PullRequestReviewPayload,
  ReviewRun
} from './types';

const apiBaseUrl = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:8080';

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${apiBaseUrl}${path}`, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...(init?.headers ?? {})
    }
  });

  if (!response.ok) {
    const message = await response.text();
    throw new Error(message || `Request failed: ${response.status}`);
  }

  return (await response.json()) as T;
}

export async function fetchProviderProfiles(): Promise<ProviderProfile[]> {
  return request<ProviderProfile[]>('/api/provider-profiles');
}

export async function startPullRequestReview(payload: PullRequestReviewPayload): Promise<ReviewRun> {
  return request<ReviewRun>('/api/reviews/pull-requests', {
    method: 'POST',
    body: JSON.stringify(payload)
  });
}

export async function startBranchReview(payload: BranchReviewPayload): Promise<ReviewRun> {
  return request<ReviewRun>('/api/reviews/branch-comparisons', {
    method: 'POST',
    body: JSON.stringify(payload)
  });
}

export async function getReviewRun(runId: string): Promise<ReviewRun> {
  return request<ReviewRun>(`/api/reviews/${runId}`);
}

export function buildEventsUrl(runId: string): string {
  return `${apiBaseUrl}/api/reviews/${runId}/events`;
}
