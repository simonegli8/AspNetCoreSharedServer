using AspNetCoreSharedServer;
using AspNetCoreSharedServer.Util;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetCoreSharedServer;


public sealed class AsyncProcessLock : IDisposable

#if NETCOREAPP
    , IAsyncDisposable
#endif

{
    private const int LOCK_EX = 2;
    private const int LOCK_NB = 4;
    private const int LOCK_UN = 8;
    const int pollMilliseconds = 100;

    private readonly string _name;

    // =========================
    // WINDOWS
    // =========================
    private Mutex? _mutex;

    // =========================
    // UNIX
    // =========================
    private FileStream? _stream;
    private SafeFileHandle? _handle;

    private bool _owned;

    private int Fd => (int)_handle!.DangerousGetHandle().ToInt64();

    private AsyncProcessLock(string name)
    {
        _name = NormalizeName(name);
    }

    // =========================================================
    // ASYNC REENTRANCY (Async flow)
    // =========================================================
    private static readonly AsyncLocal<Dictionary<string, int>> _asyncHeld
        = new();

    // =========================================================
    // SYNC REENTRANCY (Thread-local)
    // =========================================================
    [ThreadStatic]
    private static Dictionary<string, int>? _syncHeld;

    // =========================================================
    // FACTORY
    // =========================================================
    public static async Task<AsyncProcessLock> AcquireAsync(
        string name,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        timeout ??= TimeSpan.FromSeconds(15);

        var instance = new AsyncProcessLock(name);

        if (!await instance.AcquireAsyncInternal(timeout.Value, pollMilliseconds, ct))
            throw new TimeoutException();

        return instance;
    }

    public static AsyncProcessLock Acquire(
        string name,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        timeout ??= TimeSpan.FromSeconds(15);

        var instance = new AsyncProcessLock(name);

        if (!instance.AcquireSyncInternal(timeout.Value, pollMilliseconds, ct))
            throw new TimeoutException();

        return instance;
    }

    // =========================================================
    // ASYNC ACQUIRE
    // =========================================================
    private async Task<bool> AcquireAsyncInternal(
        TimeSpan timeout,
        int pollMs,
        CancellationToken ct)
    {
        var start = DateTime.UtcNow;

        if (_syncHeld != null) throw new NotSupportedException("Mixing of Sync and Async invocation of Lock is not supported.");

        var held = _asyncHeld.Value ??= new Dictionary<string, int>();

        if (held.TryGetValue(_name, out var depth))
        {
            held[_name] = depth + 1;
            return true;
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (DateTime.UtcNow - start > timeout)
                return false;

            if (await TryAcquireOnceAsync())
            {
                held[_name] = 1;
                return true;
            }

            await Task.Delay(pollMs, ct);
        }
    }

    // =========================================================
    // SYNC ACQUIRE
    // =========================================================
    private bool AcquireSyncInternal(
        TimeSpan timeout,
        int pollMs,
        CancellationToken ct)
    {
        var start = DateTime.UtcNow;

        if (_asyncHeld.Value != null) throw new NotSupportedException("Mixing of Sync and Async invocation of Lock is not supported.");

        _syncHeld ??= new Dictionary<string, int>();

        if (_syncHeld.TryGetValue(_name, out var depth))
        {
            _syncHeld[_name] = depth + 1;
            return true;
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (DateTime.UtcNow - start > timeout)
                return false;

            if (TryAcquireOnceSync())
            {
                _syncHeld[_name] = 1;
                return true;
            }

            Thread.Sleep(pollMs);
        }
    }

    // =========================================================
    // CORE ACQUIRE (ASYNC PATH)
    // =========================================================
    private async Task<bool> TryAcquireOnceAsync()
    {
        if (OSInfo.IsWindows)
        {
            _mutex ??= new Mutex(false, _name);

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

            return false;
        }

        FileStream? stream = null;

        try
        {
            stream = new FileStream(
                _name,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite);

            int fd = (int)stream.SafeFileHandle.DangerousGetHandle().ToInt64();

            if (flock(fd, LOCK_EX | LOCK_NB) == 0)
            {
                _stream = stream;
                _handle = stream.SafeFileHandle;

                stream = null;
                _owned = true;

                return true;
            }

            return false;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    // =========================================================
    // CORE ACQUIRE (SYNC PATH)
    // =========================================================
    private bool TryAcquireOnceSync()
    {
        if (OSInfo.IsWindows)
        {
            _mutex ??= new Mutex(false, _name);

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

            return false;
        }

        FileStream? stream = null;

        try
        {
            stream = new FileStream(
                _name,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite);

            int fd = (int)stream.SafeFileHandle.DangerousGetHandle().ToInt64();

            if (flock(fd, LOCK_EX | LOCK_NB) == 0)
            {
                _stream = stream;
                _handle = stream.SafeFileHandle;

                stream = null;
                _owned = true;

                return true;
            }

            return false;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    // =========================================================
    // RELEASE
    // =========================================================
    public void Release()
    {
        if (OSInfo.IsWindows)
        {
            if (_owned)
            {
                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
                _mutex = null;
                _owned = false;
            }

            return;
        }

        var asyncHeld = _asyncHeld.Value;
        if (asyncHeld != null && asyncHeld.TryGetValue(_name, out var dAsync))
        {
            if (dAsync > 1)
            {
                asyncHeld[_name] = dAsync - 1;
                return;
            }

            asyncHeld.Remove(_name);
        }

        var syncHeld = _syncHeld;
        if (syncHeld != null && syncHeld.TryGetValue(_name, out var dSync))
        {
            if (dSync > 1)
            {
                syncHeld[_name] = dSync - 1;
                return;
            }

            syncHeld.Remove(_name);
        }

        if (_handle != null) flock(Fd, LOCK_UN);

        _stream?.Dispose();
        _stream = null;
        _handle = null;

        return;
    }

    public async Task ReleaseAsync()
    {
        Release();
    }

#if NETCOREAPP
    public async ValueTask DisposeAsync()
    {
        Release();
    }
#endif

    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    // =========================================================
    // LIBC
    // =========================================================
    [DllImport("libc", SetLastError = true)]
    private static extern int flock(int fd, int operation);

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