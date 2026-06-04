using System;
using System.Threading;
using System.Threading.Tasks;

public sealed class WindowsPollingMutexAsyncLock : IAsyncDisposable
{
    private readonly Mutex _mutex;
    private bool _owned;

    public WindowsPollingMutexAsyncLock(string name)
    {
        _mutex = new Mutex(false, name);
    }

    public async Task<bool> AcquireAsync(
        TimeSpan timeout,
        int pollMs = 50,
        CancellationToken ct = default)
    {
        var start = DateTime.UtcNow;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (_mutex.WaitOne(0))
                {
                    _owned = true;
                    return true;
                }
            }
            catch (AbandonedMutexException)
            {
                // previous owner crashed, we now own it
                _owned = true;
                return true;
            }

            if (DateTime.UtcNow - start > timeout)
                return false;

            await Task.Delay(pollMs, ct);
        }
    }

    public Task ReleaseAsync()
    {
        if (_owned)
        {
            _mutex.ReleaseMutex();
            _owned = false;
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await ReleaseAsync();
        _mutex.Dispose();
    }
}