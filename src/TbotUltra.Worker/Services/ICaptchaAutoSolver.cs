namespace TbotUltra.Worker.Services;

public interface ICaptchaAutoSolver
{
    Task<bool> WarmupAsync(CancellationToken cancellationToken);

    Task<CaptchaSolverResult> TrySolveAsync(string imagePath, int timeoutSeconds, CancellationToken cancellationToken);
}
