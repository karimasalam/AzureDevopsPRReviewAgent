using System.Text.RegularExpressions;
using PRReviewAgent.Models;

namespace PRReviewAgent.Helpers;

public static partial class PullRequestUrlParser
{
    // Matches: https://dev.azure.com/{org}/{project}/_git/{repo}/pullrequest/{id}
    [GeneratedRegex(@"^https?://dev\.azure\.com/(?<org>[^/]+)/(?<project>[^/]+)/_git/(?<repo>[^/]+)/pullrequest/(?<id>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex NewStyleUrlRegex();

    // Matches: https://{org}.visualstudio.com/{project}/_git/{repo}/pullrequest/{id}
    [GeneratedRegex(@"^https?://(?<org>[^.]+)\.visualstudio\.com/(?<project>[^/]+)/_git/(?<repo>[^/]+)/pullrequest/(?<id>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex OldStyleUrlRegex();

    public static PullRequestInfo? TryParse(string url)
    {
        var match = NewStyleUrlRegex().Match(url);
        if (!match.Success)
            match = OldStyleUrlRegex().Match(url);

        if (!match.Success)
            return null;

        return new PullRequestInfo
        {
            Organization = match.Groups["org"].Value,
            Project = match.Groups["project"].Value,
            Repository = match.Groups["repo"].Value,
            PullRequestId = int.Parse(match.Groups["id"].Value)
        };
    }
}
