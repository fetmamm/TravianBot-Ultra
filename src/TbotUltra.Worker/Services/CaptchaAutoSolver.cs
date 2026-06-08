using System.Diagnostics;
using System.Text.Json;

namespace TbotUltra.Worker.Services;

public sealed class CaptchaAutoSolver : ICaptchaAutoSolver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly SemaphoreSlim WarmupGate = new(1, 1);

    private readonly string _pythonPath;
    private readonly string? _pythonHomePath;
    private readonly string _scriptPath;
    private readonly string _workingDirectory;
    private readonly string _modelPath;
    private readonly string _classesPath;
    private bool _warmupCompleted;

    public CaptchaAutoSolver(ProjectContext projectContext)
    {
        _workingDirectory = Path.Combine(projectContext.RootPath, "Captcha_solver", "math_ai");
        _pythonHomePath = Path.Combine(_workingDirectory, ".venv", "python-home");
        var packagedPythonPath = Path.Combine(_pythonHomePath, "python.exe");
        var localVenvPythonPath = Path.Combine(_workingDirectory, ".venv", "Scripts", "python.exe");
        _pythonPath = File.Exists(packagedPythonPath) ? packagedPythonPath : localVenvPythonPath;
        _scriptPath = Path.Combine(_workingDirectory, "solve_runtime.py");
        _modelPath = Path.Combine(_workingDirectory, "model.keras");
        _classesPath = Path.Combine(_workingDirectory, "classes.txt");
    }

    public async Task<bool> WarmupAsync(CancellationToken cancellationToken)
    {
        if (_warmupCompleted || !CanWarmup())
        {
            return false;
        }

        await WarmupGate.WaitAsync(cancellationToken);
        try
        {
            if (_warmupCompleted)
            {
                return false;
            }

            var result = await ExecuteProcessAsync("--warmup", 120, cancellationToken);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Reason);
            }

            _warmupCompleted = true;
            return true;
        }
        finally
        {
            WarmupGate.Release();
        }
    }

    public async Task<CaptchaSolverResult> TrySolveAsync(string imagePath, int timeoutSeconds, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return new CaptchaSolverResult(false, "", "", 0d, "Image path is missing.");
        }

        if (!File.Exists(_pythonPath))
        {
            return new CaptchaSolverResult(false, "", "", 0d, $"Python runtime not found: '{_pythonPath}'.");
        }

        if (!File.Exists(_scriptPath))
        {
            return new CaptchaSolverResult(false, "", "", 0d, $"Solver script not found: '{_scriptPath}'.");
        }

        if (!File.Exists(imagePath))
        {
            return new CaptchaSolverResult(false, "", "", 0d, $"Captcha image not found: '{imagePath}'.");
        }

        return await ExecuteProcessAsync($"--image \"{imagePath}\"", timeoutSeconds, cancellationToken);
    }

    private bool CanWarmup()
    {
        return File.Exists(_pythonPath)
            && File.Exists(_scriptPath)
            && File.Exists(_modelPath)
            && File.Exists(_classesPath);
    }

    private async Task<CaptchaSolverResult> ExecuteProcessAsync(string arguments, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = _pythonPath,
            WorkingDirectory = _workingDirectory,
            Arguments = $"solve_runtime.py {arguments}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (_pythonHomePath is not null && File.Exists(Path.Combine(_pythonHomePath, "python.exe")))
        {
            process.StartInfo.Environment["PYTHONHOME"] = _pythonHomePath;
            process.StartInfo.Environment["PYTHONPATH"] = Path.Combine(_pythonHomePath, "Lib", "site-packages");
        }

        try
        {
            if (!process.Start())
            {
                return new CaptchaSolverResult(false, "", "", 0d, "Solver process could not be started.");
            }
        }
        catch (Exception ex)
        {
            return new CaptchaSolverResult(false, "", "", 0d, $"Solver process failed to start: {ex.Message}");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);
            await process.WaitForExitAsync(linkedCts.Token);

            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();
            return ParseResult(stdout, stderr, process.ExitCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await TryKillAndWaitAsync(process);
            return new CaptchaSolverResult(false, "", "", 0d, $"Solver timed out after {timeoutSeconds} seconds.");
        }
        catch (Exception ex)
        {
            await TryKillAndWaitAsync(process);
            return new CaptchaSolverResult(false, "", "", 0d, $"Solver execution failed: {ex.Message}");
        }
    }

    private static CaptchaSolverResult ParseResult(string stdout, string stderr, int exitCode)
    {
        var jsonLine = stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith("{", StringComparison.Ordinal) && line.EndsWith("}", StringComparison.Ordinal))
            ?? string.Empty;

        var rawOutput = string.IsNullOrWhiteSpace(stderr)
            ? stdout
            : string.IsNullOrWhiteSpace(stdout)
                ? stderr
                : stdout + Environment.NewLine + stderr;

        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            return new CaptchaSolverResult(false, "", "", 0d, $"Solver returned no JSON. {stderr}".Trim(), exitCode, rawOutput);
        }

        try
        {
            var payload = JsonSerializer.Deserialize<SolverPayload>(jsonLine, JsonOptions);
            if (payload is null)
            {
                return new CaptchaSolverResult(false, "", "", 0d, "Solver returned empty JSON payload.", exitCode, rawOutput);
            }

            return new CaptchaSolverResult(
                payload.Success,
                payload.Answer ?? string.Empty,
                payload.Expression ?? string.Empty,
                payload.Confidence,
                string.IsNullOrWhiteSpace(payload.Reason) ? $"Solver exit code: {exitCode}" : payload.Reason,
                exitCode,
                rawOutput);
        }
        catch (JsonException ex)
        {
            return new CaptchaSolverResult(false, "", "", 0d, $"Could not parse solver JSON: {ex.Message}", exitCode, rawOutput);
        }
    }

    private static async Task TryKillAndWaitAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best-effort cleanup. The Process instance is still disposed by the caller.
        }
    }

    private sealed class SolverPayload
    {
        public bool Success { get; set; }

        public string? Answer { get; set; }

        public string? Expression { get; set; }

        public double Confidence { get; set; }

        public string? Reason { get; set; }
    }
}
