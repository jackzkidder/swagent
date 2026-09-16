using System;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace SwAgent.Agent
{
    /// <summary>
    /// Works out why a request never reached Anthropic, and says something the
    /// user can act on.
    ///
    /// This exists because of a real support case: a machine reported "could not
    /// reach api.anthropic.com - check the network connection, or a proxy or
    /// firewall" while the network was fine. Every transport failure produced
    /// that same sentence, because the code caught Exception and printed the
    /// type name. "Check your network" is useless advice when the problem is
    /// TLS, and worse than useless when it sends someone to their IT department
    /// over a setting on their own PC.
    ///
    /// The SDK wraps transport failures in AnthropicIOException, so the cause is
    /// always one or two levels down the InnerException chain. Read the chain,
    /// not the top.
    /// </summary>
    public static class TransportDiagnosis
    {
        public sealed class Diagnosis
        {
            public Diagnosis(string code, string message, string detail)
            {
                Code = code;
                Message = message;
                Detail = detail;
            }

            /// <summary>tls, dns, proxy, blocked, timeout or unknown.</summary>
            public string Code { get; }

            /// <summary>What to show the user.</summary>
            public string Message { get; }

            /// <summary>The full exception chain, for the log. Never shown in the panel.</summary>
            public string Detail { get; }
        }

        public static Diagnosis Diagnose(Exception ex)
        {
            string chain = Flatten(ex);
            string lower = chain.ToLowerInvariant();

            if (HasType<AuthenticationException>(ex)
                || lower.Contains("secure channel")
                || lower.Contains("ssl/tls")
                || lower.Contains("schannel")
                || lower.Contains("received an unexpected eof"))
            {
                return new Diagnosis("tls",
                    "Windows would not open a secure connection to api.anthropic.com. This is almost " +
                    "always TLS: the machine is configured to refuse TLS 1.2, or something is inspecting " +
                    "HTTPS traffic between you and Anthropic. SwAgent asks for TLS 1.2 explicitly, so if " +
                    "this persists it is a Windows or network policy rather than a setting here.",
                    chain);
            }

            if (lower.Contains("no such host")
                || lower.Contains("name or service not known")
                || lower.Contains("name resolution")
                || HasSocketError(ex, SocketError.HostNotFound))
            {
                return new Diagnosis("dns",
                    "This PC could not look up api.anthropic.com at all. That is a DNS or network problem, " +
                    "not an SwAgent one - if a browser on this machine cannot open anthropic.com either, " +
                    "start there.",
                    chain);
            }

            if (lower.Contains("(407)") || lower.Contains("proxy authentication"))
            {
                return new Diagnosis("proxy",
                    "A proxy on this network is refusing the connection until it gets credentials (HTTP 407). " +
                    "SwAgent uses the proxy Windows is configured with, but cannot supply a password for it. " +
                    "Your IT team can allow api.anthropic.com through, or you can try another network.",
                    chain);
            }

            if (lower.Contains("timed out") || lower.Contains("timeout")
                || HasSocketError(ex, SocketError.TimedOut))
            {
                return new Diagnosis("timeout",
                    "The connection to api.anthropic.com was accepted but never answered, which usually means " +
                    "a firewall or VPN is holding it. Try again, and if it repeats, try a different network - " +
                    "a phone hotspot is the quickest way to tell a network problem from a machine one.",
                    chain);
            }

            if (lower.Contains("unable to connect") || lower.Contains("actively refused")
                || lower.Contains("connection was closed") || lower.Contains("forcibly closed")
                || HasSocketError(ex, SocketError.ConnectionRefused))
            {
                return new Diagnosis("blocked",
                    "Something between this PC and api.anthropic.com refused the connection - typically a " +
                    "firewall, VPN or corporate filter. University and company networks often block it. " +
                    "Testing on a phone hotspot will tell you in a minute whether it is the network.",
                    chain);
            }

            return new Diagnosis("unknown",
                "Could not reach api.anthropic.com: " + Best(ex) +
                " The log has the full details (menu, Open log folder).",
                chain);
        }

        /// <summary>The innermost message, which is nearly always the specific one.</summary>
        private static string Best(Exception ex)
        {
            Exception current = ex;
            while (current.InnerException != null) current = current.InnerException;
            return (current.Message ?? string.Empty).TrimEnd('.') + ".";
        }

        private static bool HasType<T>(Exception ex) where T : Exception
        {
            for (Exception e = ex; e != null; e = e.InnerException)
                if (e is T) return true;
            return false;
        }

        private static bool HasSocketError(Exception ex, SocketError error)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
                if (e is SocketException socket && socket.SocketErrorCode == error) return true;
            return false;
        }

        /// <summary>Every message and type in the chain, for the log.</summary>
        private static string Flatten(Exception ex)
        {
            var sb = new StringBuilder();
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if (sb.Length > 0) sb.Append(" <- ");
                sb.Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            return sb.ToString();
        }
    }
}
