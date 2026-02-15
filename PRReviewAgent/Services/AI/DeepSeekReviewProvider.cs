using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PRReviewAgent.Configuration;
using PRReviewAgent.Models;

namespace PRReviewAgent.Services.AI;

public class DeepSeekReviewProvider : IAIReviewProvider
{
    private readonly AppSettings _settings;
    private readonly ILogger<DeepSeekReviewProvider> _logger;

    public string Name => "DeepSeek";

    public DeepSeekReviewProvider(AppSettings settings, ILogger<DeepSeekReviewProvider> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<ReviewResult> ReviewAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.DeepSeekApiKey))
            throw new InvalidOperationException(
                "DeepSeek API key is not configured. Set DEEPSEEK_API_KEY or configure DeepSeekApiKey.");

        using var http = new HttpClient
        {
            BaseAddress = new Uri("https://api.deepseek.com/"),
            Timeout = TimeSpan.FromSeconds(_settings.ApiTimeoutSeconds)
        };

        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _settings.DeepSeekApiKey);

        _logger.LogInformation("Sending review request to DeepSeek ({Model})...", _settings.DeepSeekModel);

        var request = new
        {
            model = _settings.DeepSeekModel,
            messages = new object[]
            {
                new { role = "system", content = "You are an expert code reviewer. Respond ONLY with valid JSON. No markdown fences, no extra text." },
                new { role = "user", content = prompt }
            },
            response_format = new { type = "json_object" }
        };

        var jsonBody = JsonSerializer.Serialize(request);
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        using var response = await http.PostAsync("v1/chat/completions", content, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"DeepSeek request failed with status code {(int)response.StatusCode}: {responseText}");

        using var doc = JsonDocument.Parse(responseText);
        var messageContent = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        var json = (messageContent ?? string.Empty).Trim();
        json = ExtractJson(json);

        _logger.LogDebug("DeepSeek response: {Response}", json);

        var result = JsonSerializer.Deserialize<ReviewResult>(json)
            ?? throw new InvalidOperationException("Failed to deserialize DeepSeek response.");

        _logger.LogInformation("DeepSeek review complete: {CommentCount} comments", result.Comments.Count);
        return result;
    }

    private static string ExtractJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return content;

        var firstBrace = content.IndexOf('{');
        var lastBrace = content.LastIndexOf('}');

        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            return content.Substring(firstBrace, lastBrace - firstBrace + 1);
        }

        return content;
    }
}
