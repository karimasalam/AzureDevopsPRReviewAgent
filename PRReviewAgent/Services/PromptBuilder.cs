using System.Text;
using PRReviewAgent.Models;

namespace PRReviewAgent.Services;

public static class PromptBuilder
{
    public static string Build(string prTitle, string prDescription, IReadOnlyList<FileDiff> diffs, string? problemStatement = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an expert code reviewer. Review the following pull request and provide feedback.");
        sb.AppendLine();
        sb.AppendLine("## Pull Request");
        sb.AppendLine($"**Title:** {prTitle}");
        if (!string.IsNullOrWhiteSpace(prDescription))
        {
            sb.AppendLine($"**Description:** {prDescription}");
        }

        if (!string.IsNullOrWhiteSpace(problemStatement))
        {
            sb.AppendLine();
            sb.AppendLine("## Problem Statement");
            sb.AppendLine(problemStatement);
        }

        sb.AppendLine();
        sb.AppendLine("## Changed Files");
        sb.AppendLine();

        foreach (var diff in diffs)
        {
            sb.AppendLine($"### {diff.FilePath} ({diff.ChangeType})");
            sb.AppendLine("```diff");
            sb.AppendLine(diff.DiffContent);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("## Instructions");
        sb.AppendLine("Review the code changes above. For each issue found, provide:");
        sb.AppendLine("- The file path");
        sb.AppendLine("- The line number in the new file where the issue is");
        sb.AppendLine("- A clear description of the issue and suggested fix");
        sb.AppendLine("- A severity level: \"critical\", \"warning\", or \"info\"");
        sb.AppendLine();
        sb.AppendLine("Focus on:");
        sb.AppendLine("- Bugs and logic errors");
        sb.AppendLine("- Security vulnerabilities");
        sb.AppendLine("- Performance issues");
        sb.AppendLine("- Code style and best practices");
        sb.AppendLine("- Missing error handling");
        sb.AppendLine();
        sb.AppendLine("Respond ONLY with valid JSON in this exact format:");
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"summary\": \"A brief overall summary of the PR review\",");
        sb.AppendLine("  \"comments\": [");
        sb.AppendLine("    {");
        sb.AppendLine("      \"filePath\": \"path/to/file.cs\",");
        sb.AppendLine("      \"lineNumber\": 42,");
        sb.AppendLine("      \"body\": \"Description of the issue and suggested fix\",");
        sb.AppendLine("      \"severity\": \"warning\"");
        sb.AppendLine("    }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine("If the code looks good and there are no issues, return an empty comments array with a positive summary.");

        return sb.ToString();
    }
}
