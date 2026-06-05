using AspNetCoreSharedServer;
using AspNetCoreSharedServer.Util;
using Microsoft.Win32.SafeHandles;
using NeoSmart.AsyncLock;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetCoreSharedServer;


public sealed class AsyncProcessLockOld : IDisposable

#if NETCOREAPP
    , IAsyncDisposable
#endif

{
    private const int LOCK_EX = 2;
    private const int LOCK_NB = 4;
    private const int LOCK_UN = 8;
    private const int O_CREAT = 0x40;
    private const int O_RDWR = 0x2;

    const int pollMilliseconds = 100;

    private readonly string _name;

    // =========================
    // WINDOWS
    // =========================
    private Mutex? _mutex;

    // =========================
    // UNIX
    // =========================
    private int _fd = -1;

    private bool _owned;

    private AsyncProcessLockOld(string name)
    {
        _name = NormalizeName(name);
        OldAscnyId = AsyncId;
        if (OSInfo.IsLinux || OSInfo.IsMac) Directory.CreateDirectory(Path.GetDirectoryName(_name));
    }

    // =========================================================
    // ASYNC REENTRANCY (Async flow)
    // =========================================================
    private static readonly AsyncLocal<long> asyncId = new AsyncLocal<long>();
    private static long AsyncId => asyncId.Value;
    private static long AsyncStackCounter = 0;
    const long UnlockId = default;
    long OldAsyncId = UnlockId;
    long OwnerId = UnlockId;
    private int reentrances = 0;


    // =========================================================
    // SYNC REENTRANCY (Thread-local)
    // =========================================================
    [ThreadStatic]
    private static Dictionary<string, int>? _syncHeld;

    // =========================================================
    // FACTORY
    // =========================================================
    public static async Task<AsyncProcessLockOld> AcquireAsync(
        string name,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        timeout ??= TimeSpan.FromSeconds(15);

        var instance = new AsyncProcessLockOld(name);
        instance.OldAsyncId = AsyncId;
        asyncId.Value = Interlocked.Increment(ref AsyncStackCounter);

        if (!await instance.AcquireAsyncInternal(timeout.Value, pollMilliseconds, ct))
            throw new TimeoutException();

        return instance;
    }

    public static AsyncProcessLockOld Acquire(
        string name,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        timeout ??= TimeSpan.FromSeconds(60);

        var instance = new AsyncProcessLockOld(name);

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

        if (OwnerId == UnlockId || OwnerId == OldAsyncId) OwnerId = AsyncId;
        else
        {
            // Another thread currently owns the lock
            return false;
        }


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
        int fd = -1;
        try
        {
            fd = open(_name, O_CREAT | O_RDWR, 0x1A4); // 0644

            if (fd == -1) return false;

            if (flock(fd, LOCK_EX | LOCK_NB) == 0)       
            {
                _fd = fd;
                return true;
            } else
            {
                var errno = Marshal.GetLastWin32Error();
            }

            return false;
        }
        finally
        {
            if (fd != -1 && _fd != fd) close(fd);
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

        try
        {
            int fd = open(_name, O_CREAT | O_RDWR, 0x1A4); // 0644

            if (fd == -1) return false;

            if (flock(fd, LOCK_EX | LOCK_NB) == 0)
            {
                _fd = fd;
                return true;
            } else
            {
                var errno = Marshal.GetLastWin32Error();
            }

            close(fd);
            return false;
        }
        finally
        {
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

        reentrances--;
        OwnerId = OldAsyncId;
        if (reentrances == 0) OwnerId = UnlockId;

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

        if (_fd != -1)
        {
            flock(_fd, LOCK_UN);
            close(_fd);

            _fd = -1;
        }
        
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
    [DllImport("libc", SetLastError = true)]
    private static extern int open(string pathname, int flags, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

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