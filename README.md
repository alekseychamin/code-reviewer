# TFS Review Platform

Production-oriented AI code review platform built around:

- ASP.NET Core Web API backend
- React + TypeScript frontend
- layered backend architecture: `Api`, `Application`, `Domain`, `Infrastructure`, `Integrations`
- multi-provider LLM routing for OpenAI-compatible endpoints and local Ollama
- Azure DevOps / TFS, GitHub, and GitLab pull request / merge request integration
- review targets by PR URL and by local branch comparison
- staged review pipeline with SSE progress streaming
- markdown report generation plus manual report/inline comment publishing back to the PR platform
- optional PostgreSQL persistence for durable review history, with in-memory fallback

## Solution layout

- `src/TfsReviewPlatform.Api`: HTTP API, thin controllers, OpenAPI, SSE
- `src/TfsReviewPlatform.Application`: orchestration, DTOs, contracts, validation, report building
- `src/TfsReviewPlatform.Domain`: core entities and enums
- `src/TfsReviewPlatform.Infrastructure`: in-memory repositories and background scheduling
- `src/TfsReviewPlatform.Integrations`: LLM, Git, Azure DevOps/TFS, GitHub, and GitLab adapters, prompt factory
- `frontend`: React + TypeScript operator console
- `tests/TfsReviewPlatform.Tests`: unit tests for preprocessing, normalization, and markdown publishing helpers

## Review pipeline

1. Diff acquisition
2. Preprocessing and chunking
3. Change description generation
4. Chunk review
5. Findings normalization
6. Final synthesis into markdown and publish drafts
7. Optional manual publish to Azure DevOps/TFS, GitHub, or GitLab

## API surface

- `POST /api/reviews/pull-requests`
- `POST /api/reviews/branch-comparisons`
- `GET /api/reviews/{runId}`
- `GET /api/reviews/{runId}/events`
- `GET /api/provider-profiles`

## Docker Compose

1. Create a local secrets file:
   `cp .env.example .env`
2. Fill in the values you need in `.env`, for example:
   - `OPENAI_API_KEY`
   - `AZURE_DEVOPS_TOKEN` for Azure DevOps / TFS PR review and publish
   - `GITHUB_TOKEN` for GitHub PR review and publish
   - `GITLAB_TOKEN` for GitLab merge request review and publish
   - `POSTGRES_CONNECTION_STRING` for durable history
3. Start the full local stack:
   `docker compose --profile history-db up -d --build`

This is the recommended local startup command because it enables PostgreSQL-backed review history by default.

By default the API container expects local-only Ollama to be running on the host machine and reaches it through:
`LOCAL_OLLAMA_BASE_URL=http://host.docker.internal:11434`

If you want to use the `ollama` service from docker compose instead, set:
`LOCAL_OLLAMA_BASE_URL=http://ollama:11434`
and start the Ollama profile too:
`docker compose --profile history-db --profile local-llm up -d --build`

If you want persistent review history in PostgreSQL, start the database profile too:
`docker compose --profile history-db up -d --build`

With the default `.env.example` values, the API will then store full review history, findings, diagrams, markdown reports, and inline discussion threads in PostgreSQL through:
`POSTGRES_CONNECTION_STRING=Host=postgres;Port=5432;Database=tfs_review;Username=tfs_review;Password=tfs_review`

The `postgres` service is exposed only inside the docker compose network. It is reachable from the `api` container as `postgres:5432`, but it is not published on the host machine, which avoids local port conflicts with another PostgreSQL instance.

The `qdrant` service follows the same internal-only network model. It is reachable from the `api` container as `qdrant:6333`, but port `6333` is not published on the host machine.

`docker-compose.yml` uses `env_file: .env`, so the same local file can hold LLM API keys, PR platform tokens, PostgreSQL settings, and frontend runtime settings for local development. In compose mode the frontend uses same-origin `/api` requests and Vite proxies them to the `api` service, which is more reliable than calling `localhost:8080` directly from the browser. Pull request review and publishing tokens are read on the backend from environment variables, so they no longer need to be entered in the UI.

## Pull request platforms

- Azure DevOps / TFS PR URLs are supported for diff acquisition, inline publish, and report publish.
- GitHub PR URLs are supported for diff acquisition, inline publish, and report publish.
- GitLab merge request URLs are supported for diff acquisition, inline publish, and report publish.
- Review history is keyed by normalized PR URL, so the latest completed review can be reused as baseline for `Delta Since Previous Review`.
- If the diff has not changed since the previous completed review, the backend can reuse the saved run instead of executing the full LLM pipeline again.

Required backend tokens:

- `AZURE_DEVOPS_TOKEN` for Azure DevOps / TFS PR access and publishing
- `GITHUB_TOKEN` for GitHub PR access and publishing
- `GITLAB_TOKEN` for GitLab merge request access and publishing

Supported URL examples:

- Azure DevOps / TFS: `https://tfs.example.local/tfs/Main/Project/_git/Repo/pullrequest/42`
- GitHub: `https://github.com/org/repo/pull/42`
- GitLab: `https://gitlab.example.com/group/subgroup/repo/-/merge_requests/42`

## Notes

- `appsettings.json` seeds provider profiles and default stage routing.
- To run fully on Ollama, select the `Local Ollama` provider profile in the UI or send `providerProfileId=ollama-local` to the API.
- In docker compose, host Ollama is the default local target. The optional compose `ollama` service sits under the `local-llm` profile.
- Without `POSTGRES_CONNECTION_STRING`, the backend falls back to in-memory review storage.
- With PostgreSQL enabled, review history persists across API restarts and stores service name, author, findings, diagrams, reports, inline discussions, and change deltas versus previous runs.
- Historical finding retrieval can be recency-weighted with `QDRANT_HISTORICAL_FINDING_HALF_LIFE_DAYS`; the default is 45 days, and `0` disables decay.
- Habr-inspired improvement options are tracked in `docs/habr-improvement-options.md`.
- The prompt design follows the existing Python prototype flow from `main.py` and `prompts.py`, translated into explicit pipeline stages and deterministic markdown generation.
