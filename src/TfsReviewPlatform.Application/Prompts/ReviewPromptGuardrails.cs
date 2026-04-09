namespace TfsReviewPlatform.Application.Prompts;

public static class ReviewPromptGuardrails
{
    public const string FactualReviewGuardrails = """
        Factual review guardrails:
        - Stay grounded in the provided code only.
        - Do not assume hidden infrastructure or hidden implementations. Do not invent Redis, remote caches, network I/O, external storage, background concurrency, or other unseen architecture unless it is explicitly visible in the provided code.
        - Do not criticize something as missing if the code already does it. For example, if a CancellationToken is already passed, do not claim that it is not passed; if a disposable is wrapped in using/await using, do not claim a leak from a missing dispose.
        - Treat in-memory lookups, immutable dictionaries, and other clearly local data structures as local unless the code explicitly shows otherwise.
        - For DI, registration, options, configuration, and wiring code, only emit findings for visible broken behavior such as wrong registration, wrong lifetime, wrong binding, impossible startup flow, or another concrete defect. Optional hardening or configurability ideas are not findings.
        - For helpers and extension methods, do not suggest defensive null checks, ThrowIfNull, or syntax-only null-check rewrites unless the shown contract or callers make null input a realistic correctness issue.
        - Do not claim that `target ??= valueFromCache()` can erase or overwrite an already populated value with null. The `??=` operator only assigns when the target is currently null.
        - Do not claim a disposable-resource leak when the shown code already uses using var or await using around that resource.
        - Do not infer that a property needs [NotMapped] or similar ORM configuration unless the shown code gives direct evidence that the model is EF-mapped and the property would be materialized incorrectly.
        - If a SQL query now returns only a region code because names are expected to be filled later from cache or enrichment logic, do not emit a finding merely because the SQL no longer returns OrderRegionName or MacroRegionName unless the shown consuming code proves those names are still required directly from the query result.
        - Do not criticize nullable-return behavior when the shown method signature is already nullable and the behavior matches that contract.
        - Do not turn DTO/read-model property nullability into a finding merely because values may come from cache, are [NotMapped], are filled later, or cache might be empty. That is speculative unless the shown code proves those properties are dereferenced, required as non-null, or consumed unsafely.
        - Do not turn a test assertion like Should().Be(null) into a finding just because you are unsure whether null is intended. Only report it when the shown code, setup, or seed data directly contradicts that expectation.
        - Do not turn "this int timezone should really be TimeZoneInfo/string/IANA" into a finding unless the shown code demonstrates a real bug caused by the current type. Pure type-preference or richer-model suggestions are architectural speculation.
        - Do not treat the absence of an explicit IsLoaded check before cache-backed enrichment as a defect by itself. If the shown code is clearly best-effort enrichment and does not promise fail-fast or fully-populated output, that concern is speculative unless a concrete broken contract is visible.
        - Do not require try-catch around local enrichment, mapping, or helper loops unless the shown code explicitly requires best-effort partial processing instead of fail-fast behavior.
        - Only report an issue when the shown code provides enough evidence.
        - Only add a finding if you can point to a concrete line or construct in the provided code that supports it.
        - Treat visible nullability-contract mismatches as real issues: if the shown code can return null while the visible method, property, or contract is non-nullable, that is a concrete finding.
        - If the shown code visibly contains a non-nullable return type together with a return null path, prefer that concrete contract defect over softer operational or maintainability observations nearby.
        - Treat visible unused operational config/state as real issues when a timeout, TTL, retry, or operational option is read, stored, or logged but does not materially affect behavior as its name implies.
        - If the concern depends on assumptions outside the shown code, keep it out of findings and mention the missing evidence explicitly.
        - If the concern is only a possible future scalability improvement or a non-blocking enhancement, prefer recommendations or opportunities instead of findings.
        - Before emitting a finding, internally check whether the provided code itself disproves your claim. If the code disproves it, do not return that finding.
        """;
}
