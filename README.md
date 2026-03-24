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
- in-memory persistence with clean repository interfaces for later database migration

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

## Notes

- `appsettings.json` seeds provider profiles and default stage routing.
- `LocalOnlyMode=true` routes stages to a local Ollama profile and disables PR publishing.
- The backend stores review runs in memory today, but all persistence and external dependencies already sit behind interfaces.
- The prompt design follows the existing Python prototype flow from `main.py` and `prompts.py`, translated into explicit pipeline stages and deterministic markdown generation.
