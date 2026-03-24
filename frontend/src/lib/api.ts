import type {
  BranchReviewPayload,
  ProviderProfile,
  PullRequestReviewPayload,
  ReviewRun
} from './types';

const apiBaseUrl = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:8080';

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;

  try {
    response = await fetch(`${apiBaseUrl}${path}`, {
      ...init,
      headers: {
        'Content-Type': 'application/json',
        ...(init?.headers ?? {})
      }
    });
  } catch (error) {
    throw new Error(
      `Could not reach API at ${apiBaseUrl}. Check that the backend is running and VITE_API_BASE_URL is correct.`,
      { cause: error }
    );
  }

  if (!response.ok) {
    const raw = await response.text();
    const message = extractApiErrorMessage(raw, response.status);
    throw new Error(message);
  }

  return (await response.json()) as T;
}

function extractApiErrorMessage(raw: string, status: number): string {
  if (!raw.trim()) {
    return `Request failed with status ${status}.`;
  }

  try {
    const parsed = JSON.parse(raw) as {
      detail?: string;
      title?: string;
      errors?: Record<string, string[]>;
    };

    if (parsed.detail) {
      return parsed.detail;
    }

    if (parsed.errors) {
      const flattened = Object.entries(parsed.errors)
        .flatMap(([field, messages]) => messages.map((message) => `${field}: ${message}`))
        .join('\n');

      if (flattened) {
        return flattened;
      }
    }

    if (parsed.title) {
      return parsed.title;
    }
  } catch {
    return raw;
  }

  return raw;
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
