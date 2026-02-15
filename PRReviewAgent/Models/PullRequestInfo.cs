namespace PRReviewAgent.Models;

public class PullRequestInfo
{
    public required string Organization { get; set; }
    public required string Project { get; set; }
    public required string Repository { get; set; }
    public required int PullRequestId { get; set; }

    public string BaseUrl => $"https://dev.azure.com/{Organization}";
}
