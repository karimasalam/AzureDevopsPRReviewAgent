using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PRReviewAgent.Configuration;
using PRReviewAgent.Models;

namespace PRReviewAgent.Services.AI;

public class ClaudeCliReviewProvider : IAIReviewProvider
{
    private readonly AppSettings _settings;
    private readonly ILogger<ClaudeCliReviewProvider> _logger;

    public string Name => "Claude (CLI)";

    public ClaudeCliReviewProvider(AppSettings settings, ILogger<ClaudeCliReviewProvider> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<ReviewResult> ReviewAsync(string prompt, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Sending review request to Claude Code CLI...");

        // Write prompt to a temp file to avoid stdin/pipe issues with large prompts
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, prompt, cancellationToken);

            var psi = new ProcessStartInfo
            {
                FileName = "claude",
                ArgumentList = { "-p", "--output-format", "json" },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start claude CLI. Ensure 'claude' is installed and on your PATH.");

            // Write prompt to stdin and close immediately
            await process.StandardInput.WriteAsync(prompt);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();

            // Read stdout and stderr concurrently to avoid deadlocks
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(_settings.ApiTimeoutSeconds), cancellationToken);
            var completedTask = await Task.WhenAny(process.WaitForExitAsync(cancellationToken), timeoutTask);

            if (completedTask == timeoutTask)
            {
                process.Kill(true);
                throw new TimeoutException($"Claude CLI review timed out after {_settings.ApiTimeoutSeconds} seconds.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                _logger.LogError("Claude CLI exited with code {ExitCode}. StdErr: {StdErr} StdOut: {StdOut}",
                    process.ExitCode, stderr, stdout);
                throw new InvalidOperationException(
                    $"Claude CLI exited with code {process.ExitCode}: {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}");
            }

            _logger.LogDebug("Claude CLI response: {Response}", stdout);

            // Claude CLI with --output-format json wraps the result; extract the text content
            var cliOutput = JsonSerializer.Deserialize<JsonElement>(stdout);
            var text = cliOutput.GetProperty("result").GetString()
                ?? throw new InvalidOperationException("Claude CLI returned empty result.");

            // Strip markdown code fences if present
            var json = text.Trim();
            json = ExtractJson(json);

            var result = JsonSerializer.Deserialize<ReviewResult>(json)
                ?? throw new InvalidOperationException("Failed to deserialize Claude CLI response.");

            _logger.LogInformation("Claude CLI review complete: {CommentCount} comments", result.Comments.Count);
            return result;
        }
        finally
        {
            File.Delete(tempFile);
        }
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
