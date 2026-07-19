using System.Threading.Channels;
using System.IO;

namespace TbotUltra.Desktop.Services.Logging;

internal sealed class SerialFileAppendWriter : IAsyncDisposable
{
    private readonly string _path;
    private readonly Channel<WriteRequest> _requests = Channel.CreateUnbounded<WriteRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _writerTask;

    public SerialFileAppendWriter(string path)
    {
        _path = path;
        _writerTask = Task.Run(ProcessAsync);
    }

    public void Append(IReadOnlyList<string> lines)
    {
        if (lines.Count > 0)
        {
            _requests.Writer.TryWrite(new WriteRequest(lines.ToArray(), null, null));
        }
    }

    public void AppendSessionLines(IReadOnlyList<string> logLines, IReadOnlyList<string> alarmLines)
    {
        if (logLines.Count > 0 || alarmLines.Count > 0)
        {
            _requests.Writer.TryWrite(new WriteRequest(logLines.ToArray(), alarmLines.ToArray(), null));
        }
    }

    public async Task FlushAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_requests.Writer.TryWrite(new WriteRequest(null, null, completion)))
        {
            await _writerTask.ConfigureAwait(false);
            return;
        }

        await completion.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAsync().ConfigureAwait(false);
        _requests.Writer.TryComplete();
        await _writerTask.ConfigureAwait(false);
    }

    private async Task ProcessAsync()
    {
        await foreach (var request in _requests.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (request.AlarmLines is { Length: > 0 })
                {
                    InsertAlarmLines(request.AlarmLines);
                }

                if (request.Lines is { Length: > 0 })
                {
                    File.AppendAllLines(_path, request.Lines);
                }

                request.Completion?.TrySetResult();
            }
            catch (Exception ex)
            {
                request.Completion?.TrySetException(ex);
                System.Diagnostics.Debug.WriteLine($"Could not append session logs: {ex}");
            }
        }
    }

    private void InsertAlarmLines(IReadOnlyList<string> alarmLines)
    {
        var content = File.ReadAllLines(_path).ToList();
        var logsHeaderIndex = content.FindIndex(line =>
            string.Equals(line, "=== LOGS ===", StringComparison.Ordinal));
        if (logsHeaderIndex < 0)
        {
            throw new InvalidDataException("Session log is missing the LOGS section marker.");
        }

        var insertIndex = logsHeaderIndex > 0 && string.IsNullOrEmpty(content[logsHeaderIndex - 1])
            ? logsHeaderIndex - 1
            : logsHeaderIndex;
        content.InsertRange(insertIndex, alarmLines);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllLines(temporaryPath, content);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record WriteRequest(
        string[]? Lines,
        string[]? AlarmLines,
        TaskCompletionSource? Completion);
}
