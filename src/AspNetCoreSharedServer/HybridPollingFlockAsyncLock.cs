using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

public sealed class HybridPollingFlockAsyncLock : IAsyncDisposable
{
    private const int LOCK_EX = 2;
    private const int LOCK_NB = 4;
    private const int LOCK_UN = 8;

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(int fd, int operation);

    private readonly string _path;

    private FileStream? _stream;
    private SafeFileHandle? _handle;

    // 🔹 Async-flow-local reentrancy tracking
    private static readonly AsyncLocal<Dictionary<string, int>> _heldLocks = new();

    private HybridPollingFlockAsyncLock(string path)
    {
        _path = Path.GetFullPath(path);
    }

    public static HybridPollingFlockAsyncLock Create(string path)
        => new(path);

    private int Fd =>
        (int)_handle!.DangerousGetHandle().ToInt64();

    // -----------------------------------
    // ACQUIRE
    // -----------------------------------
    public async Task<bool> AcquireAsync(
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        timeout ??= TimeSpan.FromSeconds(10);

        var start = DateTime.UtcNow;

        var heldLocks = _heldLocks.Value;

        if (heldLocks == null)
        {
            heldLocks = new Dictionary<string, int>(
                StringComparer.Ordinal);

            _heldLocks.Value = heldLocks;
        }

        // ✅ Reentrant acquisition
        if (heldLocks.TryGetValue(_path, out int depth))
        {
            heldLocks[_path] = depth + 1;
            return true;
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (DateTime.UtcNow - start > timeout)
                return false;

            FileStream? stream = null;

            try
            {
                stream = new FileStream(
                    _path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite);

                int result = flock(Fd, LOCK_EX | LOCK_NB);

                if (result == 0)
                {
                    _stream = stream;
                    _handle = stream.SafeFileHandle;

                    heldLocks[_path] = 1;

                    return true;
                }
            }
            catch (IOException)
            {
                // ignored
            }
            finally
            {
                // dispose failed attempt
                if (_stream != stream)
                    stream?.Dispose();
            }

            await Task.Delay(100, ct);
        }
    }

    // -----------------------------------
    // RELEASE
    // -----------------------------------
    public Task ReleaseAsync()
    {
        var heldLocks = _heldLocks.Value;

        if (heldLocks == null)
            return Task.CompletedTask;

        if (!heldLocks.TryGetValue(_path, out int depth))
            return Task.CompletedTask;

        // recursive release
        if (depth > 1)
        {
            heldLocks[_path] = depth - 1;
            return Task.CompletedTask;
        }

        // fully release
        heldLocks.Remove(_path);

        try
        {
            if (_handle != null)
                flock(Fd, LOCK_UN);
        }
        finally
        {
            _stream?.Dispose();

            _stream = null;
            _handle = null;
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await ReleaseAsync();
    }
}