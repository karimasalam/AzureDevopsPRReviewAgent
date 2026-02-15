using Microsoft.Extensions.Logging;
using PRReviewAgent.Models;
using PRReviewAgent.Services.AI;
using PRReviewAgent.Services.AzureDevOps;

namespace PRReviewAgent.Services;

public class ReviewOrchestrator
{
    // Rough char limit per chunk to stay within model context windows.
    // ~200k chars ≈ ~50k tokens, well within limits for all providers.
    private const int MaxCharsPerChunk = 300_000;

    private readonly IAzureDevOpsClient _adoClient;
    private readonly IAIReviewProvider _aiProvider;
    private readonly ILogger<ReviewOrchestrator> _logger;

    public ReviewOrchestrator(
        IAzureDevOpsClient adoClient,
        IAIReviewProvider aiProvider,
        ILogger<ReviewOrchestrator> logger)
    {
        _adoClient = adoClient;
        _aiProvider = aiProvider;
        _logger = logger;
    }

    public async Task<ReviewResult> RunAsync(PullRequestInfo prInfo, bool dryRun, string? problemStatement = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting PR review using {Provider}...", _aiProvider.Name);

        // Step 1: Fetch PR metadata
        var (title, description) = await _adoClient.GetPullRequestInfoAsync(prInfo, cancellationToken);
        _logger.LogInformation("PR: {Title}", title);

        // Step 2: Fetch diffs
        var diffs = await _adoClient.GetPullRequestDiffsAsync(prInfo, cancellationToken);

        if (diffs.Count == 0)
        {
            _logger.LogWarning("No file diffs found in the PR.");
            return new ReviewResult
            {
                Summary = "No file changes found to review.",
                Comments = []
            };
        }

        _logger.LogInformation("Found {Count} changed files", diffs.Count);

        // Step 3: Chunk diffs and review each batch
        var chunks = ChunkDiffs(diffs);
        _logger.LogInformation("Split into {Count} chunk(s) for review", chunks.Count);

        var allComments = new List<ReviewComment>();
        var summaries = new List<string>();

        for (int i = 0; i < chunks.Count; i++)
        {
            _logger.LogInformation("Reviewing chunk {Current}/{Total} ({FileCount} files)...",
                i + 1, chunks.Count, chunks[i].Count);

            var prompt = PromptBuilder.Build(title, description, chunks[i], problemStatement);
            var chunkResult = await _aiProvider.ReviewAsync(prompt, cancellationToken);

            allComments.AddRange(chunkResult.Comments);
            summaries.Add(chunkResult.Summary);
        }

        var review = new ReviewResult
        {
            Summary = chunks.Count == 1
                ? summaries[0]
                : string.Join("\n\n", summaries.Select((s, i) => $"**Part {i + 1}/{chunks.Count}:** {s}")),
            Comments = allComments
        };

        _logger.LogInformation("Review complete: {CommentCount} total comments", review.Comments.Count);

        // Step 4: Post comments or print to console
        if (dryRun)
        {
            PrintReview(review);
        }
        else
        {
            await _adoClient.PostReviewCommentsAsync(prInfo, review, cancellationToken);
            _logger.LogInformation("Review comments posted to PR #{PrId}", prInfo.PullRequestId);
        }

        return review;
    }

    private static List<List<FileDiff>> ChunkDiffs(IReadOnlyList<FileDiff> diffs)
    {
        var chunks = new List<List<FileDiff>>();
        var currentChunk = new List<FileDiff>();
        int currentSize = 0;

        foreach (var diff in diffs)
        {
            var diffSize = diff.DiffContent.Length + diff.FilePath.Length + 50; // overhead for formatting

            // If a single file exceeds the limit, it gets its own chunk
            if (currentChunk.Count > 0 && currentSize + diffSize > MaxCharsPerChunk)
            {
                chunks.Add(currentChunk);
                currentChunk = new List<FileDiff>();
                currentSize = 0;
            }

            currentChunk.Add(diff);
            currentSize += diffSize;
        }

        if (currentChunk.Count > 0)
            chunks.Add(currentChunk);

        return chunks;
    }

    private void PrintReview(ReviewResult review)
    {
        Console.WriteLine();
        Console.WriteLine("=== DRY RUN - Review Results ===");
        Console.WriteLine();
        Console.WriteLine($"Summary: {review.Summary}");
        Console.WriteLine();

        if (review.Comments.Count == 0)
        {
            Console.WriteLine("No issues found.");
            return;
        }

        Console.WriteLine($"Comments ({review.Comments.Count}):");
        Console.WriteLine(new string('-', 60));

        foreach (var comment in review.Comments)
        {
            var severityEmoji = comment.Severity switch
            {
                "critical" => "\ud83d\udd34",
                "warning" => "\ud83d\udfe1",
                _ => "\ud83d\udd35"
            };

            Console.WriteLine($"{severityEmoji} [{comment.Severity.ToUpperInvariant()}] {comment.FilePath}:{comment.LineNumber}");
            Console.WriteLine($"   {comment.Body}");
            Console.WriteLine();
        }
    }
}
