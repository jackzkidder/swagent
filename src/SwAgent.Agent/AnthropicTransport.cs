using System;
using System.Net;
using System.Net.Http;

namespace SwAgent.Agent
{
    /// <summary>
    /// The one HttpClient every call to Anthropic goes through.
    ///
    /// Two deployment problems live here, both of which look identical to the
    /// user - "could not reach api.anthropic.com" - and neither of which is
    /// the network:
    ///
    /// 1. TLS. api.anthropic.com needs TLS 1.2 or better. On .NET Framework the
    ///    default comes from the host process and the machine's registry, and
    ///    we are loaded inside SLDWORKS.exe, whose config we cannot change.
    ///    SwAddIn raises ServicePointManager for the process; this pins it on
    ///    the handler as well, so a later component lowering the global setting
    ///    cannot quietly break us.
    ///
    /// 2. Proxies. A corporate proxy answers 407 until it gets credentials. The
    ///    SDK's own client does not send any, so the request dies before it
    ///    leaves the building. UseDefaultCredentials makes Windows offer the
    ///    signed-in user's, which is what every other app on that PC does.
    ///
    /// One client, reused: a new HttpClient per request exhausts sockets under
    /// load, and this is created once per add-in session.
    /// </summary>
    public static class AnthropicTransport
    {
        private static readonly Lazy<HttpClient> Shared = new Lazy<HttpClient>(Create);

        /// <summary>
        /// Longer than a chat response needs, shorter than a user will wait
        /// without deciding the add-in has frozen. The agent's own retry loop
        /// handles the transient cases.
        /// </summary>
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);

        public static HttpClient Client => Shared.Value;

        private static HttpClient Create()
        {
            var handler = new HttpClientHandler();

            try
            {
                // Use whatever proxy Windows is configured with, and offer the
                // signed-in user's credentials to it.
                handler.UseProxy = true;
                handler.Proxy = WebRequest.DefaultWebProxy;
                handler.UseDefaultCredentials = true;

                if (handler.Proxy != null)
                    handler.Proxy.Credentials = CredentialCache.DefaultCredentials;
            }
            catch (Exception)
            {
                // A locked-down machine can refuse parts of this. A client with
                // default proxy behaviour is still better than no client.
            }

            try
            {
                // Belt and braces with the process-wide setting in SwAddIn.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch (Exception)
            {
            }

            return new HttpClient(handler) { Timeout = RequestTimeout };
        }
    }
}
