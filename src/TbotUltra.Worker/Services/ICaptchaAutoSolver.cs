namespace TbotUltra.Worker.Services;

public interface ICaptchaAutoSolver
{
    Task<CaptchaSolverResult> TrySolveAsync(string imagePath, int timeoutSeconds, CancellationToken cancellationToken);
}
