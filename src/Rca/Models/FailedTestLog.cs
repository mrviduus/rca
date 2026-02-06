namespace Rca.Models;

public record FailedTestLog(
    string TestName,
    string ClassName,
    string ErrorMessage,
    string? StackTrace,
    DateTime Timestamp
);
