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
                - Markdown in Russian
                - Category: Feature, Bugfix, Refactoring, Hotfix, or Config update
                - 2-3 sentence summary of business or technical value
                - 3-5 key impacted modules or layers

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
                Review the supplied diff chunk and return ONLY a valid JSON array.
                Review context:
                """ + "\n" + reviewContext + "\n\n" + """
                Focus on:
                - Security, reliability, race conditions, N+1, blocking async calls, resource leaks
                - Architecture regressions and broken test intent
                Ignore purely cosmetic style issues.

                JSON item schema:
                {
                  "file": "path/to/file.cs",
                  "line_hint": "method or class name",
                  "type": "Security | Performance | Architecture | Bug | Reliability | Logic | CodeStyle",
                  "severity": "Critical | High | Medium | Low",
                  "title": "short title",
                  "description": "why this matters",
                  "existing_code": "code snippet from diff",
                  "suggestion": "improved code or mitigation"
                }

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
