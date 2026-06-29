using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace LoreVS.Server
{
    /// <summary>
    /// Probes a Lore server''s HTTP health endpoint so the extension can fail fast with a clear
    /// message instead of waiting on the worker''s 30s SDK timeout when no server is reachable.
    /// The extension never owns the server lifetime; it only checks whether one is up.
    /// </summary>
    internal static class LoreServerHealth
    {
        /// <summary>
        /// Returns true when the server at <paramref name="endpoint"/> answers its health check with
        /// a success status. Any connection error or timeout reports unreachable rather than throwing.
        /// </summary>
        public static async Task<bool> IsReachableAsync(LoreServerEndpoint endpoint, CancellationToken cancellationToken = default)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(endpoint.HealthCheckUrl);
                request.Method = "GET";
                request.Timeout = 3000;

                // HttpWebRequest.Timeout only applies to the synchronous APIs; GetResponseAsync ignores
                // it. Drive the timeout ourselves (linked to any caller token) and Abort the request
                // when it fires, otherwise an unresponsive host could hang for the OS connect timeout.
                using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
                    using (timeoutCts.Token.Register(request.Abort))
                    using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                    {
                        return (int)response.StatusCode < 400;
                    }
                }
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return false;
            }
        }
    }
}