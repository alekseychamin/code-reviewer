using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TfsReviewPlatform.Application.Abstractions;
using TfsReviewPlatform.Application.Models;
using TfsReviewPlatform.Domain.Entities;
using TfsReviewPlatform.Domain.Enums;

namespace TfsReviewPlatform.Integrations.Llm;

public sealed class LlmCompletionService(
    IHttpClientFactory httpClientFactory,
    ILogger<LlmCompletionService> logger) : ILlmCompletionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxAttempts = 3;

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
        using var client = httpClientFactory.CreateClient(HttpClientNames.OpenAiCompatibleLlm);
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

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        message.Headers.ConnectionClose = true;

        using var response = await SendWithRetriesAsync(client, message, profile, request, cancellationToken);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
               ?? string.Empty;
    }

    private async Task<string> CompleteViaOllamaAsync(
        ProviderProfile profile,
        LlmChatRequest request,
        CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient(HttpClientNames.OllamaLlm);
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

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = JsonContent.Create(payload, options: JsonOptions)
        };

        using var response = await SendWithRetriesAsync(client, message, profile, request, cancellationToken);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
    }

    private async Task<HttpResponseMessage> SendWithRetriesAsync(
        HttpClient client,
        HttpRequestMessage template,
        ProviderProfile profile,
        LlmChatRequest request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? lastResponse = null;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastResponse?.Dispose();

            try
            {
                using var message = await CloneRequestAsync(template, cancellationToken);
                var response = await client.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                if (!IsTransientStatusCode(response.StatusCode) || attempt == MaxAttempts)
                {
                    var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new HttpRequestException(
                        $"LLM request failed with status code {(int)response.StatusCode} ({response.StatusCode}) for provider '{profile.Name}'. Response: {TrimForLog(responseBody)}");
                }

                lastResponse = response;
                logger.LogWarning(
                    "Transient LLM HTTP status {StatusCode} from provider {ProviderName} on attempt {Attempt}/{MaxAttempts} for model {Model}. Retrying.",
                    (int)response.StatusCode,
                    profile.Name,
                    attempt,
                    MaxAttempts,
                    request.Model ?? profile.DefaultModel);
            }
            catch (Exception exception) when (IsTransientException(exception, cancellationToken) && attempt < MaxAttempts)
            {
                lastException = exception;
                logger.LogWarning(
                    exception,
                    "Transient LLM transport failure from provider {ProviderName} on attempt {Attempt}/{MaxAttempts} for model {Model}. Retrying.",
                    profile.Name,
                    attempt,
                    MaxAttempts,
                    request.Model ?? profile.DefaultModel);
            }

            if (attempt < MaxAttempts)
            {
                await Task.Delay(GetRetryDelay(attempt), cancellationToken);
            }
        }

        if (lastException is not null)
        {
            throw new HttpRequestException(
                $"LLM request failed after {MaxAttempts} attempts for provider '{profile.Name}'.",
                lastException);
        }

        throw new HttpRequestException(
            $"LLM request failed after {MaxAttempts} attempts for provider '{profile.Name}'.");
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(
        HttpRequestMessage template,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(template.Method, template.RequestUri)
        {
            Version = template.Version,
            VersionPolicy = template.VersionPolicy
        };

        foreach (var header in template.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (template.Content is not null)
        {
            var bytes = await template.Content.ReadAsByteArrayAsync(cancellationToken);
            var content = new ByteArrayContent(bytes);
            foreach (var header in template.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = content;
        }

        return clone;
    }

    private static bool IsTransientStatusCode(System.Net.HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code == 408 || code == 409 || code == 425 || code == 429 || code >= 500;
    }

    private static bool IsTransientException(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException)
        {
            return !cancellationToken.IsCancellationRequested;
        }

        if (exception is HttpRequestException)
        {
            return true;
        }

        return exception is IOException;
    }

    private static TimeSpan GetRetryDelay(int attempt)
    {
        return attempt switch
        {
            1 => TimeSpan.FromSeconds(1),
            2 => TimeSpan.FromSeconds(3),
            _ => TimeSpan.FromSeconds(5)
        };
    }

    private static string TrimForLog(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "<empty>";
        }

        return content.Length <= 1200
            ? content
            : $"{content[..1200]}...";
    }
}
