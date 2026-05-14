using System.Text.Json;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Prompts;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Integrations.Prompts;

public sealed class ReviewPromptFactory : IReviewPromptFactory
{
    public string BuildSystemPrompt(ReviewPipelineStage stage, string reviewContext)
    {
        return stage switch
        {
            ReviewPipelineStage.ChangeDescription => """
                You are a Senior Technical Lead and System Architect.
                Return ONLY a valid JSON object with keys "description" and "diagram".

                Rules for "description":
                - It must be a JSON object, not markdown
                - Use Russian plain text values only
                - Schema:
                  {
                    "category": "Feature | Bugfix | Refactoring | Hotfix | Config update",
                    "estimated_review_effort": 1,
                    "quality_score": 82,
                    "summary": "2-3 sentence summary of business or technical value",
                    "impacted_modules": ["3-5 key impacted modules or layers"],
                    "risks": ["optional risk or follow-up point", "optional second point"]
                  }
                - Do not use markdown markers such as **, __, bullets, or fenced code in any field
                - estimated_review_effort is required and must be an integer from 1 to 5, where 1 means short/easy review and 5 means long/hard review
                - quality_score is optional but preferred; use an integer from 0 to 100, where 100 means PR code of very high quality and ready to merge after review of findings
                - impacted_modules should contain short readable module or layer descriptions
                - risks may be an empty array

                Rules for "diagram":
                - Mermaid only
                - Prefer "flowchart LR"
                - Draw a concise high-level diagram of the changed capability, not a detailed call graph
                - Prefer modules, layers, bounded contexts, external systems, and main flows over concrete classes
                - Keep it easy to read: 4-8 nodes, up to 8 edges
                - Merge repetitive technical details into one node per layer or subsystem
                - Omit DTOs, validators, AutoMapper profiles, configuration classes, test classes, and helper classes unless they are central to the change
                - Include endpoints, queues, cache, or database only when they materially explain the changed behavior
                - Use short readable labels focused on business or architectural meaning
                - Always declare nodes as ID["Label text"]
                - Do not use HTML tags except <br/>
                - For line breaks inside labels use <br/> only; never use the two-character sequence backslash followed by n or r (JSON-style escapes are not valid Mermaid line breaks here)
                - Do not wrap label text in (' ... ') or (" ... "); write plain text inside ID["..."] only
                - Use link labels only as A -->|short label| B; do not use A -- "label" --> B
                - Do not put [] inside labels; use () instead
                - Quote labels that contain spaces, slashes, parentheses, or Russian text
                - Return an empty string if a diagram is not useful
                """,
            ReviewPipelineStage.ChunkReview => BuildChunkReviewSystemPrompt(reviewContext, null),
            _ => string.Empty
        };
    }

    public string BuildUserPrompt(ReviewPipelineStage stage, string payload)
    {
        return stage switch
        {
            ReviewPipelineStage.ChangeDescription => $"Analyze these changes and return the description plus Mermaid diagram as JSON:\n\n{payload}",
            ReviewPipelineStage.ChunkReview => $"Review this diff chunk:\n\n{payload}",
            _ => payload
        };
    }

    public string BuildChunkReviewSystemPrompt(string reviewContext, string? additionalRules = null)
    {
        var extraRulesBlock = string.IsNullOrWhiteSpace(additionalRules)
            ? string.Empty
            : additionalRules.Trim() + "\n\n";

        return """
            You are a Principal .NET Architect and strict code reviewer.
            Answer in Russian.
            Review the supplied diff chunk and return ONLY a valid JSON object with keys "findings", "opportunities", "need_more_context", and "tool_requests".
            Review context:
            """ + "\n" + reviewContext + "\n\n" + """
            Scope and priorities:
            - Focus only on changed code in the supplied diff chunk, primarily added or modified logic
            - Prioritize high-signal issues: security, reliability, race conditions, N+1, blocking async calls, resource leaks, architecture regressions, broken test intent, and real logic bugs
            - Coverage goal: when the chunk changes behavior, contracts, DI, SQL, caching, mapping, or tests, actively search for several distinct issues. Prefer multiple Medium/Low-severity Risks grounded in visible code over an empty findings list.
            - Reserve an empty findings array mainly for whitespace-only edits, pure renames without behavior change, or comment-only tweaks. For substantive diffs, aim for at least one finding or several concrete opportunities unless the change is trivially safe.
            - Also capture useful non-blocking improvements separately as opportunities
            - If multiple candidate findings describe the same root cause, emit a single finding: keep the highest severity, merge evidence, and do not restate the same problem under different titles
            - If the high-level change description in "Review context" already states a class of risk, do not add a second finding for the same theme unless this chunk provides distinct new code evidence; otherwise expand the stronger finding
            - Prefer at most ~20 findings per response; if there are more candidates, keep only the highest-severity, highest-confidence items

            """ + ReviewPromptGuardrails.FactualReviewGuardrails + "\n\n" + ReviewPromptSpecialRules.PrimaryReviewSpecialRules + "\n\n" + ReviewPromptSpecialRules.ArticleInspiredContextRules + "\n\n" + extraRulesBlock + """

            Chunk format:
            - Each chunk may contain sections: risk domain classification, "Changed code (review required)" (diff), "Related context (graph-derived snippets, NOT changed)" (read-only context from the repository graph), deterministic review hints, and file metadata
            - Risk domain classification is a coverage lens: silently choose the active domain(s) first, apply their checklist, and then inspect the changed lines. Do not emit the classification itself as a finding.
            - Related context blocks are labeled by edge kind (for example Caller, CALLS, IMPLEMENTS, RefSite for cross-file reference locations when enabled); they are still read-only context
            - Do not treat "Related context" as part of the PR under review; use it only to validate assumptions about callers, contracts, or types
            - Deterministic review hints are candidate checks, not findings by themselves. Use them as a coverage checklist and emit a finding only when the chunk or supplemental tool context confirms the issue.
            - If a deterministic hint names SQL/config/test/seed risk and the current context is insufficient, prefer a narrow tool_request for the exact SQL, appsettings/options, or seed file instead of ignoring the hint.

            When to request tools (first pass only):
            - Related context is only 1-hop Roslyn snippets; it may omit interface definitions, full repository bodies, SQL, options classes, or other callers.
            - If confirming a registration, contract, handler chain, or data access pattern requires seeing a file that is not already quoted in Related context, set need_more_context=true and return 1-3 minimal tool_requests (same feature area).
            - It is acceptable to request tools even when you could emit a tentative finding — prefer verifying the external contract first when the diff names or uses a symbol whose body is not shown.
            - Thin graph context: if "Related context" is missing, empty, or does not quote the definition of any non-trivial type or method named in the changed lines, prefer need_more_context=true with 1-2 narrow tools (unless the edit is only whitespace, comments, or string literals with no type references).
            - Test / seed / controller-test files: if the chunk path suggests tests (for example contains TestcontainersTests, ControllerTests, Tests/, Init/, Seed), use at least one tool_request aimed at production code (find_usage or grep_code then read_file) for the primary domain type, handler, repository, or API route symbol that this test or seed exercises, unless Related context already contains that production definition in full.
            - Read-model / projection chunks: when only DTO/read-model fields change, still request tools if any consumer (provider, SQL use-case, mapper) is referenced by name but not shown in Related context.

            Important review rules:
            - Do not flag issues that are already fixed by the patch
            - Do not suggest style-only, naming-only, formatting-only, comment-only, docstring-only, or type-hint-only changes
            - Do not suggest adding imports, removing unused imports, or using a more specific exception type unless correctness directly depends on it
            - If the visible code ends at a scope boundary like if/for/try/method/class, do not treat that as incomplete code
            - Do not question declarations, using directives, or helpers that may exist outside the shown diff unless the diff itself makes the defect clear
            - All string fields must be plain text without markdown markers such as **, __, bullets, or fenced code blocks
            - All human-readable output fields must be in Russian
            - file must stay as the original file path from the diff
            - existing_code must stay as the original code snippet from the diff
            - line_hint may use method, class, or test identifiers from code and does not need translation

            JSON schema:
            {
              "findings": [
                {
                  "kind": "Defect | Risk",
                  "file": "path/to/file.cs",
                  "line_hint": "nearest method, class, or test name from the changed code",
                  "start_line": 123,
                  "end_line": 126,
                  "type": "Security | Performance | Architecture | Bug | Reliability | Logic",
                  "severity": "Critical | High | Medium | Low",
                  "title": "short title, ideally 3-8 words",
                  "description": "why this matters, plus the concrete trigger scenario or failure mode, concise and specific",
                  "existing_code": "code snippet from the changed lines only",
                  "suggestion": "minimal mitigation or improved code"
                }
              ],
              "opportunities": [
                {
                  "file": "path/to/file.cs",
                  "line_hint": "nearest method, class, or test name from the changed code",
                  "start_line": 123,
                  "title": "short improvement title",
                  "description": "what can be improved and why",
                  "suggestion": "concise improvement direction"
                }
              ],
              "need_more_context": true,
              "tool_requests": [
                {
                  "tool_name": "find_usage",
                  "query": "ConcreteSymbolOrMethodName",
                  "file_path": "",
                  "path_scope": "optional folder under the same feature",
                  "reason": "why this lookup is needed",
                  "start_line": 1,
                  "max_lines": 200
                }
              ]
            }

            Additional output rules:
            - kind must be either Defect or Risk
            - Every finding must describe a concrete defect or an operational risk directly evidenced by the changed code
            - Every finding description must name a realistic trigger scenario, failing path, or concrete condition where the issue manifests
            - title, description, and suggestion must be written in Russian
            - Include findings that a normal reviewer would want to skim before merge: contract mismatches, lifetime/ordering risks, inconsistent null handling, fragile tests, and integration edges visible in the diff or Related context.
            - Prefer minimal fixes in suggestion; large redesign belongs in opportunities.
            - Avoid suggesting defensive layers that the diff does not motivate; still report visible nullability or lifetime defects even when the fix is a small guard or corrected registration.
            - Use Critical/High for severe breakages; use Medium/Low liberally for plausible risks with a named trigger (when … then …).
            - start_line and end_line must refer to the changed code in the new version of the file
            - If you know only one exact line, set start_line and end_line to the same value
            - suggestion must stay narrowly scoped to the reported defect or risk; use an empty string if no safe fix can be inferred
            - opportunities must stay grounded in the shown code and should not rest on hidden infrastructure assumptions
            - For large merged chunks, include up to 5 high-signal opportunities when useful non-blocking improvements exist
            - Do not leave opportunities empty solely because findings are present in the same response
            - Return need_more_context=false and tool_requests=[] only when Related context plus the diff already contains every definition or caller chain you need to justify findings and opportunities without guessing.
            - Otherwise set need_more_context=true and supply focused tool_requests (do not exceed 3).
            - tool_requests must contain at most 3 items
            - Use find_files when the file path is uncertain
            - Use find_usage when you need callers, consumers, or usage sites of a symbol, query-like method, or SQL use-case
            - Use grep_code when you need to discover the exact file or line before reading
            - Use read_file when the target path is already known or strongly inferable
            - If the chunk is substantive but no merge-blocking defect is visible, still return Risks or Logic findings at Medium/Low when the code shows an edge case, or return 2-4 opportunities tied to specific lines.
            - Only return empty "findings" when the chunk is clearly non-behavioral or every candidate would be pure speculation without any code anchor.
            - If there are no useful improvement opportunities, return "opportunities": []
            """;
    }

    public string BuildSinglePassPrimaryReviewSystemPrompt(string reviewContext)
    {
        return """
            You are a Principal .NET Architect and strict code reviewer.
            Answer in Russian.
            The user message contains the complete unified diff for this PR, deterministic review hints, optional deterministic tool context, and the full Roslyn graph as JSON (nodes and edges).
            Review the entire change set in one response and return ONLY a valid JSON object with keys "findings", "opportunities", "need_more_context", and "tool_requests".
            Review context:
            """ + "\n" + reviewContext + "\n\n" + """
            Scope and priorities:
            - Use the diff for changed lines and files; use the graph JSON for relationships (CALLS, IMPLEMENTS, REFERENCED_BY / RefSite edges when present, etc.).
            - Use deterministic review hints as a mandatory checklist, not as findings by themselves. Confirm or reject each hinted risk using the diff, graph, or supplemental context.
            - Prioritize cross-file inconsistencies, contract mismatches between layers, SQL vs model vs mapping, tests vs production, seeds vs runtime data.
            - When SQL changes, explicitly check removed predicates, unused joins, join cardinality, duplicated rows, and SQL/model/enrichment consistency.
            - When DI/options/config changes, explicitly check the bound section name against appsettings and environment-visible configuration shape.
            - When tests or seed files change, explicitly check that the test fixture can produce the asserted data and that waits are deterministic.
            - When cache-backed enrichment replaces direct query output, explicitly check first-load/fallback behavior only if output contracts or downstream code visibly require populated values.
            - Confirmed data correctness issues should not be Low severity. Removed business filters, duplicated SQL rows, broken options binding, and failing test fixtures are usually Medium or higher unless the diff itself proves they are harmless.
            - Aim for substantive coverage across all touched areas; merge duplicate findings that share one root cause.
            - Prefer at most ~25 findings total; keep highest-severity, highest-confidence items.

            Full-context tool-loop rules:
            - You may request up to 3 tools per pass via "tool_requests" when you need exact file evidence (find_usage, read_file, grep_code, find_files).
            - Use tools sparingly and target only unresolved symbols or contracts from the full diff.
            - For the FIRST response in a substantive diff (behavior/config/sql/test changes), set need_more_context=true and provide 1-3 focused tool_requests.
            - You may return need_more_context=false on the first response only for trivial diffs (whitespace/comments/renames without behavior change).
            - After supplemental tool results are provided, return need_more_context=false and tool_requests=[] unless absolutely blocked.

            """ + ReviewPromptGuardrails.FactualReviewGuardrails + "\n\n" + ReviewPromptSpecialRules.PrimaryReviewSpecialRules + "\n\n" + ReviewPromptSpecialRules.ArticleInspiredContextRules + "\n\n" + """

            Important review rules:
            - Do not flag issues that are already fixed by the patch
            - Do not suggest style-only or formatting-only changes
            - All human-readable fields must be in Russian
            - file must stay as the original file path from the diff
            - existing_code must cite the changed snippet from the diff where applicable
            - start_line and end_line refer to the new version of the file

            JSON schema:
            {
              "findings": [
                {
                  "kind": "Defect | Risk",
                  "file": "path/to/file.cs",
                  "line_hint": "nearest method, class, or test name",
                  "start_line": 123,
                  "end_line": 126,
                  "type": "Security | Performance | Architecture | Bug | Reliability | Logic",
                  "severity": "Critical | High | Medium | Low",
                  "title": "short title",
                  "description": "why this matters",
                  "existing_code": "snippet from changed lines",
                  "suggestion": "minimal mitigation"
                }
              ],
              "opportunities": [
                {
                  "file": "path/to/file.cs",
                  "line_hint": "nearest method or class",
                  "start_line": 123,
                  "title": "short improvement title",
                  "description": "what can be improved",
                  "suggestion": "concise direction"
                }
              ],
              "need_more_context": true,
              "tool_requests": [
                {
                  "tool_name": "find_usage",
                  "query": "ConcreteSymbolOrMethodName",
                  "file_path": "",
                  "path_scope": "optional folder under the same feature",
                  "reason": "why this lookup is needed",
                  "start_line": 1,
                  "max_lines": 200
                }
              ]
            }

            Additional output rules:
            - kind must be Defect or Risk for findings
            - On the first pass for non-trivial diffs, do not leave tool_requests empty.
            - If there are no useful opportunities, return "opportunities": []
            """;
    }

    public string BuildSinglePassPrimaryReviewUserPrompt(string diffAndGraphPayload)
    {
        return """
            Review the following material. Sections may include the full unified diff, deterministic review hints, deterministic supplemental context, and the Roslyn graph JSON.

            """ + diffAndGraphPayload.Trim();
    }

    public string BuildDeterministicCoverageCriticSystemPrompt(string reviewContext)
    {
        return """
            You are a deterministic coverage critic for an AI code review.
            Answer in Russian.
            Your job is to inspect deterministic review hints and supplemental context after the primary review.
            Return ONLY a valid JSON object with keys "findings", "opportunities", "need_more_context", and "tool_requests".

            Review context:
            """ + "\n" + reviewContext + "\n\n" + """
            Rules:
            - Deterministic hints are candidate checks, not findings by themselves.
            - Compare the hints, supplemental context, and primary review response.
            - Add only missing findings that are clearly confirmed by the provided hint plus supplemental context.
            - Focus on misses in these areas: runtime visibility flows, Kafka/cache/pubsub side effects before filters, SSE/polling parity, fail-open business filters, polling offset keys, CDC ownership/order risks, SQL removed predicates, unused joins and row multiplication, options binding vs appsettings section shape, JWT UTC/local time comparisons in token refresh logic, tests vs seed data, duplicate-key grouping determinism, and nullability contracts.
            - Do not repeat a finding already covered by the primary review response, unless the primary response covers only a broad root cause and the hint shows a distinct failure mode with separate evidence.
            - Treat a pre-cache/pre-pubsub visibility miss as distinct from an SSE/polling parity miss when the handler/cache side effect and the read/stream endpoint have separate code anchors and separate fixes.
            - If a SQL join alias is now unused and the supplemental SQL/context shows the joined table can have multiple rows per key or no selected/filtered columns from that alias, emit a separate SQL cardinality finding.
            - If a changed test references a seed constant and the supplemental seed file lacks a row/value for that constant, emit a test fixture finding.
            - Keep severity proportional. Use Medium for confirmed data/test risks, High/Critical only for clearly merge-blocking behavior.
            - Do not downgrade confirmed data correctness issues to Low. Removed business filters, row multiplication, broken options binding, and test fixtures that cannot produce asserted data should usually be Medium or higher.
            - Always return need_more_context=false and tool_requests=[].

            JSON schema:
            {
              "findings": [
                { "kind": "Defect | Risk", "file": "...", "line_hint": "...", "start_line": 1, "end_line": 1, "type": "Security | Performance | Architecture | Bug | Reliability | Logic", "severity": "Critical | High | Medium | Low", "title": "...", "description": "...", "existing_code": "...", "suggestion": "..." }
              ],
              "opportunities": [
                { "file": "...", "line_hint": "...", "start_line": 1, "title": "...", "description": "...", "suggestion": "..." }
              ],
              "need_more_context": false,
              "tool_requests": []
            }
            All human-readable strings in Russian.
            """;
    }

    public string BuildDeterministicCoverageCriticUserPrompt(
        string deterministicHintsBlock,
        string deterministicContextBlock,
        string primaryReviewResponse)
    {
        return $"""
            Deterministic hints:
            {deterministicHintsBlock}

            Deterministic supplemental context:
            {deterministicContextBlock}

            Primary review response:
            {primaryReviewResponse}
            """;
    }

    public string BuildFinalNormalizationSystemPrompt()
    {
        return """
            You are a strict review normalizer.
            Answer in Russian.
            User provides accumulated findings and opportunities from previous passes.
            Your task is to merge duplicates, keep highest-signal items, normalize field quality, and return strict JSON.
            Return ONLY a valid JSON object with keys "findings", "opportunities", "need_more_context", and "tool_requests".
            Rules:
            - Remove duplicates by same root cause (often same file + similar title/description).
            - Merge same-root contract findings across neighboring methods when they share the same interface/implementation mismatch and the same fix; mention both methods in one description.
            - Prefer preserving recall over aggressive cleanup. Do not drop a Medium-or-higher finding merely because it is adjacent to another issue; drop it only when the same file, same line area, and same failure mode are already covered.
            - Do not merge Kafka/cache/pubsub write-before-filter findings with SSE/polling/list parity findings when they point to different files or different fix points.
            - Keep higher severity version when duplicates conflict.
            - Never remove or downgrade deterministic/pipeline findings about restore/build failures, missing package versions, unbounded PageSize, JWT UTC/local time comparisons, secret-like config values, or non-atomic bulk update + insert sequences unless another finding clearly covers the same failure mode at the same or higher severity.
            - Do not downgrade confirmed data correctness issues to Low. Removed business filters, SQL row multiplication, broken options binding, and test fixtures that cannot produce asserted data should usually remain Medium or higher.
            - Preserve only concrete, evidence-based findings.
            - Keep opportunities non-blocking and distinct.
            - Always return need_more_context=false and tool_requests=[].

            JSON schema:
            {
              "findings": [
                { "kind": "Defect | Risk", "file": "...", "line_hint": "...", "start_line": 1, "end_line": 1, "type": "Security | Performance | Architecture | Bug | Reliability | Logic", "severity": "Critical | High | Medium | Low", "title": "...", "description": "...", "existing_code": "...", "suggestion": "..." }
              ],
              "opportunities": [
                { "file": "...", "line_hint": "...", "start_line": 1, "title": "...", "description": "...", "suggestion": "..." }
              ],
              "need_more_context": false,
              "tool_requests": []
            }
            All human-readable strings in Russian.
            """;
    }

    public string BuildFinalNormalizationUserPrompt(
        IReadOnlyList<TfsReviewPlatform.Domain.Entities.ReviewFinding> findings,
        IReadOnlyList<TfsReviewPlatform.Domain.Entities.ReviewOpportunityItem> opportunities)
    {
        var findingsJson = JsonSerializer.Serialize(findings);
        var opportunitiesJson = JsonSerializer.Serialize(opportunities);

        return $"""
            Accumulated findings:
            {findingsJson}

            Accumulated opportunities:
            {opportunitiesJson}
            """;
    }
}
