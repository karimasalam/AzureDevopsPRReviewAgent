using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using PRReviewAgent.Configuration;
using PRReviewAgent.Models;

namespace PRReviewAgent.Services.AI;

public class OpenAIReviewProvider : IAIReviewProvider
{
    private readonly AppSettings _settings;
    private readonly ILogger<OpenAIReviewProvider> _logger;

    public string Name => "OpenAI";

    public OpenAIReviewProvider(AppSettings settings, ILogger<OpenAIReviewProvider> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<ReviewResult> ReviewAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.OpenAIApiKey))
            throw new InvalidOperationException("OpenAI API key is not configured. Set OPENAI_API_KEY or use --openai-key.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_settings.ApiTimeoutSeconds));

        var client = new ChatClient(_settings.OpenAIModel, _settings.OpenAIApiKey);

        _logger.LogInformation("Sending review request to OpenAI ({Model})...", _settings.OpenAIModel);

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                "code_review",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "summary": { "type": "string" },
                        "comments": {
                            "type": "array",
                            "items": {
                                "type": "object",
                                "properties": {
                                    "filePath": { "type": "string" },
                                    "lineNumber": { "type": "integer" },
                                    "body": { "type": "string" },
                                    "severity": { "type": "string", "enum": ["critical", "warning", "info"] }
                                },
                                "required": ["filePath", "lineNumber", "body", "severity"],
                                "additionalProperties": false
                            }
                        }
                    },
                    "required": ["summary", "comments"],
                    "additionalProperties": false
                }
                """),
                jsonSchemaIsStrict: true)
        };

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("You are an expert code reviewer. Respond only with valid JSON matching the provided schema."),
            new UserChatMessage(prompt)
        };

        var completion = await client.CompleteChatAsync(messages, options, cts.Token);
        var json = completion.Value.Content[0].Text;

        _logger.LogDebug("OpenAI response: {Response}", json);

        var result = JsonSerializer.Deserialize<ReviewResult>(json)
            ?? throw new InvalidOperationException("Failed to deserialize OpenAI response.");

        _logger.LogInformation("OpenAI review complete: {CommentCount} comments", result.Comments.Count);
        return result;
    }
}
