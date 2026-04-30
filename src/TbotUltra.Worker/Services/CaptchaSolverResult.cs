namespace TbotUltra.Worker.Services;

public sealed record CaptchaSolverResult(
    bool Success,
    string Answer,
    string Expression,
    double Confidence,
    string Reason,
    int ExitCode = 0,
    string RawOutput = "");
