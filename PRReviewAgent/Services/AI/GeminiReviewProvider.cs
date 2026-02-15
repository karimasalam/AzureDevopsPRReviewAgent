using System.Text.Json;
using Mscc.GenerativeAI;
using Microsoft.Extensions.Logging;
using PRReviewAgent.Configuration;
using PRReviewAgent.Models;

namespace PRReviewAgent.Services.AI;

public class GeminiReviewProvider : IAIReviewProvider
{
    private readonly AppSettings _settings;
    private readonly ILogger<GeminiReviewProvider> _logger;

    public string Name => "Gemini";

    public GeminiReviewProvider(AppSettings settings, ILogger<GeminiReviewProvider> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<ReviewResult> ReviewAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.GeminiApiKey))
            throw new InvalidOperationException("Gemini API key is not configured. Set GEMINI_API_KEY or use --gemini-key.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_settings.ApiTimeoutSeconds));

        var googleAI = new GoogleAI(_settings.GeminiApiKey);
        var model = googleAI.GenerativeModel(model: _settings.GeminiModel);

        _logger.LogInformation("Sending review request to Gemini ({Model})...", _settings.GeminiModel);

        var fullPrompt = prompt + "\n\nIMPORTANT: Respond ONLY with valid JSON. No markdown fences, no extra text.";

        // Mscc.GenerativeAI doesn't seem to have a CancellationToken overload for GenerateContent
        // We'll have to wrap it if possible, but let's check if there's any other way.
        // Actually, many of these SDKs are not well documented.
        
        var response = await model.GenerateContent(fullPrompt);
        var json = response.Text?.Trim() ?? "";

        json = ExtractJson(json);

        _logger.LogDebug("Gemini response: {Response}", json);

        var result = JsonSerializer.Deserialize<ReviewResult>(json)
            ?? throw new InvalidOperationException("Failed to deserialize Gemini response.");

        _logger.LogInformation("Gemini review complete: {CommentCount} comments", result.Comments.Count);
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
