using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ACP.NINA.Plugin.Tests {

    /// A stand-in for ACP that answers in memory. The real POST /api/plans/match
    /// is being built on the server in parallel, so the client is tested against
    /// the agreed response shape rather than against a running server.
    ///
    /// Records what it was asked, so a test can assert on the Authorization
    /// header and on the request body without the client exposing either.
    public class FakeAcpServer : HttpMessageHandler {

        private readonly Dictionary<string, Func<string>> routes =
            new Dictionary<string, Func<string>>(StringComparer.OrdinalIgnoreCase);

        /// When set, every request is answered with this status instead of a
        /// route, which is how the 401 path is exercised.
        public HttpStatusCode? ForcedStatus { get; set; }

        public string ForcedBody { get; set; } = "{\"error\": \"unauthorized\"}";

        public string LastPath { get; private set; }
        public string LastAuthorizationHeader { get; private set; }
        public string LastBody { get; private set; }
        public int RequestCount { get; private set; }

        public FakeAcpServer Route(string path, string jsonResponse) {
            routes[path] = () => jsonResponse;
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken
        ) {
            RequestCount++;
            LastPath = request.RequestUri.AbsolutePath;
            LastAuthorizationHeader = request.Headers.Authorization?.ToString();
            LastBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (ForcedStatus.HasValue) {
                return new HttpResponseMessage(ForcedStatus.Value) {
                    Content = new StringContent(ForcedBody, Encoding.UTF8, "application/json"),
                };
            }
            if (!routes.TryGetValue(LastPath, out var body)) {
                return new HttpResponseMessage(HttpStatusCode.NotFound) {
                    Content = new StringContent(
                        "{\"error\": \"no such route in the fake\"}", Encoding.UTF8, "application/json"
                    ),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(body(), Encoding.UTF8, "application/json"),
            };
        }
    }
}
