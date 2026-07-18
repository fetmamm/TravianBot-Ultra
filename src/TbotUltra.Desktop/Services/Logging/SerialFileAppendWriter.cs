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
            _requests.Writer.TryWrite(new WriteRequest(lines.ToArray(), null));
        }
    }

    public async Task FlushAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_requests.Writer.TryWrite(new WriteRequest(null, completion)))
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

    private sealed record WriteRequest(string[]? Lines, TaskCompletionSource? Completion);
}
