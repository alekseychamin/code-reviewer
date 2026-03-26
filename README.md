# TFS Review Platform

Production-oriented AI code review platform built around:

- ASP.NET Core Web API backend
- React + TypeScript frontend
- layered backend architecture: `Api`, `Application`, `Domain`, `Infrastructure`, `Integrations`
- multi-provider LLM routing for OpenAI-compatible endpoints and local Ollama
- Azure DevOps / TFS pull request integration
- review targets by PR URL and by local branch comparison
- staged review pipeline with SSE progress streaming
- markdown report generation plus summary and inline comment publishing
- optional PostgreSQL persistence for durable review history, with in-memory fallback

## Solution layout

- `src/TfsReviewPlatform.Api`: HTTP API, thin controllers, OpenAPI, SSE
- `src/TfsReviewPlatform.Application`: orchestration, DTOs, contracts, validation, report building
- `src/TfsReviewPlatform.Domain`: core entities and enums
- `src/TfsReviewPlatform.Infrastructure`: in-memory repositories and background scheduling
- `src/TfsReviewPlatform.Integrations`: LLM, Git, Azure DevOps/TFS adapters, prompt factory
- `frontend`: React + TypeScript operator console
- `tests/TfsReviewPlatform.Tests`: unit tests for preprocessing, normalization, and markdown publishing helpers

## Review pipeline

1. Diff acquisition
2. Preprocessing and chunking
3. Change description generation
4. Chunk review
5. Findings normalization
6. Final synthesis into markdown and publish drafts
7. Publish to Azure DevOps/TFS

## API surface

- `POST /api/reviews/pull-requests`
- `POST /api/reviews/branch-comparisons`
- `GET /api/reviews/{runId}`
- `GET /api/reviews/{runId}/events`
- `GET /api/provider-profiles`

## Docker Compose

1. Create a local secrets file:
   `cp .env.example .env`
2. Fill in the values you need in `.env`, for example `OPENAI_API_KEY`, `AZURE_DEVOPS_TOKEN`, and optionally `POSTGRES_CONNECTION_STRING`.
3. Start the stack:
   `docker compose up --build`

By default the API container expects local-only Ollama to be running on the host machine and reaches it through:
`LOCAL_OLLAMA_BASE_URL=http://host.docker.internal:11434`

If you want to use the `ollama` service from docker compose instead, set:
`LOCAL_OLLAMA_BASE_URL=http://ollama:11434`
and start the Ollama profile too:
`docker compose --profile local-llm up --build`

If you want persistent review history in PostgreSQL, start the database profile too:
`docker compose --profile history-db up --build`

With the default `.env.example` values, the API will then store full review history, findings, diagrams, markdown reports, and inline discussion threads in PostgreSQL through:
`POSTGRES_CONNECTION_STRING=Host=postgres;Port=5432;Database=tfs_review;Username=tfs_review;Password=tfs_review`

`docker-compose.yml` now uses `env_file: .env`, so the same local file can hold LLM API keys, Azure DevOps/TFS tokens, and frontend runtime settings for local development. In compose mode the frontend uses same-origin `/api` requests and Vite proxies them to the `api` service, which is more reliable than calling `localhost:8080` directly from the browser. The pull request flow now reads Azure DevOps/TFS PAT from `AZURE_DEVOPS_TOKEN` on the backend, so the token no longer needs to be entered in the UI.

## Notes

- `appsettings.json` seeds provider profiles and default stage routing.
- To run fully on Ollama, select the `Local Ollama` provider profile in the UI or send `providerProfileId=ollama-local` to the API.
- In docker compose, host Ollama is the default local target. The optional compose `ollama` service sits under the `local-llm` profile.
- Without `POSTGRES_CONNECTION_STRING`, the backend falls back to in-memory review storage.
- With PostgreSQL enabled, review history persists across API restarts and stores service name, author, findings, diagrams, reports, and inline discussions.
- The prompt design follows the existing Python prototype flow from `main.py` and `prompts.py`, translated into explicit pipeline stages and deterministic markdown generation.
