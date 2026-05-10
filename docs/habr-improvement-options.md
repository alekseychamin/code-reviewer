# Habr Article Improvement Options

Source article: "Gefestych: our experience automating Code Review through LLM".

This project already implements several ideas from the article: direct OpenAI-compatible and Ollama providers, structured JSON outputs, markdown review artifacts, workspace tools, Roslyn graph context, and optional Qdrant retrieval. The options below are the next practical improvements.

## Implemented now

- Historical finding retrieval now applies configurable recency decay through `Qdrant:HistoricalFindingHalfLifeDays`.
- Default half-life is 45 days. Set `QDRANT_HISTORICAL_FINDING_HALF_LIFE_DAYS=0` to keep pure semantic ranking.
- Primary review prompts now explicitly separate changed diff, issue/service documentation, graph snippets, semantic context, and tool results as different evidence sources.
- Diff preprocessing now emits deterministic review hints for SQL predicate removals, unused join aliases, options-section mismatches, test/seed references, nullability contracts, and non-deterministic `GroupBy(...).First()` patterns.
- Full-context primary review now prefetches deterministic supplemental context for those hints before the model's own tool-loop, so SQL/config/test risks are checked even when the model does not request the right files itself.

## Recommended next options

1. Add issue context tools.
   Provide task text from Jira, GitLab Issues, Azure Boards, or TFS work items as a first-class context block before primary review. This should be separate from code context so the model can check business intent without treating issue prose as code evidence.

2. Add service documentation retrieval.
   Index service README, ADRs, OpenAPI specs, database docs, and runbooks into the same retrieval layer. Use this to validate API contracts, integration flows, and operational assumptions before emitting findings.

3. Promote Qdrant from follow-up retrieval to primary review context.
   For each changed file or symbol, retrieve a small set of current service docs and historical findings before the first review pass. Keep the result bounded and label it as supporting context.

4. Track retrieval freshness.
   Store source path, content hash, indexed_at, and source kind for every semantic point. Show stale or fallback retrieval in logs and in the run diagnostics so weak context does not look authoritative.

5. Add a self-hosted model profile preset.
   Add optional profiles for vLLM or llama.cpp OpenAI-compatible endpoints. Keep the same `ILlmCompletionService` contract so model upgrades do not change the review pipeline.

6. Evaluate markdown diff variants.
   The current chunks are already markdown-like. A controlled experiment can compare the current structured hunk format against fenced unified diff blocks and a single full-context markdown message for large-context models.

7. Add review quality telemetry.
   Store accepted, rejected, edited, and published findings. Use that signal to tune prompt rules, retrieval half-life, top-k values, and model routing.
