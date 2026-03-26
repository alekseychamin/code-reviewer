using TfsReviewPlatform.Application.Abstractions;
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
                    "summary": "2-3 sentence summary of business or technical value",
                    "impacted_modules": ["3-5 key impacted modules or layers"],
                    "risks": ["optional risk or follow-up point", "optional second point"]
                  }
                - Do not use markdown markers such as **, __, bullets, or fenced code in any field
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
            ReviewPipelineStage.ChunkReview => """
                You are a Principal .NET Architect and strict code reviewer.
                Answer in Russian.
                Review the supplied diff chunk and return ONLY a valid JSON array.
                Review context:
                """ + "\n" + reviewContext + "\n\n" + """
                Scope and priorities:
                - Focus only on changed code in the supplied diff chunk, primarily added or modified logic
                - Prioritize high-signal issues: security, reliability, race conditions, N+1, blocking async calls, resource leaks, architecture regressions, broken test intent, and real logic bugs
                - Prefer returning fewer findings over noisy or speculative findings

                Important review rules:
                - Do not flag issues that are already fixed by the patch
                - Do not suggest style-only, naming-only, formatting-only, comment-only, docstring-only, or type-hint-only changes
                - Do not suggest adding imports, removing unused imports, or using a more specific exception type unless correctness directly depends on it
                - Do not assume missing surrounding code is a bug; you only see a diff chunk, not the whole file or repository
                - If the visible code ends at a scope boundary like if/for/try/method/class, do not treat that as incomplete code
                - Do not question declarations, using directives, or helpers that may exist outside the shown diff unless the diff itself makes the defect clear
                - Only report an issue when the visible changed code provides enough evidence
                - All string fields must be plain text without markdown markers such as **, __, bullets, or fenced code blocks
                - All human-readable output fields must be in Russian
                - file must stay as the original file path from the diff
                - existing_code must stay as the original code snippet from the diff
                - line_hint may use method, class, or test identifiers from code and does not need translation

                JSON item schema:
                {
                  "kind": "Defect | Risk",
                  "file": "path/to/file.cs",
                  "line_hint": "nearest method, class, or test name from the changed code",
                  "start_line": 123,
                  "end_line": 126,
                  "type": "Security | Performance | Architecture | Bug | Reliability | Logic",
                  "severity": "Critical | High | Medium | Low",
                  "title": "short title, ideally 3-8 words",
                  "description": "why this matters, concrete and concise",
                  "existing_code": "code snippet from the changed lines only",
                  "suggestion": "minimal mitigation or improved code"
                }

                Additional output rules:
                - kind must be either Defect or Risk
                - Every finding must describe a concrete defect or an operational risk directly evidenced by the changed code
                - If an observation is mainly an improvement suggestion, refactoring idea, readability improvement, cleanup, or code-style preference, do not return it
                - title, description, and suggestion must be written in Russian
                - Only include findings that a human reviewer should realistically inspect before merge
                - Do not propose alternative designs unless the current changed code is likely wrong, unsafe, or materially inefficient
                - Do not suggest extra validation, null checks, logging, retries, caching, or abstractions unless the diff shows a realistic failing path
                - Use Low severity sparingly; if the issue would not change a reviewer decision, omit it
                - start_line and end_line must refer to the changed code in the new version of the file
                - If you know only one exact line, set start_line and end_line to the same value
                - suggestion must stay narrowly scoped to the reported defect or risk; use an empty string if no safe fix can be inferred
                - If there are no clear issues, return []
                If there are no issues, return [].
                """,
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
}
