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
    public string ErrorMessage => Failure?.Messages?.FirstOrDefault() ?? "Unknown error";
    public string? StackTrace => Failure?.StackTraces?.FirstOrDefault();
}

public record FailureInfo(string[] Messages, string[]? StackTraces);

public record LogEntry(
    string Timestamp,
    string Level,
    string Category,
    string Message,
    string? Exception
);
