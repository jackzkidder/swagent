using System;
using System.Threading;
using System.Threading.Tasks;

namespace SwAgent.Core.Infrastructure
{
    /// <summary>
    /// Marshals work onto the thread that owns the SOLIDWORKS COM pointer.
    ///
    /// The rule this type exists to enforce, in both directions:
    ///   - COM calls MUST run on the SOLIDWORKS thread.
    ///   - Network calls MUST NOT, or the whole CAD UI freezes mid-conversation.
    ///
    /// So the agent loop awaits HTTP off-thread on a worker, then routes every
    /// tool execution back through here. Nothing in this repo may touch an
    /// ISldWorks pointer outside an <see cref="InvokeAsync{T}"/> callback.
    /// </summary>
    public interface ISwDispatcher
    {
        /// <summary>True when the caller is already on the SOLIDWORKS thread.</summary>
        bool IsOnSwThread { get; }

        /// <summary>
        /// Run <paramref name="work"/> on the SOLIDWORKS thread and return its
        /// result. Exceptions thrown by the callback surface on the awaiting
        /// thread rather than escaping into the SOLIDWORKS message loop.
        /// </summary>
        Task<T> InvokeAsync<T>(Func<T> work, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>Run <paramref name="work"/> on the SOLIDWORKS thread.</summary>
        Task InvokeAsync(Action work, CancellationToken cancellationToken = default(CancellationToken));
    }

    /// <summary>
    /// Runs everything inline on the calling thread.
    ///
    /// This is for the headless test harness, which creates its own SOLIDWORKS
    /// session and drives it from a single thread it already owns. Never use it
    /// inside the add-in: there the calling thread is a worker, and running COM
    /// on it is exactly the bug <see cref="ISwDispatcher"/> exists to prevent.
    /// </summary>
    public sealed class InlineSwDispatcher : ISwDispatcher
    {
        private readonly int _ownerThreadId;

        public InlineSwDispatcher()
        {
            _ownerThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public bool IsOnSwThread => Thread.CurrentThread.ManagedThreadId == _ownerThreadId;

        public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsOnSwThread)
            {
                throw new InvalidOperationException(
                    "InlineSwDispatcher was called from a thread other than the one that created it. " +
                    "The headless harness must drive SOLIDWORKS from a single thread.");
            }

            try
            {
                return Task.FromResult(work());
            }
            catch (Exception ex)
            {
                var tcs = new TaskCompletionSource<T>();
                tcs.SetException(ex);
                return tcs.Task;
            }
        }

        public Task InvokeAsync(Action work, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            return InvokeAsync<bool>(() => { work(); return true; }, cancellationToken);
        }
    }
}
