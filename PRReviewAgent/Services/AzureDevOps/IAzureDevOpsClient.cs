using PRReviewAgent.Models;

namespace PRReviewAgent.Services.AzureDevOps;

public interface IAzureDevOpsClient
{
    Task<(string Title, string Description)> GetPullRequestInfoAsync(
        PullRequestInfo prInfo, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileDiff>> GetPullRequestDiffsAsync(
        PullRequestInfo prInfo, CancellationToken cancellationToken = default);

    Task PostReviewCommentsAsync(
        PullRequestInfo prInfo, ReviewResult review, CancellationToken cancellationToken = default);
}
