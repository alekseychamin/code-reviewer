using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;
using TfsReviewPlatform.Integrations.Llm;

namespace TfsReviewPlatform.Integrations.AzureDevOps;

public sealed class AzureDevOpsReviewPublisher(IHttpClientFactory httpClientFactory) : IReviewPublisher
{
    private const string ApiVersion = "6.0";

    public async Task<bool> PublishReportAsync(
        string pullRequestUrl,
        string accessToken,
        string reportContent,
        CancellationToken cancellationToken)
    {
        var reference = AzureDevOpsUrlParser.Parse(pullRequestUrl);
        var client = httpClientFactory.CreateClient(HttpClientNames.AzureDevOps);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", EncodePat(accessToken));

        return await PostThreadAsync(
            client,
            reference.BuildThreadsApiUrl(ApiVersion),
            new
            {
                comments = new[]
                {
                    new
                    {
                        parentCommentId = 0,
                        content = reportContent,
                        commentType = 1
                    }
                },
                status = 1
            },
            cancellationToken);
    }

    public async Task<bool> PublishAsync(
        string pullRequestUrl,
        string accessToken,
        PublishMode publishMode,
        string summaryComment,
        IReadOnlyList<InlineCommentDraft> inlineComments,
        CancellationToken cancellationToken)
    {
        var reference = AzureDevOpsUrlParser.Parse(pullRequestUrl);
        var client = httpClientFactory.CreateClient(HttpClientNames.AzureDevOps);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", EncodePat(accessToken));

        var summarySucceeded = await PostThreadAsync(
            client,
            reference.BuildThreadsApiUrl(ApiVersion),
            new
            {
                comments = new[]
                {
                    new
                    {
                        parentCommentId = 0,
                        content = summaryComment,
                        commentType = 1
                    }
                },
                status = 1
            },
            cancellationToken);

        if (!summarySucceeded || publishMode != PublishMode.SummaryAndInline)
        {
            return summarySucceeded;
        }

        var inlineSucceeded = true;
        foreach (var inlineComment in inlineComments)
        {
            var success = await PostInlineThreadAsync(client, reference.BuildThreadsApiUrl(ApiVersion), inlineComment, cancellationToken);

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
        var reference = AzureDevOpsUrlParser.Parse(pullRequestUrl);
        var client = httpClientFactory.CreateClient(HttpClientNames.AzureDevOps);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", EncodePat(accessToken));
        return await PostInlineThreadAsync(client, reference.BuildThreadsApiUrl(ApiVersion), inlineComment, cancellationToken);
    }

    private static async Task<bool> PostThreadAsync(
        HttpClient client,
        string url,
        object payload,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(url, payload, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private static Task<bool> PostInlineThreadAsync(
        HttpClient client,
        string url,
        InlineCommentDraft inlineComment,
        CancellationToken cancellationToken)
    {
        return PostThreadAsync(
            client,
            url,
            new
            {
                comments = new[]
                {
                    new
                    {
                        parentCommentId = 0,
                        content = ResolveInlineCommentContent(inlineComment),
                        commentType = 1
                    }
                },
                status = 1,
                threadContext = new
                {
                    filePath = inlineComment.FilePath,
                    rightFileStart = new { line = inlineComment.LineNumber, offset = 1 },
                    rightFileEnd = new { line = inlineComment.LineNumber, offset = 1 }
                }
            },
            cancellationToken);
    }

    private static string ResolveInlineCommentContent(InlineCommentDraft inlineComment)
    {
        var initialAssistantMessage = inlineComment.Messages.FirstOrDefault(message =>
            string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase));

        return !string.IsNullOrWhiteSpace(initialAssistantMessage?.Content)
            ? initialAssistantMessage.Content
            : inlineComment.Content;
    }

    private static string EncodePat(string accessToken)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"pat:{accessToken}"));
}
