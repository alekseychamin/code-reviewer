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
                - Do not put [] inside labels; use () instead
                - Quote labels that contain spaces, slashes, parentheses, or Russian text
                - Return an empty string if a diagram is not useful
                """,
            ReviewPipelineStage.ChunkReview => BuildChunkReviewSystemPrompt(reviewContext),
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
            - Prefer returning fewer findings over noisy or speculative findings
            - Prefer an empty findings list over uncertain or low-confidence findings
            - Also capture useful non-blocking improvements separately as opportunities

            """ + ReviewPromptGuardrails.FactualReviewGuardrails + "\n\n" + ReviewPromptSpecialRules.PrimaryReviewSpecialRules + "\n\n" + extraRulesBlock + """

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
              "need_more_context": false,
              "tool_requests": [
                {
                  "tool_name": "find_files | find_usage | grep_code | read_file",
                  "query": "optional search query, symbol name, file name fragment, or SQL fragment",
                  "file_path": "repository-relative file path when known",
                  "path_scope": "optional folder or feature scope",
                  "reason": "why this file is needed",
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
            - Only include findings that a human reviewer should realistically inspect before merge
            - Do not propose alternative designs unless the current changed code is likely wrong, unsafe, or materially inefficient
            - Do not suggest extra validation, null checks, logging, retries, caching, or abstractions unless the diff shows a realistic failing path
            - Use Low severity sparingly; if the issue would not change a reviewer decision, omit it
            - start_line and end_line must refer to the changed code in the new version of the file
            - If you know only one exact line, set start_line and end_line to the same value
            - suggestion must stay narrowly scoped to the reported defect or risk; use an empty string if no safe fix can be inferred
            - opportunities must stay grounded in the shown code and should not rest on hidden infrastructure assumptions
            - If the shown diff chunk is sufficient, return need_more_context=false and tool_requests=[]
            - Request extra context only when it is necessary to avoid speculation
            - tool_requests must contain at most 3 items
            - Use find_files when the file path is uncertain
            - Use find_usage when you need callers, consumers, or usage sites of a symbol, query-like method, or SQL use-case
            - Use grep_code when you need to discover the exact file or line before reading
            - Use read_file when the target path is already known or strongly inferable
            - If there are no clear defects, return "findings": []
            - If there are no useful improvement opportunities, return "opportunities": []
            """;
    }
}
