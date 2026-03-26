import type {
  BranchReviewPayload,
  ProviderProfile,
  PullRequestReviewPayload,
  ReviewRun
} from './types';

const apiBaseUrl = normalizeBaseUrl(import.meta.env.VITE_API_BASE_URL);

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;

  try {
    response = await fetch(buildUrl(path), {
      ...init,
      headers: {
        'Content-Type': 'application/json',
        ...(init?.headers ?? {})
      }
    });
  } catch (error) {
    const location = apiBaseUrl ? ` ${apiBaseUrl}` : '';
    throw new Error(
      `Не удалось обратиться к API${location}. Проверь, что backend запущен и VITE_API_BASE_URL настроен корректно.`,
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

function normalizeBaseUrl(raw: string | undefined): string {
  if (!raw || !raw.trim()) {
    return '';
  }

  return raw.trim().replace(/\/+$/, '');
}

function buildUrl(path: string): string {
  return apiBaseUrl ? `${apiBaseUrl}${path}` : path;
}

function extractApiErrorMessage(raw: string, status: number): string {
  if (!raw.trim()) {
    return `Запрос завершился с кодом ${status}.`;
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
  return buildUrl(`/api/reviews/${runId}/events`);
}

export async function publishInlineComment(runId: string, commentId: string): Promise<ReviewRun> {
  return request<ReviewRun>(`/api/reviews/${runId}/inline-comments/${commentId}/publish`, {
    method: 'POST'
  });
}

export async function publishReport(runId: string): Promise<ReviewRun> {
  return request<ReviewRun>(`/api/reviews/${runId}/report/publish`, {
    method: 'POST'
  });
}

export async function continueInlineDiscussion(runId: string, commentId: string, message: string): Promise<ReviewRun> {
  return request<ReviewRun>(`/api/reviews/${runId}/inline-comments/${commentId}/discussion`, {
    method: 'POST',
    body: JSON.stringify({ message })
  });
}

export function buildDiffDownloadUrl(runId: string): string {
  return buildUrl(`/api/reviews/${runId}/artifacts/diff`);
}

export function buildReportDownloadUrl(runId: string): string {
  return buildUrl(`/api/reviews/${runId}/artifacts/report`);
}
