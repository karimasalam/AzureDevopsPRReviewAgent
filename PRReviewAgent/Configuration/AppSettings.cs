namespace PRReviewAgent.Configuration;

public class AppSettings
{
    public string? AzureDevOpsPat { get; set; }
    public string? OpenAIApiKey { get; set; }
    public string? GeminiApiKey { get; set; }
    public string? OpenRouterApiKey { get; set; }
    public string? DeepSeekApiKey { get; set; }
    public string Provider { get; set; } = "openai";
    public string OpenAIModel { get; set; } = "gpt-4o";
    public string GeminiModel { get; set; } = "gemini-2.0-flash";
    public string OpenRouterModel { get; set; } = "openai/gpt-4o-mini";
    public string DeepSeekModel { get; set; } = "deepseek-reasoner";
    public int ApiTimeoutSeconds { get; set; } = 3000;
}
