using AspNetCoreSharedServer;
using AspNetCoreSharedServer.Util;
using Microsoft.Win32.SafeHandles;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetCoreSharedServer;

#if NETCOREAPP
public sealed class AsyncProcessLock : IAsyncDisposable, IDisposable
#else
public sealed class AsyncProcessLock : IDisposable
#endif
{
    private const int LOCK_EX = 2;
    private const int LOCK_NB = 4;
    private const int LOCK_UN = 8;

    private readonly string _name;

    // Windows
    private Mutex? _mutex;

    // Unix
    private FileStream? _stream;
    private SafeFileHandle? _handle;

    // 🔹 Async-flow-local reentrancy tracking
    private static readonly AsyncLocal<Dictionary<string, int>> _heldLocks = new();

    private bool _owned;

    private AsyncProcessLock(string name)
    {
        _name = NormalizeName(name);
    }
    private int Fd => (int)_handle!.DangerousGetHandle().ToInt64();

    // -----------------------------
    // FACTORY
    // -----------------------------
    public static async Task<AsyncProcessLock> AcquireAsync(
        string name,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        const int pollMilliseconds = 100;

        timeout ??= TimeSpan.FromSeconds(15);

        var instance = new AsyncProcessLock(name);

        bool ok = await instance.AcquireInternalAsync(timeout ?? TimeSpan.FromSeconds(10), pollMilliseconds, ct);

        if (!ok)
            throw new TimeoutException("Failed to acquire process lock.");

        return instance;
    }

    // -----------------------------
    // PLATFORM DISPATCH
    // -----------------------------
    private async Task<bool> AcquireInternalAsync(
        TimeSpan timeout,
        int pollMs,
        CancellationToken ct)
    {
        if (OSInfo.IsWindows)
            return await AcquireWindowsAsync(timeout, pollMs, ct);

        return await AcquireUnixAsync(timeout, pollMs, ct);
    }

    // =============================
    // WINDOWS IMPLEMENTATION
    // =============================
    private async Task<bool> AcquireWindowsAsync(
        TimeSpan timeout,
        int pollMs,
        CancellationToken ct)
    {
        var start = DateTime.UtcNow;

        _mutex = new Mutex(false, _name);

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
                _owned = true;
                return true;
            }

            if (DateTime.UtcNow - start > timeout)
                return false;

            await Task.Delay(pollMs, ct);
        }
    }

    // =============================
    // UNIX IMPLEMENTATION
    // =============================
    [DllImport("libc", SetLastError = true)]
    private static extern int flock(int fd, int operation);

    private async Task<bool> AcquireUnixAsync(
        TimeSpan timeout,
        int pollMilliseconds,
        CancellationToken ct)
    {
        var start = DateTime.UtcNow;

        var heldLocks = _heldLocks.Value;

        if (heldLocks == null)
        {
            heldLocks = new Dictionary<string, int>(
                StringComparer.Ordinal);

            _heldLocks.Value = heldLocks;
        }

        // ✅ Reentrant acquisition
        if (heldLocks.TryGetValue(_name, out int depth))
        {
            heldLocks[_name] = depth + 1;
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
                    _name,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite);

                int result = flock(Fd, LOCK_EX | LOCK_NB);

                if (result == 0)
                {
                    _stream = stream;
                    _handle = stream.SafeFileHandle;

                    heldLocks[_name] = 1;

                    stream = null; // transfer ownership

                    return true;
                }
            }
            finally
            {
                stream?.Dispose();
            }

            await Task.Delay(pollMilliseconds, ct);
        }
    }

    // -----------------------------
    // RELEASE
    // -----------------------------

    public void Release()
    {
        if (OSInfo.IsWindows)
        {
            if (!_owned)
                return;

            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            _mutex = null;

            _owned = false;
        }
        else
        {
            var heldLocks = _heldLocks.Value;

            if (heldLocks == null)
                return;

            if (!heldLocks.TryGetValue(_name, out int depth))
                return;

            // recursive release
            if (depth > 1)
            {
                heldLocks[_name] = depth - 1;
                return;
            }

            // fully release
            heldLocks.Remove(_name);

            if (_handle != null) flock(Fd, LOCK_UN);

            _stream?.Dispose();
            _stream = null;
            _handle = null;
        }

        return;
    }

    public void Dispose() => Release();
#if NETCOREAPP
    public async ValueTask DisposeAsync() => Release();
#endif
    // -----------------------------
    // HELPERS
    // -----------------------------
    private static string NormalizeName(string name)
    {
        if (OSInfo.IsWindows)
            return $"Global\\{name.Replace('/', '_')}";
        if (OSInfo.IsLinux && Unix.IsRoot) return $"/run/aspnet-server/{name}.lock";
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var lockpath = Path.Combine(appData, "aspnet-server");
            var lockfile = Path.Combine(lockpath, $"{name}.lock");
            Directory.CreateDirectory(lockpath);
            return lockfile;
        }
    }
}