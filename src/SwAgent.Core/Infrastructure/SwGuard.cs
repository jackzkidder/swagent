using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SwAgent.Core.Tools;

namespace SwAgent.Core.Infrastructure
{
    /// <summary>
    /// The exception boundary. Nothing this add-in runs may throw into the
    /// SOLIDWORKS message loop.
    ///
    /// Our code runs inside the user's CAD session. An unhandled exception can
    /// take down SOLIDWORKS and destroy hours of unsaved work, and a customer
    /// who loses a day's modelling to us is gone along with their whole team.
    /// So every tool handler and every COM call is wrapped: a failure fails the
    /// operation and is reported cleanly, and the session stays up.
    ///
    /// "No crash across the full test suite" is a release gate that outranks
    /// every feature, and this type is how it is met.
    /// </summary>
    public static class SwGuard
    {
        // COM HRESULTs that mean the SOLIDWORKS side of the pointer is gone.
        // These are not retryable: there is nothing left to talk to.
        private const int RPC_E_DISCONNECTED = unchecked((int)0x80010108);
        private const int RPC_S_SERVER_UNAVAILABLE = unchecked((int)0x800706BA);
        private const int RPC_E_SERVERFAULT = unchecked((int)0x80010105);
        private const int CO_E_OBJNOTCONNECTED = unchecked((int)0x800401FD);

        /// <summary>
        /// Run a tool body and convert anything it throws into a ToolResult.
        /// </summary>
        /// <param name="toolName">Named in the error text so the agent knows what failed.</param>
        /// <param name="body">The tool implementation. Runs on the SOLIDWORKS thread.</param>
        /// <param name="log">Optional sink for diagnostics. Must never receive secrets.</param>
        public static ToolResult Run(string toolName, Func<ToolResult> body, ISwLog log = null)
        {
            try
            {
                return body() ?? ToolResult.Failure(
                    $"{toolName} returned no result. This is a bug in the add-in.", "internal_error");
            }
            catch (UnitRangeException ex)
            {
                // Bad argument from the model. Entirely recoverable: tell it
                // precisely what was wrong so the retry is a better one.
                log?.Debug($"{toolName}: rejected argument: {ex.Message}");
                return ToolResult.Failure($"{toolName}: {ex.Message}", "invalid_argument");
            }
            catch (ArgumentException ex)
            {
                log?.Debug($"{toolName}: invalid argument: {ex.Message}");
                return ToolResult.Failure($"{toolName}: {ex.Message}", "invalid_argument");
            }
            catch (SwSessionLostException ex)
            {
                log?.Error($"{toolName}: session lost: {ex.Message}");
                return ToolResult.Fatal($"{toolName}: {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                // Preconditions we check on purpose: no document open, no sketch
                // open, wrong document type, SOLIDWORKS refused a feature. Those
                // messages are already written for the agent and usually name
                // the tool that fixes the problem, so pass them through intact.
                //
                // Letting these fall through to the catch-all would be actively
                // harmful: "failed unexpectedly" tells the model it has hit a
                // bug in us and should stop, when what it should actually do is
                // open a sketch and try again.
                log?.Debug($"{toolName}: precondition not met: {ex.Message}");
                return ToolResult.Failure($"{toolName}: {ex.Message}", "precondition_failed");
            }
            catch (COMException ex)
            {
                if (IsSessionLost(ex))
                {
                    log?.Error($"{toolName}: SOLIDWORKS is no longer reachable (0x{ex.HResult:X8}).");
                    return ToolResult.Fatal(
                        $"{toolName}: SOLIDWORKS is no longer responding. The session has ended.");
                }

                log?.Error($"{toolName}: COM failure 0x{ex.HResult:X8}: {ex.Message}");
                return ToolResult.Failure(
                    $"{toolName}: the SOLIDWORKS API rejected the call (0x{ex.HResult:X8}). {ex.Message}",
                    "com_error");
            }
            catch (Exception ex)
            {
                // The catch-all is the point of this type. Anything that gets
                // here is a bug in us, but it still must not reach SOLIDWORKS.
                log?.Error($"{toolName}: unhandled {ex.GetType().Name}: {ex.Message}");
                log?.Debug(ex.ToString());
                return ToolResult.Failure(
                    $"{toolName} failed unexpectedly: {ex.GetType().Name}: {ex.Message}", "internal_error");
            }
        }

        /// <summary>
        /// Async variant, for tool bodies that must dispatch onto the
        /// SOLIDWORKS thread themselves.
        /// </summary>
        public static async Task<ToolResult> RunAsync(string toolName, Func<Task<ToolResult>> body, ISwLog log = null)
        {
            try
            {
                var result = await body().ConfigureAwait(false);
                return result ?? ToolResult.Failure(
                    $"{toolName} returned no result. This is a bug in the add-in.", "internal_error");
            }
            catch (Exception ex)
            {
                // Reuse the synchronous classification by rethrowing into it.
                return Run(toolName, () => throw ex, log);
            }
        }

        /// <summary>
        /// Wrap a bare COM call whose failure should abort the surrounding tool.
        /// Use when a null or false return is not distinguishable from success.
        /// </summary>
        public static T Com<T>(string what, Func<T> call)
        {
            try
            {
                return call();
            }
            catch (COMException ex) when (IsSessionLost(ex))
            {
                throw new SwSessionLostException(
                    $"SOLIDWORKS stopped responding during '{what}' (0x{ex.HResult:X8}).", ex);
            }
        }

        private static bool IsSessionLost(COMException ex)
        {
            return ex.HResult == RPC_E_DISCONNECTED
                || ex.HResult == RPC_S_SERVER_UNAVAILABLE
                || ex.HResult == RPC_E_SERVERFAULT
                || ex.HResult == CO_E_OBJNOTCONNECTED;
        }
    }

    /// <summary>The SOLIDWORKS session we were given is gone. Not retryable.</summary>
    public class SwSessionLostException : Exception
    {
        public SwSessionLostException(string message, Exception inner = null) : base(message, inner) { }
    }
}
