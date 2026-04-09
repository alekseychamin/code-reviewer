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
        - Prefer an empty findings list over speculative or low-confidence findings.
        - Return only findings that a human reviewer should realistically inspect before merge.
        - Every finding must include a concrete trigger scenario or failure mode visible from the changed code. If you cannot describe when the issue manifests, prefer omitting it or moving it to an opportunity.
        - Treat nullability contract mismatches as high-priority findings when the visible code can return null through a path while the method, property, or contract is declared non-nullable.
        - In repository getters, mappers, and simple accessors that return string or another non-nullable type, a visible return null path should almost always be emitted as a finding rather than an opportunity.
        - If a visible method signature is non-nullable and the shown code literally returns null, prioritize that concrete contract finding over softer discussion about retries, configurability, or maintainability in nearby files.
        - Treat unused operational config or state as a likely finding when a visible timeout, TTL, retry, refresh interval, or operational option is read, captured, or logged but does not materially affect behavior as its name implies.
        - Do not turn configuration hot-reload speculation into a finding. "This value is read once and would not update if config changes at runtime" is not a finding unless the shown code explicitly requires runtime config reload.
        - Prefer surfacing nullability contract mismatches and unused operational config/state over generic performance or cleanup suggestions.
        - Code duplication, helper extraction, IsNullOrWhiteSpace hardening, and readability-only improvements belong in opportunities unless the changed code shows a realistic bug or risk.
        - For DI, registration, configuration, or wiring chunks, emit findings only for visible broken behavior such as wrong registration, wrong service lifetime, wrong options binding, or impossible startup flow. "Could be configurable", "could validate options", and "could use backoff" are usually opportunities, not findings.
        - For helpers and extension methods, do not emit opportunities about switching == null to is null, adding ThrowIfNull, or adding defensive null checks on collaborators unless the code is an actual public API boundary or the shown callers make null input realistic.
        - For internal helpers, provider enrichment code, DI-wired collaborators, and extension methods used inside the service graph, do not emit findings just because a collaborator parameter is not null-checked. Missing defensive null checks on injected/internal collaborators usually belong nowhere unless the changed code shows a realistic null path.
        - Do not emit a finding that says `target ??= cacheGetter(...)` can overwrite an already populated value with null. `??=` only assigns when the target is currently null, so that specific overwrite claim is incorrect.
        - Do not emit a finding just because a provider or enrichment flow does not explicitly check `IsLoaded` before reading from a cache-backed repository. If the shown code is best-effort enrichment and does not promise fully-populated output, missing `IsLoaded` checks are speculative unless the changed code shows a concrete broken contract.
        - Do not surface "move constants to config", "make timeout configurable", or similar hardening suggestions unless the shown code already demonstrates a concrete operational mismatch or conflicting runtime requirement.
        - If a query removes a JOIN because region or lookup data is now expected to come from an in-memory cache or another enrichment step, do not emit a finding solely saying the JOIN was removed and cache might be empty. That is usually an architectural tradeoff or an opportunity unless the changed code shows a concrete broken path.
        - If SQL now returns RegionCode instead of OrderRegionName or MacroRegionName and nearby code clearly enriches names from cache afterwards, do not emit a finding just because the SQL result no longer contains the human-readable names.
        - Do not emit a finding for DTO/read-model property nullability merely because the values may come from cache, are marked [NotMapped], are filled later, or cache could be empty. That is speculative unless the shown code demonstrates an actual dereference, invalid assignment, or a non-null requirement exercised in the changed code.
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
        - Do not elevate low-signal cleanup to findings: splitting a test, extracting constants, introducing Null Object, creating helper methods, or removing small local duplication should stay in opportunities unless they directly fix a correctness, reliability, or contract issue.
        - Prefer at most a few high-signal opportunities; omit nitpicks such as repeated literals, cosmetic helper extraction, or minor readability cleanups when stronger findings already exist.
        """;

    public const string PrimaryReviewToolRequestRules = """
        Tool request rules:
        - If the diff chunk is insufficient for a reliable review, you may request up to 3 workspace tools.
        - Use tool_requests only when they are necessary to avoid speculation.
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
        - If the review depends on a neighboring implementation, interface, options class, repository, SQL file, or test helper that is not shown, request tools first instead of guessing.
        - For DI, configuration, SQL, and other wiring-oriented chunks, prefer at most one narrow search path and one targeted read_file.
        - Do not request both a broad search and unrelated downstream files when one focused query is enough.
        - Prefer files from the same feature area or neighboring folder over distant files from another layer when validating a local concern.
        - Do not request interface or contract discovery only to speculate whether a few local enrichment or getter calls might secretly perform external I/O, database access, or expensive remote work.
        - If the current code already looks like in-memory enrichment, cache read access, or local mapping, hidden cost assumptions are speculation and do not justify extra tools.
        - For extension-method or enrichment chunks that only read cache-like values, map fields, or fill view models, do not request tools just to investigate hypothetical N+1, remote I/O, or expensive getter behavior unless the shown code itself contains visible async I/O, network/database calls, or another concrete signal of non-local work.
        - For test chunks, prefer the direct helper, extension, repository, query, or SQL file used by the test. Do not request a hosted service or background service unless the test directly exercises lifecycle behavior of that service.
        - For test chunks, do not guess a helper file path and request read_file unless the path is exact. If find_usage already identified a direct helper or related test file, prefer that result over speculative read_file guesses.
        - For query-like names such as GetOrderList, GetOrderListLiteV2, CreateOrder, or UpdateOrder, prefer find_usage over find_files when you need the calling provider, handler, repository, or context.
        - For SQL and other use-case chunks, if find_usage already identifies the caller chain, do not add an extra grep_code request unless it narrows the same caller chain or the same locator to a more precise line.
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
