namespace Stockline.Application.Orders.Import;

public enum IssueSeverity
{
    Error,
    Warning,
}

public sealed record ImportIssue(
    IssueSeverity Severity,
    string Code,
    string Message,
    int? Row = null,
    string? Field = null,
    IReadOnlyList<string>? Suggestions = null);
