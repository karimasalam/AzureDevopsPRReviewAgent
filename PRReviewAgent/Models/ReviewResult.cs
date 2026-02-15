using System.Text.Json.Serialization;

namespace PRReviewAgent.Models;

public class ReviewResult
{
    [JsonPropertyName("summary")]
    public required string Summary { get; set; }

    [JsonPropertyName("comments")]
    public List<ReviewComment> Comments { get; set; } = [];
}
