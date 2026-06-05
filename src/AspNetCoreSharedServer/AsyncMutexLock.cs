using AspNetCoreSharedServer.Util;
using NeoSmart.AsyncLock;
using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace AspNetCoreSharedServer
{
    public class AsyncMutexLock
    {
        private SemaphoreSlim _reentrancy = new SemaphoreSlim(1, 1);
        private int _reentrances = 0;
        // We are using this SemaphoreSlim like a posix condition variable.
        // We only want to wake waiters, one or more of whom will try to obtain
        // a different lock to do their thing. So long as we can guarantee no
        // wakes are missed, the number of awakees is not important.
        // Ideally, this would be "friend" for access only from InnerLock, but
        // whatever.
        internal SemaphoreSlim _retry = new SemaphoreSlim(0, 1);
        private const long UnlockedId = 0x00; // "owning" task id when unlocked
        internal long _owningId = UnlockedId;
        internal int _owningThreadId = (int)UnlockedId;
        private static long AsyncStackCounter = 0;
        // An AsyncLocal<T> is not really the task-based equivalent to a ThreadLocal<T>, in that
        // it does not track the async flow (as the documentation describes) but rather it is
        // associated with a stack snapshot. Mutation of the AsyncLocal in an await call does
        // not change the value observed by the parent when the call returns, so if you want to
        // use it as a persistent async flow identifier, the value needs to be set at the outer-
        // most level and never touched internally.
        private static readonly AsyncLocal<long> _asyncId = new AsyncLocal<long>();
        private static long AsyncId => _asyncId.Value;

#if NETSTANDARD1_3
        private static int ThreadCounter = 0x00;
        private static ThreadLocal<int> LocalThreadId = new ThreadLocal<int>(() => ++ThreadCounter);
        private static int ThreadId => LocalThreadId.Value;
#else
        private static int ThreadId => Thread.CurrentThread.ManagedThreadId;
#endif

        public AsyncMutexLock()
        {
        }

#if !DEBUG
        readonly
#endif
        struct InnerLock : IDisposable
        {
            private readonly AsyncMutexLock _parent;
            private readonly long _oldId;
            private readonly int _oldThreadId;
#if DEBUG
            private bool _disposed;
#endif

            internal InnerLock(AsyncMutexLock parent, long oldId, int oldThreadId)
            {
                _parent = parent;
                _oldId = oldId;
                _oldThreadId = oldThreadId;
#if DEBUG
                _disposed = false;
#endif
            }

            internal async Task<IDisposable> ObtainLockAsync(CancellationToken cancellationToken = default)
            {
                while (true)
                {
                    await _parent._reentrancy.WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (InnerTryEnter(synchronous: false))
                    {
                        break;
                    }
                    // We need to wait for someone to leave the lock before trying again.
                    // We need to "atomically" obtain _retry and release _reentrancy, but there
                    // is no equivalent to a condition variable. Instead, we call *but don't await*
                    // _retry.WaitAsync(), then release the reentrancy lock, *then* await the saved task.
                    var waitTask = _parent._retry.WaitAsync(cancellationToken).ConfigureAwait(false);
                    _parent._reentrancy.Release();
                    await waitTask;
                }

                if (_parent._reentrances == 1) // Poll for mutex
                {
                    _parent._reentrancy.Release();

                    while (true)
                    {
                        await _parent._reentrancy.WaitAsync(cancellationToken).ConfigureAwait(false);
                        if (TryMutexAcquireOnce())
                        {
                            break;
                        }
                        var waitTask = Task.Delay(pollMilliseconds, cancellationToken).ConfigureAwait(false);
                        _parent._reentrancy.Release();
                        await waitTask;
                    }
                }

                _parent._owningThreadId = ThreadId;
                _parent._reentrancy.Release();
                return this;
            }

            internal async Task<IDisposable?> TryObtainLockAsync(TimeSpan timeout)
            {
                // In case of zero-timeout, don't even wait for protective lock contention
                if (timeout == TimeSpan.Zero)
                {
                    if (!_parent._reentrancy.Wait(timeout)) return null;
                    if (InnerTryEnter(synchronous: false))
                    {
                        // Reset the owning thread id after all await calls have finished, otherwise we
                        // could be resumed on a different thread and set an incorrect value.
                        _parent._owningThreadId = ThreadId;
                        if (_parent._reentrances != 1 || TryMutexAcquireOnce())
                        {
                            _parent._reentrancy.Release();
                            return this;
                        }
                    }
                    _parent._reentrancy.Release();
                    return null;
                }

                var now = DateTimeOffset.UtcNow;
                var last = now;
                var remainder = timeout;

                // We need to wait for someone to leave the lock before trying again.
                while (remainder > TimeSpan.Zero)
                {
                    if (!await _parent._reentrancy.WaitAsync(remainder).ConfigureAwait(false)) return null;
                    if (InnerTryEnter(synchronous: false))
                    {
                        if (_parent._reentrances == 1) // Poll for mutex
                        {
                            _parent._reentrancy.Release();

                            while (remainder > TimeSpan.Zero)
                            {
                                if (!await _parent._reentrancy.WaitAsync(remainder).ConfigureAwait(false)) return null;
                                if (TryMutexAcquireOnce())
                                {
                                    break;
                                }

                                _parent._reentrancy.Release();

                                now = DateTimeOffset.UtcNow;
                                remainder -= now - last;
                                last = now;
                                var poll = Math.Min(pollMilliseconds, remainder.Milliseconds);
                                if (poll > 0) Thread.Sleep(poll);

                                now = DateTimeOffset.UtcNow;
                                remainder -= now - last;
                                last = now;
                            }
                        }

                        if (remainder > TimeSpan.Zero)
                        {
                            // Reset the owning thread id after all await calls have finished, otherwise we
                            // could be resumed on a different thread and set an incorrect value.
                            _parent._owningThreadId = ThreadId;
                            _parent._reentrancy.Release();
                            return this;
                        }
                        else
                        {
                            _parent._reentrancy.Release();
                            return null;
                        }
                    }
                    // BUG? _parent._reentrancy.Release();

                    now = DateTimeOffset.UtcNow;
                    remainder -= now - last;
                    last = now;
                    if (remainder <= TimeSpan.Zero)
                    {
                        _parent._reentrancy.Release();
                        return null;
                    }

                    var waitTask = _parent._retry.WaitAsync(remainder).ConfigureAwait(false);
                    _parent._reentrancy.Release();
                    if (!await waitTask)
                    {
                        return null;
                    }

                    now = DateTimeOffset.UtcNow;
                    remainder -= now - last;
                    last = now;
                }

                return null;
            }

            internal async Task<IDisposable?> TryObtainLockAsync(CancellationToken cancellationToken = default)
            {
                try
                {
                    while (true)
                    {
                        await _parent._reentrancy.WaitAsync(cancellationToken).ConfigureAwait(false);
                        if (InnerTryEnter(synchronous: false))
                        {
                            break;
                        }
                        // We need to wait for someone to leave the lock before trying again.
                        var waitTask = _parent._retry.WaitAsync(cancellationToken).ConfigureAwait(false);
                        _parent._reentrancy.Release();
                        await waitTask;
                    }
                }
                catch (OperationCanceledException)
                {
                    return null;
                }

                if (_parent._reentrances == 1) // Poll for mutex
                {
                    _parent._reentrancy.Release();

                    try
                    {
                        while (true)
                        {
                            await _parent._reentrancy.WaitAsync(cancellationToken).ConfigureAwait(false);
                            if (TryMutexAcquireOnce()) break;
                            
                            var waitTask = Task.Delay(pollMilliseconds, cancellationToken).ConfigureAwait(false);
                            _parent._reentrancy.Release();
                            await waitTask;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return null;
                    }

                    _parent._owningThreadId = ThreadId;
                    _parent._reentrancy.Release();
                }
                return this;
            }

            internal IDisposable ObtainLock(CancellationToken cancellationToken = default)
            {
                while (true)
                {
                    _parent._reentrancy.Wait(cancellationToken);
                    if (InnerTryEnter(synchronous: true))
                    {
                        break;
                    }
                    // We need to wait for someone to leave the lock before trying again.
                    var waitTask = _parent._retry.WaitAsync(cancellationToken);
                    _parent._reentrancy.Release();
                    // This should be safe since the task we are awaiting doesn't need to make progress
                    // itself to complete - it will be completed by another thread altogether. cf SemaphoreSlim internals.
                    waitTask.GetAwaiter().GetResult();
                }

                if (_parent._reentrances == 1) // Poll for mutex
                {
                    _parent._reentrancy.Release();

                    while (true)
                    {
                        _parent._reentrancy.Wait(cancellationToken);
                        if (TryMutexAcquireOnce())
                        {
                            break;
                        }
                        var waitTask = Task.Delay(pollMilliseconds, cancellationToken);
                        _parent._reentrancy.Release();
                        // This should be safe since the task we are awaiting doesn't need to make progress
                        // itself to complete - it will be completed by another thread altogether. cf SemaphoreSlim internals.
                        waitTask.GetAwaiter().GetResult();
                    }
                }

                _parent._reentrancy.Release();
                return this;
            }

            internal IDisposable? TryObtainLock(TimeSpan timeout)
            {
                // In case of zero-timeout, don't even wait for protective lock contention
                if (timeout == TimeSpan.Zero)
                {
                    if (!_parent._reentrancy.Wait(timeout)) return null;
                    if (InnerTryEnter(synchronous: true))
                    {
                        if (_parent._reentrances != 1 || TryMutexAcquireOnce())
                        {
                            _parent._reentrancy.Release();
                            return this;
                        }
                    }
                    _parent._reentrancy.Release();
                    return null;
                }

                var now = DateTimeOffset.UtcNow;
                var last = now;
                var remainder = timeout;

                // We need to wait for someone to leave the lock before trying again.
                while (remainder > TimeSpan.Zero)
                {
                    if (!_parent._reentrancy.Wait(remainder)) return null;
                    if (InnerTryEnter(synchronous: true))
                    {
                        if (_parent._reentrances == 1) // Poll for mutex
                        {
                            _parent._reentrancy.Release();

                            while (remainder > TimeSpan.Zero)
                            {
                                if (!_parent._reentrancy.Wait(remainder)) return null;
                                if (TryMutexAcquireOnce())
                                {
                                    break;
                                }

                                _parent._reentrancy.Release();

                                now = DateTimeOffset.UtcNow;
                                remainder -= now - last;
                                last = now;
                                var poll = Math.Min(pollMilliseconds, remainder.Milliseconds);
                                if (poll > 0) Thread.Sleep(poll);

                                now = DateTimeOffset.UtcNow;
                                remainder -= now - last;
                                last = now;
                            }

                            if (remainder <= TimeSpan.Zero) return null;
                        }
                        
                        _parent._reentrancy.Release();
                        return this;
                    }

                    now = DateTimeOffset.UtcNow;
                    remainder -= now - last;
                    last = now;

                    var waitTask = _parent._retry.WaitAsync(remainder);
                    _parent._reentrancy.Release();
                    if (!waitTask.GetAwaiter().GetResult())
                    {
                        return null;
                    }

                    now = DateTimeOffset.UtcNow;
                    remainder -= now - last;
                    last = now;
                }

                return null;
            }

            // Mutex code
            Mutex mutex;
            bool owned = false;
            string name;
            int FlockFile = -1;

            private const int LOCK_EX = 2;
            private const int LOCK_NB = 4;
            private const int LOCK_UN = 8;
            private const int O_CREAT = 0x40;
            private const int O_RDWR = 0x2;

            const int pollMilliseconds = 100;
            [DllImport("libc", SetLastError = true)]
            private static extern int flock(int fd, int operation);
            [DllImport("libc", SetLastError = true)]
            private static extern int open(string pathname, int flags, uint mode);

            [DllImport("libc", SetLastError = true)]
            private static extern int close(int fd);

            public static bool IsWindows => RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);

            private bool TryMutexAcquireOnce()
            {
                if (IsWindows)
                {
                    mutex ??= new Mutex(false, name);

                    try
                    {
                        if (mutex.WaitOne(0))
                        {
                            owned = true;
                            return true;
                        }
                    }
                    catch (AbandonedMutexException)
                    {
                        owned = true;
                        return true;
                    }

                    return false;
                }
                else
                {
                    int file = -1;
                    try
                    {
                        file = open(name, O_CREAT | O_RDWR, 0x1A4); // 0644

                        if (file == -1) return false;

                        if (flock(file, LOCK_EX | LOCK_NB) == 0)
                        {
                            this.FlockFile = file;
                            return true;
                        }
                        else
                        {
                            var errno = Marshal.GetLastWin32Error();
                        }

                        return false;
                    }
                    finally
                    {
                        if (file != -1 && this.FlockFile != file) close(file);
                    }
                }
            }

            public void MutexRelease()
            {
                if (IsWindows)
                {
                    if (_owned)
                    {
                        _mutex?.ReleaseMutex();
                        _mutex?.Dispose();
                        _mutex = null;
                        _owned = false;
                    }
                }
                else
                {
                    if (file != -1)
                    {
                        flock(file, LOCK_UN);
                        close(file);

                        file = -1;
                    }
                }
            }

            private bool InnerTryEnter(bool synchronous)
            {
                if (synchronous)
                {
                    if (_parent._owningThreadId == UnlockedId)
                    {
                        _parent._owningThreadId = ThreadId;
                    }
                    else if (_parent._owningThreadId != ThreadId)
                    {
                        // Another thread currently owns the lock
                        return false;
                    }
                    _parent._owningId = AsyncLock.AsyncId;
                }
                else
                {
                    if (_parent._owningId == UnlockedId)
                    {
                        _parent._owningId = AsyncLock.AsyncId;
                    }
                    else if (_parent._owningId != _oldId)
                    {
                        // Another thread currently owns the lock
                        return false;
                    }
                    else
                    {
                        // Nested re-entrance
                        _parent._owningId = AsyncId;
                    }
                }

                // We can go in
                _parent._reentrances += 1;
                return true;
            }

            public void Dispose()
            {
#if DEBUG
                Debug.Assert(!_disposed);
                _disposed = true;
#endif
                var @this = this;
                var oldId = this._oldId;
                var oldThreadId = this._oldThreadId;
                @this._parent._reentrancy.Wait();
                try
                {
                    @this._parent._reentrances -= 1;
                    @this._parent._owningId = oldId;
                    @this._parent._owningThreadId = oldThreadId;
                    if (@this._parent._reentrances == 0)
                    {
                        // The owning thread is always the same so long as we
                        // are in a nested stack call. We reset the owning id
                        // only when the lock is fully unlocked.
                        @this._parent._owningId = UnlockedId;
                        @this._parent._owningThreadId = (int)UnlockedId;
                        
                        MutexRelease();
                    }
                    // We can't place this within the _reentrances == 0 block above because we might
                    // still need to notify a parallel reentrant task to wake. I think.
                    // This should not be a race condition since we only wait on _retry with _reentrancy locked,
                    // then release _reentrancy so the Dispose() call can obtain it to signal _retry in a big hack.
                    if (@this._parent._retry.CurrentCount == 0)
                    {
                        @this._parent._retry.Release();
                    }
                }
                finally
                {
                    @this._parent._reentrancy.Release();
                }
            }
        }

        // Make sure InnerLock.LockAsync() does not use await, because an async function triggers a snapshot of
        // the AsyncLocal value.
        public Task<IDisposable> LockAsync(CancellationToken cancellationToken = default)
        {
            var @lock = new InnerLock(this, _asyncId.Value, ThreadId);
            _asyncId.Value = Interlocked.Increment(ref AsyncMutexLock.AsyncStackCounter);
            return @lock.ObtainLockAsync(cancellationToken);
        }

        // Make sure InnerLock.LockAsync() does not use await, because an async function triggers a snapshot of
        // the AsyncLocal value.
        public Task<bool> TryLockAsync(Action callback, TimeSpan timeout)
        {
            var @lock = new InnerLock(this, _asyncId.Value, ThreadId);
            _asyncId.Value = Interlocked.Increment(ref AsyncMutexLock.AsyncStackCounter);

            return @lock.TryObtainLockAsync(timeout)
                .ContinueWith(state =>
                {
                    if (state.Exception is AggregateException ex)
                    {
                        ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
                    }
                    var disposableLock = state.Result;
                    if (disposableLock is null)
                    {
                        return false;
                    }

                    try
                    {
                        callback();
                    }
                    finally
                    {
                        disposableLock.Dispose();
                    }
                    return true;
                });
        }

        // Make sure InnerLock.LockAsync() does not use await, because an async function triggers a snapshot of
        // the AsyncLocal value.
        public Task<bool> TryLockAsync(Func<Task> callback, TimeSpan timeout)
        {
            var @lock = new InnerLock(this, _asyncId.Value, ThreadId);
            _asyncId.Value = Interlocked.Increment(ref AsyncMutexLock.AsyncStackCounter);

            return @lock.TryObtainLockAsync(timeout)
                .ContinueWith(state =>
                {
                    if (state.Exception is AggregateException ex)
                    {
                        ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
                    }
                    var disposableLock = state.Result;
                    if (disposableLock is null)
                    {
                        return Task.FromResult(false);
                    }

                    return callback()
                        .ContinueWith(result =>
                        {
                            disposableLock.Dispose();

                            if (result.Exception is AggregateException ex)
                            {
                                ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
                            }

                            return true;
                        }, TaskScheduler.Default);
                }, TaskScheduler.Default).Unwrap();
        }

        // Make sure InnerLock.TryLockAsync() does not use await, because an async function triggers a snapshot of
        // the AsyncLocal value.
        public Task<bool> TryLockAsync(Action callback, CancellationToken cancellationToken)
        {
            var @lock = new InnerLock(this, _asyncId.Value, ThreadId);
            _asyncId.Value = Interlocked.Increment(ref AsyncMutexLock.AsyncStackCounter);

            return @lock.TryObtainLockAsync(cancellationToken)
                .ContinueWith(state =>
                {
                    if (state.Exception is AggregateException ex)
                    {
                        ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
                    }
                    var disposableLock = state.Result;
                    if (disposableLock is null)
                    {
                        return false;
                    }

                    try
                    {
                        callback();
                    }
                    finally
                    {
                        disposableLock.Dispose();
                    }
                    return true;
                }, TaskScheduler.Default);
        }

        // Make sure InnerLock.LockAsync() does not use await, because an async function triggers a snapshot of
        // the AsyncLocal value.
        public Task<bool> TryLockAsync(Func<Task> callback, CancellationToken cancellationToken)
        {
            var @lock = new InnerLock(this, _asyncId.Value, ThreadId);
            _asyncId.Value = Interlocked.Increment(ref AsyncMutexLock.AsyncStackCounter);

            return @lock.TryObtainLockAsync(cancellationToken)
                .ContinueWith(state =>
                {
                    if (state.Exception is AggregateException ex)
                    {
                        ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
                    }
                    var disposableLock = state.Result;
                    if (disposableLock is null)
                    {
                        return Task.FromResult(false);
                    }

                    return callback()
                        .ContinueWith(result =>
                        {
                            disposableLock.Dispose();

                            if (result.Exception is AggregateException ex)
                            {
                                ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
                            }

                            return true;
                        }, TaskScheduler.Default);
                }, TaskScheduler.Default).Unwrap();
        }

        public IDisposable Lock(CancellationToken cancellationToken = default)
        {
            var @lock = new InnerLock(this, _asyncId.Value, ThreadId);
            // Increment the async stack counter to prevent a child task from getting
            // the lock at the same time as a child thread.
            _asyncId.Value = Interlocked.Increment(ref AsyncMutexLock.AsyncStackCounter);
            return @lock.ObtainLock(cancellationToken);
        }

        public bool TryLock(Action callback, TimeSpan timeout)
        {
            var @lock = new InnerLock(this, _asyncId.Value, ThreadId);
            // Increment the async stack counter to prevent a child task from getting
            // the lock at the same time as a child thread.
            _asyncId.Value = Interlocked.Increment(ref AsyncMutexLock.AsyncStackCounter);
            var lockDisposable = @lock.TryObtainLock(timeout);
            if (lockDisposable is null)
            {
                return false;
            }

            // Execute the callback then release the lock
            try
            {
                callback();
            }
            finally
            {
                lockDisposable.Dispose();
            }
            return true;
        }
    }
}
