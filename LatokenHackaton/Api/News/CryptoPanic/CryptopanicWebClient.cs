using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using LatokenHackaton.Api.Captcha;
using LatokenHackaton.Common;
using RestSharp;
using ZstdSharp;

namespace LatokenHackaton.Api.News.CryptoPanic
{
    internal sealed class CryptopanicWebClient
    {
        private static readonly Uri BaseUri = new("https://cryptopanic.com");
        private static readonly string NoSessionCookieMessage = "No session cookie present. User not logged in.";
        private static readonly string InvalidZstdSizeMessage = "Zstd decompression size is invalid or zero.";
        private static readonly string FailedZstdMessage = "Failed to decompress Zstd response.";
        private static readonly string FormUrlEncoded = "application/x-www-form-urlencoded";
        private static readonly string CsrfCookieName = "csrftoken";
        private static readonly string SessionCookieName = "sessionid";
        private static readonly string UserAuthCookieName = "user_authenticated";
        private readonly PersistentDictionary cache = new(nameof(CryptoPanicNewsClient));
        private readonly object loginLock = new();

        private static readonly Regex CsrfTokenRegex = new Regex(
            "name=\"csrfmiddlewaretoken\"\\s+value=\"([^\"]+)\"",
            RegexOptions.Compiled
        );

        private static readonly Regex TurnstileScriptRegex = new Regex(
            "<script\\s+src\\s*=\\s*['\"](?<src>https://challenges.cloudflare.com/turnstile/[^\\s'\"]+)['\"]",
            RegexOptions.Compiled
        );

        private static readonly Regex TurnstileSiteKeyRegex = new Regex(
            "class=\"cf-turnstile\"\\s+data-sitekey=\"([^\"]+)\"",
            RegexOptions.Compiled
        );

        private static readonly string AcceptHeaderValue =
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp," +
            "image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7";

        private static readonly string[] CommonHeaders =
        {
            "accept","accept-encoding","accept-language","cache-control","pragma",
            "priority","referer","sec-ch-ua","sec-ch-ua-mobile","sec-ch-ua-platform",
            "sec-fetch-dest","sec-fetch-mode","sec-fetch-site","sec-fetch-user",
            "upgrade-insecure-requests","user-agent"
        };

        private static readonly string[] PostSpecificHeaders =
        {
            "content-length","cookie","origin","x-csrftoken","x-requested-with"
        };

        private static readonly string AcceptEncodingHeaderValue = "gzip, deflate, br, zstd";
        private static readonly string AcceptLanguageHeaderValue = "en-US,en;q=0.9";
        private static readonly string CacheControlHeaderValue = "no-cache";
        private static readonly string OriginHeaderValue = BaseUri.AbsoluteUri;
        private static readonly string PragmaHeaderValue = "no-cache";
        private static readonly string PriorityHeaderValue = "u=0, i";
        private static readonly string RefererHeaderValue = BaseUri.AbsoluteUri;
        private static readonly string SecChUaHeaderValue = "\"Google Chrome\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"";
        private static readonly string SecChUaMobileHeaderValue = "?0";
        private static readonly string SecChUaPlatformHeaderValue = "\"macOS\"";
        private static readonly string SecFetchDestHeaderValue = "document";
        private static readonly string SecFetchModeHeaderValue = "navigate";
        private static readonly string SecFetchSiteHeaderValue = "same-origin";
        private static readonly string SecFetchUserHeaderValue = "?1";
        private static readonly string UpgradeInsecureRequestsHeaderValue = "1";
        private static readonly string XRequestedWithHeaderValue = "XMLHttpRequest";
        private static readonly string UserAgentHeaderValue =
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        private readonly RestClient restClient;
        private readonly CloudflareCaptchaSolver captchaSolver;
        private readonly string username;
        private readonly string password;
        private readonly CookieContainer cookieContainer;

        private string? csrfToken;

        public string? CsrfToken => this.csrfToken;

        public CryptopanicWebClient(CloudflareCaptchaSolver captchaSolver, string username, string password)
        {
            this.cookieContainer = new CookieContainer();
            var options = new RestClientOptions(BaseUri)
            {
                CookieContainer = this.cookieContainer,
                FollowRedirects = false,
                MaxRedirects = 1,
                Proxy = new WebProxy("http://localhost:8080")
            };
            this.captchaSolver = captchaSolver;
            this.username = username;
            this.password = password;
            this.restClient = new RestClient(options);
        }

        public async Task<string?> Get(string path)
        {
            await this.EnsureSessionExists();
            var result = await this.SendAsync(new CryptopanicGetRequest(path));
            return result.Content;
        }

        public async Task<string?> Post(string path, string body)
        {
            await this.EnsureSessionExists();
            var result = await this.SendAsync(new CryptopanicLoginPostRequest(path, body));
            return result.Content;
        }

        public async Task<string?> PostMultipartForm(string path, Dictionary<string, string> formFields)
        {
            await this.EnsureSessionExists();
            var result = await this.SendAsync(new CryptopanicMultipartPostRequest(path, formFields));
            return result.Content;
        }

        public async Task<string?> GetCache(string path)
        {
            return await this.cache.GetOrAddAsync(path, async p => await this.Get(path));
        }

        public async Task<string?> PostCache(string path, string body)
        {
            return await this.cache.GetOrAddAsync(path + body, async p => await this.Post(path, body));
        }

        public async Task<string?> PostMultipartFormCache(string path, Dictionary<string, string> formFields)
        {
            return await this.cache.GetOrAddAsync(path + string.Join("|", formFields.Select(x => x.Key + ";"+ x.Value)), async p => await this.PostMultipartForm(path, formFields));
        }

        public (string Csrf, string Session, string UserAuth) GetCookies()
        {
            var cookies = this.cookieContainer.GetCookies(BaseUri);
            var csrf = "";
            var sessionId = "";
            var userAuth = "";
            foreach (Cookie cookie in cookies)
            {
                if (cookie.Name == CsrfCookieName) csrf = cookie.Value;
                if (cookie.Name == SessionCookieName) sessionId = cookie.Value;
                if (cookie.Name == UserAuthCookieName) userAuth = cookie.Value;
            }

            return (csrf, sessionId, userAuth);
        }

        private async Task EnsureSessionExists()
        {
            if(this.csrfToken == null)
            {
                var authFlow = new CryptopanicAuthFlow(this);
                this.csrfToken = await authFlow.LoginAsync(this.username, this.password);
            }

            var (_, sessionId, _) = this.GetCookies();
            if (string.IsNullOrEmpty(sessionId)) throw new InvalidOperationException(NoSessionCookieMessage);
        }

        private async Task<RestResponse> SendAsync(CryptopanicRequestBase requestBase)
        {
            var restRequest = requestBase.Build();
            if(!string.IsNullOrWhiteSpace(this.csrfToken))
                restRequest.AddHeader("x-csrftoken", this.csrfToken);
            restRequest.CookieContainer = this.cookieContainer;
            var response = await this.restClient.ExecuteAsync(restRequest);
            this.TryDecompressZstd(response);
            return response;
        }

        private bool TryDecompressZstd(RestResponse response)
        {
            if (response.ContentEncoding?.Any(x => x.Contains("zstd", StringComparison.OrdinalIgnoreCase)) != true
                || response.RawBytes == null)
            {
                return false;
            }

            var size = Decompressor.GetDecompressedSize(response.RawBytes);
            if (size <= 0) throw new InvalidOperationException(InvalidZstdSizeMessage);

            var decompressor = new Decompressor();
            var buffer = new byte[size];
            if (!decompressor.TryUnwrap(response.RawBytes, buffer, 0, out var written))
            {
                throw new InvalidOperationException(FailedZstdMessage);
            }

            response.Content = Encoding.UTF8.GetString(buffer, 0, written);
            return true;
        }

        private string? GetRedirectLocation(RestResponse response)
        {
            var locationHeader = response.Headers?.FirstOrDefault(
                x => x.Name.Equals("Location", StringComparison.OrdinalIgnoreCase)
            );
            return locationHeader?.Value?.ToString();
        }

        private string? ExtractTurnstileScript(string html)
        {
            var match = TurnstileScriptRegex.Match(html);
            return match.Success ? match.Groups["src"].Value : null;
        }

        private string? ExtractTurnstileSiteKey(string html)
        {
            var match = TurnstileSiteKeyRegex.Match(html);
            return match.Success ? match.Groups[1].Value : null;
        }

        private string? ExtractCsrfFromHtml(string html)
        {
            var match = CsrfTokenRegex.Match(html);
            return match.Success ? match.Groups[1].Value : null;
        }

        internal async Task<RestResponse> DoGet(string path)
        {
            return await this.SendAsync(new CryptopanicGetRequest(path));
        }

        internal async Task<RestResponse> DoPost(string path, string? body)
        {
            return await this.SendAsync(new CryptopanicLoginPostRequest(path, body));
        }

        private sealed class CryptopanicAuthFlow
        {
            private readonly CryptopanicWebClient webClient;

            public CryptopanicAuthFlow(CryptopanicWebClient client)
            {
                this.webClient = client;
            }

            public async Task<string> LoginAsync(string username, string password)
            {
                var initialResponse = await this.webClient.DoGet("/accounts/login/");
                if (!initialResponse.IsSuccessful || string.IsNullOrEmpty(initialResponse.Content))
                    return null;

                var (initialCsrf, _, _) = this.webClient.GetCookies();
                if (string.IsNullOrEmpty(initialCsrf))
                    return null;

                var scriptUrl = this.webClient.ExtractTurnstileScript(initialResponse.Content);
                var siteKey = this.webClient.ExtractTurnstileSiteKey(initialResponse.Content);

                string? captchaResponse = null;
                if (!string.IsNullOrEmpty(scriptUrl) && !string.IsNullOrEmpty(siteKey))
                {
                    var scriptPath = scriptUrl.Replace("https://challenges.cloudflare.com", "");
                    await this.webClient.DoGet(scriptPath);
                    captchaResponse = await this.webClient.captchaSolver.SolveTurnstile(new Uri(BaseUri, "/accounts/login/").AbsoluteUri, siteKey);
                }

                this.webClient.cookieContainer.Add(BaseUri, new Cookie(CsrfCookieName, initialCsrf));
                var formData =
                    "csrfmiddlewaretoken=" + Uri.EscapeDataString(initialCsrf) +
                    "&login=" + Uri.EscapeDataString(username) +
                    "&password=" + Uri.EscapeDataString(password) +
                    "&remember=on";

                if (!string.IsNullOrEmpty(captchaResponse))
                {
                    formData += "&cf-turnstile-response=" + Uri.EscapeDataString(captchaResponse);
                }

                var postResponse = await this.webClient.DoPost("/accounts/login/", formData);
                var (csrf, sessionId, userAuth) = this.webClient.GetCookies();
                if (string.IsNullOrEmpty(csrf) || string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(userAuth))
                    return null;
                return csrf;
            }
        }

        private abstract class CryptopanicRequestBase
        {
            protected abstract Method HttpMethod { get; }
            protected string Path { get; }
            protected virtual bool IsPost => this.HttpMethod == Method.Post;
            protected virtual string? RequestBody => null;
            protected virtual int? ForcedContentLength => null;
            protected virtual string? ContentType => null;

            protected CryptopanicRequestBase(string uriPath)
            {
                var uri = new Uri(uriPath, UriKind.RelativeOrAbsolute);
                this.Path = uri.IsAbsoluteUri ? uri.AbsoluteUri : new Uri(BaseUri, uri).AbsoluteUri;
            }

            public virtual RestRequest Build()
            {
                var request = new RestRequest(this.Path, this.HttpMethod)
                {

                };

                var headers = this.IsPost
                    ? CommonHeaders.Concat(PostSpecificHeaders).ToArray()
                    : CommonHeaders;

                foreach (var headerName in headers)
                {
                    var headerValue = headerName switch
                    {
                        "accept" => AcceptHeaderValue,
                        "accept-encoding" => AcceptEncodingHeaderValue,
                        "accept-language" => AcceptLanguageHeaderValue,
                        "cache-control" => CacheControlHeaderValue,
                        "content-length" => this.ForcedContentLength?.ToString() ?? "",
                        "cookie" => "",
                        "origin" => OriginHeaderValue,
                        "pragma" => PragmaHeaderValue,
                        "priority" => PriorityHeaderValue,
                        "referer" => RefererHeaderValue,
                        "sec-ch-ua" => SecChUaHeaderValue,
                        "sec-ch-ua-mobile" => SecChUaMobileHeaderValue,
                        "sec-ch-ua-platform" => SecChUaPlatformHeaderValue,
                        "sec-fetch-dest" => SecFetchDestHeaderValue,
                        "sec-fetch-mode" => SecFetchModeHeaderValue,
                        "sec-fetch-site" => SecFetchSiteHeaderValue,
                        "sec-fetch-user" => SecFetchUserHeaderValue,
                        "upgrade-insecure-requests" => UpgradeInsecureRequestsHeaderValue,
                        "x-requested-with" => XRequestedWithHeaderValue,
                        "user-agent" => UserAgentHeaderValue,
                        _ => ""
                    };

                    if (headerName == "cookie") continue;
                    if (!string.IsNullOrEmpty(headerValue))
                    {
                        request.AddHeader(headerName, headerValue);
                    }
                }

                if (this.IsPost && !string.IsNullOrEmpty(this.RequestBody))
                {
                    request.AddParameter(
                        this.ContentType ?? FormUrlEncoded,
                        this.RequestBody,
                        ParameterType.RequestBody
                    );
                }

                return request;
            }
        }

        private sealed class CryptopanicGetRequest : CryptopanicRequestBase
        {

            public CryptopanicGetRequest(string path) : base(path)
            {

            }

            protected override Method HttpMethod => Method.Get;
        }

        private sealed class CryptopanicLoginPostRequest : CryptopanicRequestBase
        {
            private readonly string? body;

            public CryptopanicLoginPostRequest(string uriPath, string? requestBody) : base(uriPath)
            {
                this.body = requestBody;
            }

            protected override Method HttpMethod => Method.Post;
            protected override string? RequestBody => this.body;
        }

        private sealed class CryptopanicMultipartPostRequest : CryptopanicRequestBase
        {
            private readonly Dictionary<string, string> formFields;

            public CryptopanicMultipartPostRequest(string uriPath, Dictionary<string, string> formFields) : base(uriPath)
            {
                this.formFields = formFields;
            }

            protected override Method HttpMethod => Method.Post;

            public override RestRequest Build()
            {
                var restRequest = base.Build();
                restRequest.AlwaysMultipartFormData = true;
                foreach (var (fieldKey, fieldValue) in this.formFields)
                {
                    restRequest.AddParameter(
                        fieldKey,
                        fieldValue
                    );
                }
                return restRequest;
            }
        }
    }
}