using System.Text;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using PRReviewAgent.Configuration;
using PRReviewAgent.Models;
using ChangeType = PRReviewAgent.Models.ChangeType;
using DiffChangeType = DiffPlex.DiffBuilder.Model.ChangeType;
using FileDiff = PRReviewAgent.Models.FileDiff;

namespace PRReviewAgent.Services.AzureDevOps;

public class AzureDevOpsClient : IAzureDevOpsClient
{
    private readonly AppSettings _settings;
    private readonly ILogger<AzureDevOpsClient> _logger;

    public AzureDevOpsClient(AppSettings settings, ILogger<AzureDevOpsClient> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    private GitHttpClient CreateGitClient(PullRequestInfo prInfo)
    {
        if (string.IsNullOrWhiteSpace(_settings.AzureDevOpsPat))
            throw new InvalidOperationException("Azure DevOps PAT is not configured. Set AZURE_DEVOPS_PAT or use --ado-pat.");

        var credentials = new VssBasicCredential(string.Empty, _settings.AzureDevOpsPat);
        var connection = new VssConnection(new Uri(prInfo.BaseUrl), credentials);
        connection.Settings.SendTimeout = TimeSpan.FromSeconds(_settings.ApiTimeoutSeconds);
        return connection.GetClient<GitHttpClient>();
    }

    public async Task<(string Title, string Description)> GetPullRequestInfoAsync(
        PullRequestInfo prInfo, CancellationToken cancellationToken = default)
    {
        using var gitClient = CreateGitClient(prInfo);

        _logger.LogInformation("Fetching PR #{PrId} from {Org}/{Project}/{Repo}...",
            prInfo.PullRequestId, prInfo.Organization, prInfo.Project, prInfo.Repository);

        var pr = await gitClient.GetPullRequestAsync(
            project: prInfo.Project,
            repositoryId: prInfo.Repository,
            pullRequestId: prInfo.PullRequestId,
            cancellationToken: cancellationToken);

        return (pr.Title, pr.Description ?? "");
    }

    public async Task<IReadOnlyList<FileDiff>> GetPullRequestDiffsAsync(
        PullRequestInfo prInfo, CancellationToken cancellationToken = default)
    {
        using var gitClient = CreateGitClient(prInfo);

        _logger.LogInformation("Fetching PR iterations...");

        var iterations = await gitClient.GetPullRequestIterationsAsync(
            project: prInfo.Project,
            repositoryId: prInfo.Repository,
            pullRequestId: prInfo.PullRequestId,
            cancellationToken: cancellationToken);

        if (iterations.Count == 0)
            return [];

        var lastIteration = iterations.Last();

        var changes = await gitClient.GetPullRequestIterationChangesAsync(
            project: prInfo.Project,
            repositoryId: prInfo.Repository,
            pullRequestId: prInfo.PullRequestId,
            iterationId: lastIteration.Id!.Value,
            cancellationToken: cancellationToken);

        // Fetch PR once for commit references
        var pullRequest = await gitClient.GetPullRequestAsync(
            project: prInfo.Project,
            repositoryId: prInfo.Repository,
            pullRequestId: prInfo.PullRequestId,
            cancellationToken: cancellationToken);

        var diffs = new List<FileDiff>();
        var differ = new Differ();
        var diffBuilder = new InlineDiffBuilder(differ);

        foreach (var change in changes.ChangeEntries)
        {
            var path = change.Item?.Path;
            if (string.IsNullOrEmpty(path)) continue;

            // Skip directories
            if (change.Item!.IsFolder) continue;

            var changeType = change.ChangeType switch
            {
                VersionControlChangeType.Add => ChangeType.Add,
                VersionControlChangeType.Delete => ChangeType.Delete,
                VersionControlChangeType.Rename => ChangeType.Rename,
                _ => ChangeType.Edit
            };

            try
            {
                string oldContent = "";
                string newContent = "";

                // Get the base version for edits/deletes
                if (changeType != ChangeType.Add)
                {
                    var baseStream = await gitClient.GetItemContentAsync(
                        repositoryId: prInfo.Repository,
                        path: path,
                        project: prInfo.Project,
                        versionDescriptor: new GitVersionDescriptor
                        {
                            VersionType = GitVersionType.Commit,
                            Version = pullRequest.LastMergeTargetCommit.CommitId
                        },
                        cancellationToken: cancellationToken);

                    using var reader = new StreamReader(baseStream);
                    oldContent = await reader.ReadToEndAsync(cancellationToken);
                }

                // Get the new version for adds/edits
                if (changeType != ChangeType.Delete)
                {
                    var newStream = await gitClient.GetItemContentAsync(
                        repositoryId: prInfo.Repository,
                        path: path,
                        project: prInfo.Project,
                        versionDescriptor: new GitVersionDescriptor
                        {
                            VersionType = GitVersionType.Commit,
                            Version = pullRequest.LastMergeSourceCommit.CommitId
                        },
                        cancellationToken: cancellationToken);

                    using var reader = new StreamReader(newStream);
                    newContent = await reader.ReadToEndAsync(cancellationToken);
                }

                var diffResult = diffBuilder.BuildDiffModel(oldContent, newContent);
                var diffText = FormatUnifiedDiff(path, diffResult);

                if (!string.IsNullOrWhiteSpace(diffText))
                {
                    diffs.Add(new FileDiff
                    {
                        FilePath = path,
                        DiffContent = diffText,
                        ChangeType = changeType
                    });
                }

                _logger.LogDebug("Processed diff for {Path} ({ChangeType})", path, changeType);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get diff for {Path}, skipping", path);
            }
        }

        _logger.LogInformation("Found {Count} file diffs", diffs.Count);
        return diffs;
    }

    public async Task PostReviewCommentsAsync(
        PullRequestInfo prInfo, ReviewResult review, CancellationToken cancellationToken = default)
    {
        using var gitClient = CreateGitClient(prInfo);

        // Post summary as a general comment thread
        _logger.LogInformation("Posting review summary comment...");

        var summaryThread = new GitPullRequestCommentThread
        {
            Comments = new List<Comment>
            {
                new()
                {
                    Content = $"## AI Code Review Summary\n\n{review.Summary}",
                    CommentType = CommentType.Text
                }
            },
            Status = CommentThreadStatus.Closed
        };

        await gitClient.CreateThreadAsync(
            commentThread: summaryThread,
            repositoryId: prInfo.Repository,
            pullRequestId: prInfo.PullRequestId,
            project: prInfo.Project,
            cancellationToken: cancellationToken);

        // Post inline comments
        foreach (var comment in review.Comments)
        {
            try
            {
                var severityEmoji = comment.Severity switch
                {
                    "critical" => "\ud83d\udd34",
                    "warning" => "\ud83d\udfe1",
                    _ => "\ud83d\udd35"
                };

                var thread = new GitPullRequestCommentThread
                {
                    Comments = new List<Comment>
                    {
                        new()
                        {
                            Content = $"{severityEmoji} **[{comment.Severity.ToUpperInvariant()}]** {comment.Body}",
                            CommentType = CommentType.Text
                        }
                    },
                    ThreadContext = new CommentThreadContext
                    {
                        FilePath = comment.FilePath.StartsWith('/') ? comment.FilePath : "/" + comment.FilePath,
                        RightFileStart = new CommentPosition { Line = comment.LineNumber, Offset = 1 },
                        RightFileEnd = new CommentPosition { Line = comment.LineNumber, Offset = 1 }
                    },
                    Status = comment.Severity == "critical"
                        ? CommentThreadStatus.Active
                        : CommentThreadStatus.Closed
                };

                await gitClient.CreateThreadAsync(
                    commentThread: thread,
                    repositoryId: prInfo.Repository,
                    pullRequestId: prInfo.PullRequestId,
                    project: prInfo.Project,
                    cancellationToken: cancellationToken);

                _logger.LogDebug("Posted comment on {File}:{Line}", comment.FilePath, comment.LineNumber);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to post comment on {File}:{Line}", comment.FilePath, comment.LineNumber);
            }
        }

        _logger.LogInformation("Posted {Count} inline comments", review.Comments.Count);
    }

    private static string FormatUnifiedDiff(string path, DiffPaneModel diffResult)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"--- a/{path}");
        sb.AppendLine($"+++ b/{path}");

        foreach (var line in diffResult.Lines)
        {
            var prefix = line.Type switch
            {
                DiffChangeType.Inserted => "+",
                DiffChangeType.Deleted => "-",
                DiffChangeType.Modified => "~",
                DiffChangeType.Imaginary => " ",
                _ => " "
            };
            sb.AppendLine($"{prefix}{line.Text}");
        }

        return sb.ToString();
    }
}
