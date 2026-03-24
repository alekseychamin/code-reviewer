using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Integrations.Llm;

public sealed class LlmCompletionService(IHttpClientFactory httpClientFactory) : ILlmCompletionService
{
    public async Task<string> CompleteAsync(
        ProviderProfile profile,
        LlmChatRequest request,
        CancellationToken cancellationToken)
    {
        return profile.Kind switch
        {
            LlmProviderKind.OpenAiCompatible => await CompleteViaOpenAiCompatibleAsync(profile, request, cancellationToken),
            LlmProviderKind.Ollama => await CompleteViaOllamaAsync(profile, request, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported provider kind '{profile.Kind}'.")
        };
    }

    private async Task<string> CompleteViaOpenAiCompatibleAsync(
        ProviderProfile profile,
        LlmChatRequest request,
        CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient();
        var apiKey = profile.ResolveApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        var endpoint = $"{profile.BaseUrl.TrimEnd('/')}/chat/completions";
        var payload = new
        {
            model = request.Model ?? profile.DefaultModel,
            temperature = request.Temperature,
            stream = false,
            messages = new[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt }
            }
        };

        using var response = await client.PostAsJsonAsync(endpoint, payload, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
               ?? string.Empty;
    }

    private async Task<string> CompleteViaOllamaAsync(
        ProviderProfile profile,
        LlmChatRequest request,
        CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient();
        var endpoint = $"{profile.BaseUrl.TrimEnd('/')}/api/chat";
        var payload = new
        {
            model = request.Model ?? profile.DefaultModel,
            stream = false,
            format = request.ExpectJson ? "json" : null,
            messages = new[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt }
            }
        };

        using var response = await client.PostAsJsonAsync(endpoint, payload, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
    }
}
