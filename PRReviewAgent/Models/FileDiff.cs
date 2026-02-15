namespace PRReviewAgent.Models;

public class FileDiff
{
    public required string FilePath { get; set; }
    public required string DiffContent { get; set; }
    public ChangeType ChangeType { get; set; }
}

public enum ChangeType
{
    Add,
    Edit,
    Delete,
    Rename
}
