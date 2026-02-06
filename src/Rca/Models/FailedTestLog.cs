namespace Rca.Models;

/// <summary>
/// xUnitOTel JSON format
/// </summary>
public record FailedTestLog(
    string TraceId,
    string TestClass,
    string TestName,
    DateTime Timestamp,
    FailureInfo? Failure,
    List<LogEntry>? Logs
)
{
    public string ErrorMessage => Failure?.Messages is { Length: > 0 }
        ? string.Join("\n", Failure.Messages)
        : "Unknown error";

    public string? StackTrace => Failure?.StackTraces is { Length: > 0 }
        ? string.Join("\n---\n", Failure.StackTraces)
        : null;
}

public record FailureInfo(string[] Messages, string[]? StackTraces);

public record LogEntry(
    string Timestamp,
    string Level,
    string Category,
    string Message,
    string? Exception
);
