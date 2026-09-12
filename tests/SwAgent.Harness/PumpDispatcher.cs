using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using SwAgent.Core.Infrastructure;

namespace SwAgent.Harness
{
    /// <summary>
    /// A dispatcher that marshals work back to the thread that created it, by
    /// pumping a queue.
    ///
    /// This stands in for the add-in's UiThreadDispatcher, which posts to
    /// SOLIDWORKS' Win32 message pump. The harness has no message pump, so it
    /// runs one: the main thread calls <see cref="PumpUntil"/> while the agent
    /// loop runs on worker threads, and every COM call lands back here.
    ///
    /// This matters for the test's honesty. InlineSwDispatcher would throw the
    /// moment the agent awaited an HTTP call and resumed on a thread-pool
    /// thread - so using it here would prove nothing about the threading
    /// contract. This proves the real thing: network off-thread, COM on one
    /// thread, marshalled between them.
    /// </summary>
    internal sealed class PumpDispatcher : ISwDispatcher, IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly int _ownerThreadId;

        public PumpDispatcher()
        {
            _ownerThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public bool IsOnSwThread => Thread.CurrentThread.ManagedThreadId == _ownerThreadId;

        /// <summary>How many calls were marshalled across threads, for reporting.</summary>
        public int MarshalledCalls { get; private set; }

        public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (work == null) throw new ArgumentNullException(nameof(work));

            if (IsOnSwThread)
            {
                try { return Task.FromResult(work()); }
                catch (Exception ex)
                {
                    var failed = new TaskCompletionSource<T>();
                    failed.SetException(ex);
                    return failed.Task;
                }
            }

            var tcs = new TaskCompletionSource<T>();

            try
            {
                _queue.Add(() =>
                {
                    MarshalledCalls++;
                    try { tcs.TrySetResult(work()); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(new SwSessionLostException("The dispatcher queue is closed.", ex));
            }

            return tcs.Task;
        }

        public Task InvokeAsync(Action work, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            return InvokeAsync(() => { work(); return true; }, cancellationToken);
        }

        /// <summary>
        /// Run queued work on the calling thread until <paramref name="until"/>
        /// completes. Call this from the thread that constructed the dispatcher.
        /// </summary>
        public void PumpUntil(Task until)
        {
            if (!IsOnSwThread)
                throw new InvalidOperationException("PumpUntil must run on the thread that created the dispatcher.");

            while (!until.IsCompleted)
            {
                if (_queue.TryTake(out Action work, 25))
                    work();
            }

            // Drain anything queued in the gap between the last check and
            // completion, so no tool call is silently dropped.
            while (_queue.TryTake(out Action leftover, 0))
                leftover();
        }

        public void Dispose()
        {
            try { _queue.CompleteAdding(); } catch { }
            try { _queue.Dispose(); } catch { }
        }
    }
}
