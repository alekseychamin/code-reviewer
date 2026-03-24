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
                You are a Senior Technical Lead.
                Produce a concise markdown description of the pull request or branch comparison.
                Include:
                - Category: Feature, Bugfix, Refactoring, Hotfix, or Config update
                - 2-3 sentence summary
                - 3-5 key impacted modules or layers
                Write in Russian.
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
            ReviewPipelineStage.ChangeDescription => $"Analyze these changes and describe the review target:\n\n{payload}",
            ReviewPipelineStage.ChunkReview => $"Review this diff chunk:\n\n{payload}",
            _ => payload
        };
    }
}
