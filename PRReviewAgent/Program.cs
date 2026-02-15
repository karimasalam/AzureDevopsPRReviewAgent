using System.CommandLine;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PRReviewAgent.Configuration;
using PRReviewAgent.Helpers;
using PRReviewAgent.Models;
using PRReviewAgent.Services;
using PRReviewAgent.Services.AI;
using PRReviewAgent.Services.AzureDevOps;

var prUrlArg = new Argument<string?>("pr-url", () => null,
    "Azure DevOps PR URL (e.g. https://dev.azure.com/org/project/_git/repo/pullrequest/42)");

var orgOption = new Option<string?>("--org", "Azure DevOps organization name");
var projectOption = new Option<string?>("--project", "Azure DevOps project name");
var repoOption = new Option<string?>("--repo", "Azure DevOps repository name");
var prIdOption = new Option<int?>("--pr-id", "Pull request ID");
var providerOption = new Option<string>("--provider", () => "openai",
    "AI provider: openai, gemini, claude, openrouter, or deepseek");
var dryRunOption = new Option<bool>("--dry-run", () => false,
    "Print review to console without posting comments to the PR");
var adoPatOption = new Option<string?>("--ado-pat", "Azure DevOps Personal Access Token");
var openaiKeyOption = new Option<string?>("--openai-key", "OpenAI API key");
var geminiKeyOption = new Option<string?>("--gemini-key", "Gemini API key");
var openrouterKeyOption = new Option<string?>("--openrouter-key", "OpenRouter API key");
var deepseekKeyOption = new Option<string?>("--deepseek-key", "DeepSeek API key");
var timeoutOption = new Option<int?>("--timeout", "API timeout in seconds");
var problemOption = new Option<string?>("--problem", "Optional problem statement that this PR is fixing or creating");

var rootCommand = new RootCommand("PR Review Agent - AI-powered Azure DevOps pull request reviewer")
{
    prUrlArg,
    orgOption,
    projectOption,
    repoOption,
    prIdOption,
    providerOption,
    dryRunOption,
    adoPatOption,
    openaiKeyOption,
    geminiKeyOption,
    openrouterKeyOption,
    deepseekKeyOption,
    timeoutOption,
    problemOption
};

rootCommand.SetHandler(async (context) =>
{
    var prUrl = context.ParseResult.GetValueForArgument(prUrlArg);
    var org = context.ParseResult.GetValueForOption(orgOption);
    var project = context.ParseResult.GetValueForOption(projectOption);
    var repo = context.ParseResult.GetValueForOption(repoOption);
    var prId = context.ParseResult.GetValueForOption(prIdOption);
    var provider = context.ParseResult.GetValueForOption(providerOption)!;
    var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
    var adoPat = context.ParseResult.GetValueForOption(adoPatOption);
    var openaiKey = context.ParseResult.GetValueForOption(openaiKeyOption);
    var geminiKey = context.ParseResult.GetValueForOption(geminiKeyOption);
    var openrouterKey = context.ParseResult.GetValueForOption(openrouterKeyOption);
    var deepseekKey = context.ParseResult.GetValueForOption(deepseekKeyOption);
    var timeout = context.ParseResult.GetValueForOption(timeoutOption);
    var problem = context.ParseResult.GetValueForOption(problemOption);
    var cancellationToken = context.GetCancellationToken();

    // Resolve PR coordinates
    PullRequestInfo? prInfo = null;

    if (!string.IsNullOrWhiteSpace(prUrl))
    {
        prInfo = PullRequestUrlParser.TryParse(prUrl);
        if (prInfo is null)
        {
            Console.Error.WriteLine($"Error: Could not parse PR URL: {prUrl}");
            context.ExitCode = 1;
            return;
        }
    }
    else if (!string.IsNullOrWhiteSpace(org) && !string.IsNullOrWhiteSpace(project)
             && !string.IsNullOrWhiteSpace(repo) && prId.HasValue)
    {
        prInfo = new PullRequestInfo
        {
            Organization = org,
            Project = project,
            Repository = repo,
            PullRequestId = prId.Value
        };
    }
    else
    {
        Console.Error.WriteLine("Error: Provide either a PR URL or --org, --project, --repo, and --pr-id.");
        context.ExitCode = 1;
        return;
    }

    // Build configuration
    var configBuilder = new ConfigurationBuilder();

    var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    if (File.Exists(appSettingsPath))
        configBuilder.AddJsonFile(appSettingsPath, optional: true);

    configBuilder.AddEnvironmentVariables();
    configBuilder.AddUserSecrets<Program>();

    var configuration = configBuilder.Build();

    // Build AppSettings with priority: CLI > env vars > appsettings.json
    var settings = new AppSettings
    {
        AzureDevOpsPat = adoPat
            ?? Environment.GetEnvironmentVariable("AZURE_DEVOPS_PAT")
            ?? configuration["AzureDevOpsPat"],
        OpenAIApiKey = openaiKey
            ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? configuration["OpenAIApiKey"],
        GeminiApiKey = geminiKey
            ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? configuration["GeminiApiKey"],
        OpenRouterApiKey = openrouterKey
            ?? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
            ?? configuration["OpenRouterApiKey"],
        DeepSeekApiKey = deepseekKey
            ?? Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
            ?? configuration["DeepSeekApiKey"],
        Provider = provider
    };

    var timeoutStr = configuration["ApiTimeoutSeconds"] 
                     ?? Environment.GetEnvironmentVariable("API_TIMEOUT_SECONDS");
    if (int.TryParse(timeoutStr, out var t))
        settings.ApiTimeoutSeconds = t;
    
    if (timeout.HasValue)
        settings.ApiTimeoutSeconds = timeout.Value;

    if (!string.IsNullOrWhiteSpace(configuration["OpenRouterModel"]))
        settings.OpenRouterModel = configuration["OpenRouterModel"]!;

    var openRouterModelEnv = Environment.GetEnvironmentVariable("OPENROUTER_MODEL");
    if (!string.IsNullOrWhiteSpace(openRouterModelEnv))
        settings.OpenRouterModel = openRouterModelEnv;

    if (!string.IsNullOrWhiteSpace(configuration["DeepSeekModel"]))
        settings.DeepSeekModel = configuration["DeepSeekModel"]!;

    var deepSeekModelEnv = Environment.GetEnvironmentVariable("DEEPSEEK_MODEL");
    if (!string.IsNullOrWhiteSpace(deepSeekModelEnv))
        settings.DeepSeekModel = deepSeekModelEnv;

    // Set up DI
    var services = new ServiceCollection();

    services.AddLogging(builder =>
    {
        builder.AddConsole();
        builder.SetMinimumLevel(LogLevel.Information);
    });

    services.AddSingleton(settings);
    services.AddSingleton<IAzureDevOpsClient, AzureDevOpsClient>();
    services.AddSingleton<ReviewOrchestrator>();

    // Register AI provider based on selection
    switch (provider.ToLowerInvariant())
    {
        case "openai":
            services.AddSingleton<IAIReviewProvider, OpenAIReviewProvider>();
            break;
        case "gemini":
            services.AddSingleton<IAIReviewProvider, GeminiReviewProvider>();
            break;
        case "claude":
            services.AddSingleton<IAIReviewProvider, ClaudeCliReviewProvider>();
            break;
        case "openrouter":
            services.AddSingleton<IAIReviewProvider, OpenRouterReviewProvider>();
            break;
        case "deepseek":
            services.AddSingleton<IAIReviewProvider, DeepSeekReviewProvider>();
            break;
        default:
            Console.Error.WriteLine(
                $"Error: Unknown provider '{provider}'. Use 'openai', 'gemini', 'claude', 'openrouter', or 'deepseek'.");
            context.ExitCode = 1;
            return;
    }

    var serviceProvider = services.BuildServiceProvider();
    var orchestrator = serviceProvider.GetRequiredService<ReviewOrchestrator>();

    try
    {
        await orchestrator.RunAsync(prInfo, dryRun, problem, cancellationToken);
    }
    catch (Exception ex)
    {
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Review failed");
        context.ExitCode = 1;
    }
});

static string PromptRequired(string prompt)
{
    while (true)
    {
        Console.Write(prompt);
        var input = Console.ReadLine();
        if (!string.IsNullOrWhiteSpace(input))
            return input.Trim();
    }
}

static bool PromptYesNo(string prompt)
{
    while (true)
    {
        Console.Write(prompt);
        var input = (Console.ReadLine() ?? string.Empty).Trim();
        if (input.Equals("y", StringComparison.OrdinalIgnoreCase)
            || input.Equals("yes", StringComparison.OrdinalIgnoreCase))
            return true;

        if (input.Equals("n", StringComparison.OrdinalIgnoreCase)
            || input.Equals("no", StringComparison.OrdinalIgnoreCase))
            return false;
    }
}

static string PromptProvider(string prompt)
{
    while (true)
    {
        Console.Write(prompt);
        var input = (Console.ReadLine() ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(input))
            return "openai";

        if (input.Equals("openai", StringComparison.OrdinalIgnoreCase)
            || input.Equals("gemini", StringComparison.OrdinalIgnoreCase)
            || input.Equals("claude", StringComparison.OrdinalIgnoreCase)
            || input.Equals("deepseek", StringComparison.OrdinalIgnoreCase))
            return input.ToLowerInvariant();

        if (input.Equals("openrouter", StringComparison.OrdinalIgnoreCase))
            return "openrouter";
    }
}

static string? PromptMultiLine(string prompt)
{
    Console.WriteLine(prompt);
    var sb = new StringBuilder();
    while (true)
    {
        var line = Console.ReadLine();
        if (line == "." || line == null)
            break;

        if (sb.Length == 0 && string.IsNullOrWhiteSpace(line))
            break;

        sb.AppendLine(line);
    }

    var result = sb.ToString().Trim();
    return string.IsNullOrEmpty(result) ? null : result;
}

if (args.Length == 0)
{
    var prUrl = PromptRequired("PR URL: ");
    var provider = PromptProvider("Agent/provider (openai/gemini/claude/openrouter/deepseek) [openai]: ");
    var dryRun = PromptYesNo("Dry run? (y/n): ");
    
    var problem = PromptMultiLine("Problem statement (optional). Enter/paste text and end with '.' on a new line, or press Enter to skip:");

    var argsList = new List<string> { prUrl, "--provider", provider };
    if (dryRun) argsList.Add("--dry-run");
    if (!string.IsNullOrWhiteSpace(problem))
    {
        argsList.Add("--problem");
        argsList.Add(problem);
    }
    args = argsList.ToArray();
}

return await rootCommand.InvokeAsync(args);

// Required for ILogger<Program> to work with top-level statements
public partial class Program;
