using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;
using TfsReviewPlatform.Integrations.Llm;
using TfsReviewPlatform.Integrations.Publishing;

namespace TfsReviewPlatform.Integrations.GitHub;

internal sealed class GitHubReviewPublisherProvider(IHttpClientFactory httpClientFactory) : IPullRequestReviewPublisherProvider
{
    public bool CanHandle(string pullRequestUrl)
    {
        try
        {
            GitHubPullRequestUrlParser.Parse(pullRequestUrl);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> PublishReportAsync(
        string pullRequestUrl,
        string accessToken,
        string reportContent,
        CancellationToken cancellationToken)
    {
        var reference = GitHubPullRequestUrlParser.Parse(pullRequestUrl);
        var client = CreateClient(accessToken);

        using var response = await client.PostAsJsonAsync(
            reference.BuildIssueCommentsApiUrl(),
            new { body = reportContent },
            cancellationToken);

        return response.IsSuccessStatusCode;
    }

    public async Task<bool> PublishAsync(
        string pullRequestUrl,
        string accessToken,
        PublishMode publishMode,
        string summaryComment,
        IReadOnlyList<InlineCommentDraft> inlineComments,
        CancellationToken cancellationToken)
    {
        var reference = GitHubPullRequestUrlParser.Parse(pullRequestUrl);
        var client = CreateClient(accessToken);
        var headSha = await GetHeadShaAsync(client, reference, cancellationToken);

        using var summaryResponse = await client.PostAsJsonAsync(
            reference.BuildIssueCommentsApiUrl(),
            new { body = summaryComment },
            cancellationToken);

        if (!summaryResponse.IsSuccessStatusCode || publishMode != PublishMode.SummaryAndInline)
        {
            return summaryResponse.IsSuccessStatusCode;
        }

        var inlineSucceeded = true;
        foreach (var inlineComment in inlineComments)
        {
            var success = await PostInlineCommentAsync(client, reference, headSha, inlineComment, cancellationToken);
            inlineSucceeded &= success;
        }

        return inlineSucceeded;
    }

    public async Task<bool> PublishInlineCommentAsync(
        string pullRequestUrl,
        string accessToken,
        InlineCommentDraft inlineComment,
        CancellationToken cancellationToken)
    {
        var reference = GitHubPullRequestUrlParser.Parse(pullRequestUrl);
        var client = CreateClient(accessToken);
        var headSha = await GetHeadShaAsync(client, reference, cancellationToken);
        return await PostInlineCommentAsync(client, reference, headSha, inlineComment, cancellationToken);
    }

    private HttpClient CreateClient(string accessToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientNames.GitHub);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private static async Task<bool> PostInlineCommentAsync(
        HttpClient client,
        GitHubPullRequestRef reference,
        string headSha,
        InlineCommentDraft inlineComment,
        CancellationToken cancellationToken)
    {
        if (inlineComment.LineNumber <= 0)
        {
            return false;
        }

        var relativePath = inlineComment.FilePath
            .Replace("\\", "/", StringComparison.Ordinal)
            .TrimStart('/');

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        using var response = await client.PostAsJsonAsync(
            reference.BuildReviewCommentsApiUrl(),
            new
            {
                body = ResolveInlineCommentContent(inlineComment),
                commit_id = headSha,
                path = relativePath,
                side = "RIGHT",
                line = inlineComment.LineNumber
            },
            cancellationToken);

        return response.IsSuccessStatusCode;
    }

    private static async Task<string> GetHeadShaAsync(
        HttpClient client,
        GitHubPullRequestRef reference,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(reference.BuildPullRequestApiUrl(), cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GitHub pull request metadata request failed with status {(int)response.StatusCode}: {TrimForLog(payload)}");
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        return root.GetProperty("head").GetProperty("sha").GetString()
               ?? throw new InvalidOperationException("GitHub response does not contain head.sha.");
    }

    private static string ResolveInlineCommentContent(InlineCommentDraft inlineComment)
    {
        var initialAssistantMessage = inlineComment.Messages.FirstOrDefault(message =>
            string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase));

        return !string.IsNullOrWhiteSpace(initialAssistantMessage?.Content)
            ? initialAssistantMessage.Content
            : inlineComment.Content;
    }

    private static string TrimForLog(string text)
    {
        var compact = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return compact.Length <= 400 ? compact : compact[..400];
    }
}
