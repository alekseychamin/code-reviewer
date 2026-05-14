namespace TfsReviewPlatform.Application.Prompts;

public static class ReviewPromptSpecialRules
{
    public const string ContractsAndDataIntegrityPassRules = """
        Focus pass: contracts and data integrity.
        - Prioritize nullability contract mismatches, public API inconsistencies, silent data loss, duplicate-key handling, grouping determinism, and misleading contract behavior.
        - Search specifically for non-nullable members that visibly return null or expose nullable behavior through a non-nullable contract.
        - Search specifically for duplicate-key or grouping logic where First(), Single(), or dictionary materialization can produce silent data loss, non-deterministic results, or hidden conflicts.
        - Deprioritize generic retry, timeout, background loop, and micro-optimization concerns in this pass unless they directly break a visible contract or corrupt data.
        - Emit opportunities only if no stronger contract or data-integrity finding is available in the same local area.
        """;

    public const string OperationalReliabilityPassRules = """
        Focus pass: operational reliability.
        - Prioritize retry logic, timeout handling, refresh intervals, TTL semantics, cancellation behavior, background service loops, and operational configuration that does not affect runtime behavior as expected.
        - Search specifically for timeouts, TTLs, refresh intervals, retry counters, and operational options that are read, captured, or logged but do not materially affect control flow or scheduling.
        - Search specifically for flaky Task.Delay-based waits in tests when they can make tests timing-dependent or unstable.
        - Deprioritize cosmetic cleanup, helper extraction, and small local duplication in this pass.
        - Emit opportunities only if no stronger operational reliability finding is available in the same local area.
        """;

    public const string PrimaryReviewSpecialRules = """
        Primary review special rules:
        - Use opportunities for non-blocking improvements such as refactoring, duplication cleanup, maintainability improvements, safer API ergonomics, or operational hardening that is helpful but not a merge-blocking defect.
        - For large merged chunks, still inspect the whole chunk for useful non-blocking improvements and return up to 5 high-signal opportunities when they are present.
        - Do not omit opportunities merely because the same response also contains findings; keep defects/risks in findings and helpful non-blocking improvements in opportunities.
        - Prefer actionable output over silence: report plausible Risks or Defects when the changed code suggests a realistic failure mode, lifetime mismatch, inconsistent assumption, or fragile test behavior. Use Medium or Low severity when the issue is worth a glance but not catastrophic.
        - Only skip findings when there is truly no visible anchor in the chunk or Related context for the claim.
        - Every finding must include a concrete trigger scenario ("when … then …"). Brief scenarios are acceptable for Medium/Low Risks when tied to visible code.
        - Start each substantive chunk by identifying the risk domain before judging individual lines: auth/token, DI/config/options, cache/refresh/TTL/time, HTTP client/integration, persistence/SQL, Kafka/stream, tests/fixtures, or public contract.
        - For auth, token, cache refresh, and identity changes, trace the flow end-to-end: source identity, fallback identity, token acquisition, token cache key, refresh threshold, time basis, cancellation/timeouts, DI registration, config validation, and tests.
        - Treat external review/PR-Agent output as descriptive context only. Re-check its claims against the changed diff and do not rely on it for finding coverage.
        - If a diff adds a library or public capability, check the consumer path: extension method, DI registration, options, client implementation, and tests must make the capability usable.
        - For token expiration, JWT `ValidTo`/`ValidFrom` and `exp` are UTC. `DateTime.Now`/`DateTimeOffset.Now` near token validity or refresh logic is a strong candidate finding unless the code explicitly converts to UTC.
        - Treat nullability contract mismatches as high-priority findings when the visible code can return null through a path while the method, property, or contract is declared non-nullable.
        - In repository getters, mappers, and simple accessors that return string or another non-nullable type, a visible return null path should almost always be emitted as a finding rather than an opportunity.
        - If a visible method signature is non-nullable and the shown code literally returns null, prioritize that concrete contract finding over softer discussion about retries, configurability, or maintainability in nearby files.
        - Do not claim code returns a cached/stale value after an exception when the shown code has try/finally but no catch around the throwing call; the exception escapes and no return statement after that call executes.
        - Do not report missing Dispose/IDisposable for SemaphoreSlim as a finding unless the diff shows AvailableWaitHandle usage or repeated creation in a loop/hot path; otherwise omit it or keep it as a low-priority opportunity only when useful.
        - Do not report Task.Factory.StartNew scheduler/UI-thread capture as a finding in backend/server library code unless a UI SynchronizationContext, WPF, WinForms, or another concrete custom scheduler is visible in the diff/context.
        - Treat Task.Factory.StartNew/Task.Run as suspicious when it wraps async I/O, token refresh, HTTP, database, Kafka, or other naturally asynchronous operations. These APIs should primarily be used for CPU-bound offload; report the concrete risk when the diff shows wasted thread-pool work, fire-and-forget lifetime, cancellation loss, unobserved exceptions, or scheduler ambiguity.
        - Do not emit Task.Factory.StartNew -> Task.Run simplification/readability advice as a finding or opportunity. Report Task.Factory.StartNew only when the diff proves a concrete runtime bug, not a stylistic modernization.
        - Do not report HttpRequestMessage.Headers.Add duplicate-header failures for custom CorrelationId-like headers unless the exact API/headers evidence proves that the added value is invalid or that the header cannot accept multiple values.
        - Skip style/documentation-only nits such as XML <returns>, unused using, comment typos, or parameter naming unless they hide a real contract bug.
        - Treat unused operational config or state as a likely finding when a visible timeout, TTL, retry, refresh interval, or operational option is read, captured, or logged but does not materially affect behavior as its name implies.
        - Do not turn configuration hot-reload speculation into a finding. "This value is read once and would not update if config changes at runtime" is not a finding unless the shown code explicitly requires runtime config reload.
        - Prefer surfacing nullability contract mismatches and unused operational config/state over generic performance or cleanup suggestions.
        - Code duplication, helper extraction, IsNullOrWhiteSpace hardening, and readability-only improvements belong in opportunities unless the changed code shows a realistic bug or risk.
        - For DI, registration, configuration, or wiring chunks, emit findings only for visible broken behavior such as wrong registration, wrong service lifetime, wrong options binding, or impossible startup flow. "Could be configurable", "could validate options", and "could use backoff" are usually opportunities, not findings.
        - When CDC consumers are added for EF entities, verify source-of-truth ownership. Local admin/API writes to the same entity/table are a finding when the diff does not show an explicit coexistence strategy, because CDC replay can overwrite or conflict with local state.
        - When two entities are consumed from independent CDC topics and the changed model adds a strict EF foreign key between them, treat out-of-order child-before-parent delivery as a data-integrity risk unless tool context proves retry, DLQ, bootstrap ordering, or another guard.
        - For Kafka/consumer flows that write to Redis/cache/pubsub/SSE, trace visibility filters before the write or publish side effect. If system, client category, authorization zone, task flag, tenant, or similar business visibility checks are applied only in a later read endpoint, report the earlier cache/stream path as a finding when users can observe the unfiltered data.
        - Do not collapse a pre-cache/pre-pubsub visibility issue into a downstream SSE/polling parity issue when they are confirmed in different code locations. The handler that saves/publishes unfiltered data and the endpoint that reads it without filtering have separate fix points and can both be findings.
        - Treat context-dependent business filters as fail-closed unless the requirement explicitly allows fail-open. If client/handling/user context cannot be resolved and the code skips a restrictive category or visibility check, report the scenario where a restricted record can be shown.
        - For polling offsets, compare the cache key with every dimension that changes the filtered result set. If offset is stored only by session/handling while results depend on system, category, zone, task flag, or user context, report that one context can advance another context's offset and hide or leak records.
        - When a service exposes both stream/SSE/pubsub and polling/list endpoints for the same business objects, compare their filtering semantics. A finding is warranted when one output path applies visibility rules and another reads the same cache or event stream without them.
        - For helpers and extension methods, do not emit opportunities about switching == null to is null, adding ThrowIfNull, or adding defensive null checks on collaborators unless the code is an actual public API boundary or the shown callers make null input realistic.
        - For internal helpers, provider enrichment code, DI-wired collaborators, and extension methods used inside the service graph, do not emit findings just because a collaborator parameter is not null-checked. Missing defensive null checks on injected/internal collaborators usually belong nowhere unless the changed code shows a realistic null path.
        - Do not emit a finding that says `target ??= cacheGetter(...)` can overwrite an already populated value with null. `??=` only assigns when the target is currently null, so that specific overwrite claim is incorrect.
        - Do not emit a finding just because a provider or enrichment flow does not explicitly check `IsLoaded` before reading from a cache-backed repository. If the shown code is best-effort enrichment and does not promise fully-populated output, missing `IsLoaded` checks are speculative unless the changed code shows a concrete broken contract.
        - Do not surface "move constants to config", "make timeout configurable", or similar hardening suggestions unless the shown code already demonstrates a concrete operational mismatch or conflicting runtime requirement.
        - If a query removes a JOIN because region or lookup data is now expected to come from an in-memory cache or another enrichment step, do not emit a finding solely saying the JOIN was removed and cache might be empty. That is usually an architectural tradeoff or an opportunity unless the changed code shows a concrete broken path.
        - If SQL now returns RegionCode instead of OrderRegionName or MacroRegionName and nearby code clearly enriches names from cache afterwards, do not emit a finding just because the SQL result no longer contains the human-readable names.
        - For DTO/read-model properties marked [NotMapped] or fed from cache, emit a Risk finding when the changed code introduces or relies on non-nullable typing or downstream use (export, Excel, serialization) without a visible guarantee that values are populated before use; omit only when Related context shows safe consumption.
        - Do not emit a finding that an int/int? timezone contract should be string, IANA id, or TimeZoneInfo unless the shown code demonstrates a concrete bug from the current representation. That is usually a modeling preference, not a defect.
        - Dictionary/grouping determinism problems are findings when duplicate keys or grouped records can lead to silent data loss or non-deterministic selection.
        - Public API and contract inconsistencies are findings when the visible code contradicts its own interface, nullable annotations, or declared behavior.
        - Test-only fragility such as Task.Delay polling, brittle timing, or non-idempotent seed setup should usually be findings only when the visible tests can become flaky or misleading, otherwise keep them as opportunities.
        - Treat Task.Delay-based waiting in tests as a finding when it can realistically make the test flaky or timing-dependent.
        - Do not emit a finding that admits it may be correct, says it is "not an error", says "check the contract", or says "leave as is if intended". Self-contradictory or hedge-heavy observations belong in opportunities or should be omitted.
        - Do not report StopAsync/CancelAsync ordering in tests as a finding unless the shown test contains explicit evidence of hangs, leaked background work, swallowed exceptions, or flaky assertions caused by that ordering.
        - Do not report leaks or missing disposal for CancellationTokenSource, stream, or disposable helpers when the shown code already wraps them in using var or await using.
        - Do not report [NotMapped] or ORM mapping problems unless the shown code directly demonstrates that the type is EF-mapped and that the added property would be mapped incorrectly.
        - If the shown method signature is already nullable and returns null accordingly, that is not a nullability-contract finding.
        - Do not turn "add try-catch so one item does not break the whole loop" into a finding for local enrichment or mapping helpers unless the shown business logic explicitly requires partial-success behavior.
        - Do not surface in-memory micro-optimizations as top opportunities. Replacing a few FrozenDictionary or dictionary lookups with a one-pass helper, batch lookup, or TryGetValue consolidation is usually too small unless the changed code shows a real hot-path problem.
        - Low-signal cleanup stays in opportunities unless it fixes a correctness issue.
        - When the chunk spans multiple concerns, multiple opportunities are welcome; omit only duplicate nitpicks that repeat the same finding.
        """;

    public const string ArticleInspiredContextRules = """
        Article-inspired review context rules:
        - Treat changed diff, issue or task context, service documentation, graph snippets, semantic chunks, and tool results as separate evidence sources when they are available.
        - The changed diff is the only code under review; service docs, issue text, graph snippets, semantic chunks, and tool results are supporting context, not modified code.
        - When task or service documentation is present, verify whether the changed code contradicts the stated business flow, operational expectation, or integration contract.
        - When semantic or historical context is stale, thin, or missing, do not invent facts; request tools when exact repository evidence is needed, or keep the idea as a non-blocking opportunity.
        - Prefer markdown section boundaries in the payload as the source of truth: "Changed code" requires review, while "Related context" only increases confidence.
        - Model-specific quirks should not leak into findings. Return the same strict JSON schema regardless of whether the selected provider is OpenAI-compatible or Ollama.
        """;

    public const string PrimaryReviewToolRequestRules = """
        Tool request rules (prefer narrow tools over guessing):
        - You may request workspace tools up to the run-specific budget when cross-file context would materially improve confidence.
        - Prefer tool_requests when "Related context" does not already show the defining implementation, interface, options binding, repository method, SQL/use-case body, or critical caller that the changed code relies on.
        - For DI registration, new interface usage, provider/handler wiring, SQL or MediatR-style entry points: if the chunk references a symbol whose behavior is not visible in the diff or Related context, use at least one focused tool (typically find_usage or read_file) unless the graph snippets already contain that definition.
        - Supported tools are find_files, find_usage, grep_code, and read_file.
        - Use find_files when you know only part of a file name, feature name, or nearby module path.
        - Use find_usage when you need callers, consumers, or usage sites of a symbol, query-like method, handler, provider entry point, or SQL use-case.
        - Use grep_code when you need to find a symbol, SQL fragment, method call, or config key before reading a file.
        - Use read_file when you already know the file path or can strongly infer it.
        - Prefer one narrow search plus one read_file over broad repository exploration.
        - query should contain the shortest concrete locator that will work: a file name fragment, class name, method name, property name, SQL fragment, or config key.
        - file_path should be repository-relative when known.
        - path_scope should narrow the search to a relevant folder or feature area when possible.
        - start_line and max_lines are mainly for read_file.
        - If the review depends on a neighboring implementation, interface, options class, repository, SQL file, or test helper that is not shown, request tools instead of guessing.
        - For DI, configuration, SQL, and other wiring-oriented chunks, use at most one narrow search path plus one targeted read_file when both are needed.
        - Do not request both a broad search and unrelated downstream files when one focused query is enough.
        - Prefer files from the same feature area or neighboring folder over distant files from another layer when validating a local concern.
        - Avoid tools only for pure fantasy scenarios (e.g. "maybe the getter does remote I/O") when the visible code has no async/database/network signal; still use tools when you need to confirm a real registration, interface contract, or handler chain that the diff references by name but does not show.
        - For extension-method or enrichment chunks: if Related context already shows the callee implementation snippet, skip extra tools; otherwise prefer find_usage/read_file for the unresolved symbol instead of assuming behavior.
        - For test chunks, prefer the direct helper, extension, repository, query, or SQL file used by the test. Do not request a hosted service or background service unless the test directly exercises lifecycle behavior of that service.
        - For test chunks, do not guess a helper file path for read_file unless the path is exact or find_usage/grep_code narrowed it; use find_usage first when the helper name is known from the test.
        - For query-like names such as GetOrderList, GetOrderListLiteV2, CreateOrder, or UpdateOrder, prefer find_usage over find_files when you need the calling provider, handler, repository, or context.
        - For SQL and other use-case chunks, avoid redundant grep_code when find_usage already pinpoints the same caller chain unless grep_code narrows to a precise line or file.
        - If Related context is empty or clearly thinner than the diff (no neighbor snippets, or snippets do not cover symbols that the diff names), bias toward need_more_context=true with 1-2 tools whenever the changed code references a type or member defined outside the chunk.
        - For file paths under test, integration, seed, or Init folders: when the diff is not purely cosmetic, include at least one narrow tool_request unless Related context already shows the full production counterpart you need.
        """;

    public const string PrimaryReviewFinalizationRules = """
        Finalization rules:
        - Supplemental tool results are already provided below when available.
        - Do not request any more tools in this pass.
        - Always return need_more_context=false and tool_requests=[] in this pass.
        - Use the supplemental tool results only to validate or refine findings grounded in the original diff chunk.
        """;

    public const string FollowUpReviewSpecialRules = """
        Follow-up review special rules:
        - If the user explicitly asks to search for additional defects, you may return new_findings, but only for concrete review-worthy issues visible in this chunk.
        - If you find useful non-blocking improvements that are worth surfacing but are not defects or risks, return them in new_opportunities instead of new_findings.
        - Do not return style-only, cleanup-only, comment-only, or speculative findings.
        - If there are no new review-worthy issues in this chunk, return an empty new_findings array.
        - If there are no noteworthy non-defect improvements in this chunk, return an empty new_opportunities array.
        - If the diff chunk is not enough but supplemental file context contains the target code, use the supplemental file context to answer.
        - If neither the diff chunk nor supplemental file context is enough to answer reliably, say exactly what is missing.
        - Treat nullability contract mismatches as priority findings when the visible code proves a non-nullable contract can return null.
        - In repository getters, mappers, and simple accessors that return string or another non-nullable type, a visible return null path should almost always be emitted as a finding rather than an opportunity.
        - If a visible method signature is non-nullable and the shown code literally returns null, prefer that concrete contract finding over softer hardening ideas nearby.
        - Treat unused operational config or state as a likely finding only when the shown code proves the option is read, stored, or logged without materially affecting behavior.
        - Do not turn configuration hot-reload speculation into a finding. If your concern is only that a value is captured once and would not react to runtime config changes, keep it out unless runtime reload is explicitly required by the shown code.
        - Prefer these contract and operational findings over generic cleanup or micro-optimization suggestions.
        - Code duplication, helper extraction, and readability-only improvements belong in new_opportunities unless the shown code demonstrates a concrete bug or risk.
        - For DI, registration, configuration, or wiring chunks, keep optional configurability and hardening ideas out of findings unless the shown code already demonstrates broken behavior or a concrete reliability defect.
        - For helpers and extension methods, do not surface == null versus is null rewrites, ThrowIfNull additions, or defensive null checks on collaborators unless the shown contract makes them materially relevant.
        - For internal helpers, provider enrichment code, DI-wired collaborators, and extension methods used inside the service graph, do not emit findings just because a collaborator parameter is not null-checked unless the shown code provides a realistic null path.
        - Do not claim code returns a cached/stale value after an exception when the shown code has try/finally but no catch around the throwing call; the exception escapes.
        - Do not report missing Dispose/IDisposable for SemaphoreSlim or Task.Factory.StartNew UI-thread capture as findings unless the concrete triggering runtime context is visible.
        - Treat Task.Factory.StartNew/Task.Run around async I/O/token refresh/HTTP/database/Kafka as a concurrency signal; report only a concrete risk such as fire-and-forget lifetime, cancellation loss, unobserved exception, wasted thread-pool scheduling, or scheduler ambiguity.
        - Do not emit Task.Factory.StartNew -> Task.Run simplification/readability advice as a finding or opportunity.
        - Skip XML docs, unused using, comment typos, and parameter naming nits unless they hide a real contract bug.
        - If a JOIN or direct lookup disappears because enrichment moved to cache-backed or follow-up logic, do not emit a finding that merely says "cache might be empty" without a concrete broken execution path in the shown code.
        - Do not claim that `??=` with a nullable cache getter can erase already populated values. That specific overwrite scenario is incorrect unless the target is explicitly reset elsewhere in the shown code.
        - If a SQL result now carries only region codes because names are enriched later from cache or follow-up logic, do not treat the missing name columns as a finding unless the shown consumer still expects the names directly from SQL.
        - Do not treat a test assertion like Should().Be(null) as a finding just because it looks suspicious. Only emit a finding when the shown test setup, seed data, or production contract directly contradicts that asserted null.
        - Do not treat missing `IsLoaded` checks before cache-backed enrichment as findings unless the shown code explicitly promises fully-populated data or otherwise demonstrates a concrete broken contract.
        - Do not emit a finding that says the code may already be correct, says it is not actually an error, or relies on "check the contract" style uncertainty.
        - Do not report StopAsync/CancelAsync ordering in tests as a finding unless the shown test code contains visible hang, leak, or flaky-test evidence.
        - Do not report leaks or missing disposal for CancellationTokenSource or other disposables when the shown code already uses using var or await using.
        - Do not report [NotMapped] or ORM mapping issues without direct evidence that the shown type is EF-mapped and that the property would be mapped incorrectly.
        - If the shown method signature is already nullable and returns null accordingly, do not treat that as a contract defect.
        - Do not require item-level try-catch in local enrichment or mapping loops unless the shown code clearly requires best-effort partial processing.
        - Do not surface in-memory micro-optimizations such as consolidating a few dictionary lookups into one pass unless the shown code demonstrates a meaningful hot-path cost.
        - Do not elevate low-signal cleanup to findings: splitting tests, extracting constants, introducing Null Object, or removing tiny local duplication should remain in new_opportunities unless they directly prevent a visible defect.
        """;
}
