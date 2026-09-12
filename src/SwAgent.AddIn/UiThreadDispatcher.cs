using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SwAgent.Core.Infrastructure;

namespace SwAgent.AddIn
{
    /// <summary>
    /// Marshals work onto the SOLIDWORKS UI thread.
    ///
    /// This is the real implementation of the rule that
    /// <see cref="ISwDispatcher"/> only describes, and it is the reason the
    /// threading model has to exist from day one rather than be retrofitted.
    ///
    /// How it works: SOLIDWORKS is a native application, so
    /// SynchronizationContext.Current is null on its thread - there is no
    /// managed context to capture. What SOLIDWORKS does have is a Win32 message
    /// pump, so we create a hidden WinForms control on that thread during
    /// ConnectToSW and post work to it. Control.BeginInvoke puts a message on
    /// that pump, and the SOLIDWORKS thread runs our callback when it next
    /// pumps messages.
    ///
    /// The consequence for callers: an agent loop running on a worker thread
    /// awaits HTTP off-thread (never blocking CAD), then routes each tool call
    /// through here so the COM work lands where COM requires it. The UI stays
    /// responsive throughout, which is verifiable by dragging the SOLIDWORKS
    /// window while the agent works.
    /// </summary>
    internal sealed class UiThreadDispatcher : ISwDispatcher, IDisposable
    {
        private readonly Control _marshaller;
        private readonly int _uiThreadId;
        private readonly ISwLog _log;
        private volatile bool _disposed;

        /// <summary>
        /// Must be constructed ON the SOLIDWORKS UI thread - that is, from
        /// inside ConnectToSW. Constructing it anywhere else captures the wrong
        /// thread, and every COM call afterwards runs in the wrong place.
        /// </summary>
        public UiThreadDispatcher(ISwLog log = null)
        {
            _log = log ?? NullSwLog.Instance;
            _uiThreadId = Thread.CurrentThread.ManagedThreadId;

            _marshaller = new Control();

            // Force handle creation now, on this thread. BeginInvoke throws if
            // the handle does not exist yet, and creating it lazily from a
            // worker would create it on the wrong thread.
            var _ = _marshaller.Handle;
        }

        public bool IsOnSwThread => Thread.CurrentThread.ManagedThreadId == _uiThreadId;

        public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (work == null) throw new ArgumentNullException(nameof(work));

            if (_disposed)
            {
                return Failed<T>(new SwSessionLostException(
                    "The SOLIDWORKS session has ended; the add-in is no longer connected."));
            }

            if (cancellationToken.IsCancellationRequested)
                return Cancelled<T>();

            // Already on the SOLIDWORKS thread: run inline. Posting to the pump
            // from the pump's own thread would deadlock a synchronous caller.
            if (IsOnSwThread)
            {
                try
                {
                    return Task.FromResult(work());
                }
                catch (Exception ex)
                {
                    return Failed<T>(ex);
                }
            }

            var tcs = new TaskCompletionSource<T>();

            try
            {
                _marshaller.BeginInvoke((Action)(() =>
                {
                    // Re-check: the session can end between posting and running.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled();
                        return;
                    }

                    try
                    {
                        tcs.TrySetResult(work());
                    }
                    catch (Exception ex)
                    {
                        // Complete the task with the exception rather than
                        // letting it escape onto the SOLIDWORKS thread, where
                        // it would be an unhandled exception in the host.
                        tcs.TrySetException(ex);
                    }
                }));
            }
            catch (InvalidOperationException ex)
            {
                // The handle is gone: SOLIDWORKS is shutting down or has
                // already torn the window down.
                _log.Error($"Dispatcher could not reach the SOLIDWORKS thread: {ex.Message}");
                return Failed<T>(new SwSessionLostException(
                    "The SOLIDWORKS UI thread is no longer reachable.", ex));
            }
            catch (Exception ex)
            {
                return Failed<T>(ex);
            }

            return tcs.Task;
        }

        public Task InvokeAsync(Action work, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            return InvokeAsync(() => { work(); return true; }, cancellationToken);
        }

        private static Task<T> Failed<T>(Exception ex)
        {
            var tcs = new TaskCompletionSource<T>();
            tcs.SetException(ex);
            return tcs.Task;
        }

        private static Task<T> Cancelled<T>()
        {
            var tcs = new TaskCompletionSource<T>();
            tcs.SetCanceled();
            return tcs.Task;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _marshaller.Dispose();
            }
            catch (Exception ex)
            {
                _log.Debug($"Dispatcher dispose: {ex.Message}");
            }
        }
    }
}
