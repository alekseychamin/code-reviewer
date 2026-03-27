using System.Net.Http.Json;
using System.Text.Json;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;
using TfsReviewPlatform.Integrations.Llm;
using TfsReviewPlatform.Integrations.Publishing;

namespace TfsReviewPlatform.Integrations.GitLab;

internal sealed class GitLabReviewPublisherProvider(IHttpClientFactory httpClientFactory) : IPullRequestReviewPublisherProvider
{
    public bool CanHandle(string pullRequestUrl)
    {
        try
        {
            GitLabMergeRequestUrlParser.Parse(pullRequestUrl);
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
        var reference = GitLabMergeRequestUrlParser.Parse(pullRequestUrl);
        var client = CreateClient(accessToken);
        return await PostNoteAsync(client, reference.BuildNotesApiUrl(), reportContent, cancellationToken);
    }

    public async Task<bool> PublishAsync(
        string pullRequestUrl,
        string accessToken,
        PublishMode publishMode,
        string summaryComment,
        IReadOnlyList<InlineCommentDraft> inlineComments,
        CancellationToken cancellationToken)
    {
        var reference = GitLabMergeRequestUrlParser.Parse(pullRequestUrl);
        var client = CreateClient(accessToken);
        var summarySucceeded = await PostNoteAsync(client, reference.BuildNotesApiUrl(), summaryComment, cancellationToken);

        if (!summarySucceeded || publishMode != PublishMode.SummaryAndInline)
        {
            return summarySucceeded;
        }

        var inlineSucceeded = true;
        foreach (var inlineComment in inlineComments)
        {
            var success = await PublishInlineCommentInternalAsync(client, reference, inlineComment, cancellationToken);
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
        var reference = GitLabMergeRequestUrlParser.Parse(pullRequestUrl);
        var client = CreateClient(accessToken);
        return await PublishInlineCommentInternalAsync(client, reference, inlineComment, cancellationToken);
    }

    private HttpClient CreateClient(string accessToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientNames.GitLab);
        client.DefaultRequestHeaders.Add("PRIVATE-TOKEN", accessToken);
        return client;
    }

    private static async Task<bool> PostNoteAsync(
        HttpClient client,
        string url,
        string body,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(
        [
            KeyValuePair.Create("body", body)
        ]);
        using var response = await client.PostAsync(url, content, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private static async Task<bool> PublishInlineCommentInternalAsync(
        HttpClient client,
        GitLabMergeRequestRef reference,
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

        var position = await GetLatestPositionAsync(client, reference, cancellationToken);
        if (position is null)
        {
            return false;
        }

        using var content = new FormUrlEncodedContent(
        [
            KeyValuePair.Create("body", ResolveInlineCommentContent(inlineComment)),
            KeyValuePair.Create("position[position_type]", "text"),
            KeyValuePair.Create("position[base_sha]", position.BaseSha),
            KeyValuePair.Create("position[start_sha]", position.StartSha),
            KeyValuePair.Create("position[head_sha]", position.HeadSha),
            KeyValuePair.Create("position[old_path]", relativePath),
            KeyValuePair.Create("position[new_path]", relativePath),
            KeyValuePair.Create("position[new_line]", inlineComment.LineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture))
        ]);

        using var response = await client.PostAsync(reference.BuildDiscussionsApiUrl(), content, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private static async Task<GitLabDiscussionPosition?> GetLatestPositionAsync(
        HttpClient client,
        GitLabMergeRequestRef reference,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(reference.BuildVersionsApiUrl(), cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GitLab merge request versions request failed with status {(int)response.StatusCode}: {TrimForLog(payload)}");
        }

        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        var latestVersion = document.RootElement[0];
        var baseSha = latestVersion.TryGetProperty("base_commit_sha", out var baseShaElement) && baseShaElement.ValueKind == JsonValueKind.String
            ? baseShaElement.GetString()
            : null;
        var startSha = latestVersion.TryGetProperty("start_commit_sha", out var startShaElement) && startShaElement.ValueKind == JsonValueKind.String
            ? startShaElement.GetString()
            : null;
        var headSha = latestVersion.TryGetProperty("head_commit_sha", out var headShaElement) && headShaElement.ValueKind == JsonValueKind.String
            ? headShaElement.GetString()
            : null;

        return string.IsNullOrWhiteSpace(baseSha) ||
               string.IsNullOrWhiteSpace(startSha) ||
               string.IsNullOrWhiteSpace(headSha)
            ? null
            : new GitLabDiscussionPosition(baseSha, startSha, headSha);
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

    private sealed record GitLabDiscussionPosition(string BaseSha, string StartSha, string HeadSha);
}
