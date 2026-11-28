using System;
using BillionSend.Internal;

namespace BillionSend
{
    /// <summary>
    /// The BillionSend API client.
    /// </summary>
    /// <remarks>
    /// <code>
    /// using var client = new BillionSendClient(); // reads BILLIONSEND_API_KEY
    /// var sent = await client.Emails.SendAsync(new
    /// {
    ///     from = "Acme &lt;hello@acme.dev&gt;",
    ///     to = new[] { "ana@example.com" },
    ///     subject = "Welcome",
    ///     html = "&lt;p&gt;Hi&lt;/p&gt;",
    /// });
    /// Console.WriteLine(sent?["id"]);
    /// </code>
    /// Every method is asynchronous and returns the parsed JSON answer as a
    /// <see cref="System.Text.Json.Nodes.JsonNode"/>; a refusal or a failure is thrown as a
    /// <see cref="BillionSendException"/> subclass. One client is safe to share across threads
    /// and should live as long as the application.
    /// </remarks>
    public partial class BillionSendClient : IDisposable
    {
        /// <summary>The SDK version, sent in the <c>User-Agent</c> header.</summary>
        public const string SdkVersion = "0.4.0";

        /// <summary>The API address used when neither the options nor <c>BILLIONSEND_BASE_URL</c> set one.</summary>
        public const string DefaultBaseUrl = "https://api.billionsend.com";

        /// <summary>Retries per call when the options set none.</summary>
        public const int DefaultMaxRetries = 2;

        /// <summary>The per-attempt timeout when the options set none.</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

        private readonly ApiClient _api;

        /// <summary>
        /// A client configured from the environment: <c>BILLIONSEND_API_KEY</c> and, when set,
        /// <c>BILLIONSEND_BASE_URL</c>. Without a key only <c>Agent.SignupAsync</c> works.
        /// </summary>
        public BillionSendClient()
            : this(new BillionSendOptions())
        {
        }

        /// <summary>A client with this API key (<c>bs_...</c>).</summary>
        /// <param name="apiKey">The API key.</param>
        public BillionSendClient(string apiKey)
            : this(new BillionSendOptions { ApiKey = apiKey })
        {
        }

        /// <summary>A client with these options; what they leave unset comes from the environment or the defaults.</summary>
        /// <param name="options">The client options.</param>
        /// <exception cref="ArgumentException">An option is out of range or the base URL is not an absolute URL.</exception>
        public BillionSendClient(BillionSendOptions options)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _api = new ApiClient(options);
            InitResources(_api);
        }

        /// <summary>The API base URL this client calls.</summary>
        public string BaseUrl => _api.BaseUrl;

        internal ApiClient Api => _api;

        /// <summary>
        /// Releases the HTTP connections when the client created its own <see cref="System.Net.Http.HttpClient"/>;
        /// one passed in the options is left to its owner.
        /// </summary>
        public void Dispose()
        {
            _api.Dispose();
            GC.SuppressFinalize(this);
        }

        private partial void InitResources(ApiClient api);
    }
}
