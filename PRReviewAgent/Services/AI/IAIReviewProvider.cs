using PRReviewAgent.Models;

namespace PRReviewAgent.Services.AI;

public interface IAIReviewProvider
{
    string Name { get; }
    Task<ReviewResult> ReviewAsync(string prompt, CancellationToken cancellationToken = default);
}
