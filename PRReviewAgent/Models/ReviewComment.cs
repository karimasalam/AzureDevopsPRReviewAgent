using System.Text.Json.Serialization;

namespace PRReviewAgent.Models;

public class ReviewComment
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; set; }

    [JsonPropertyName("lineNumber")]
    public int LineNumber { get; set; }

    [JsonPropertyName("body")]
    public required string Body { get; set; }

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "info";
}
