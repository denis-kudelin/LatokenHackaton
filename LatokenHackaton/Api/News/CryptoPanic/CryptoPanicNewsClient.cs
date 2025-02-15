using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Web;
using HtmlAgilityPack;
using LatokenHackaton.Common;

namespace LatokenHackaton.Api.News.CryptoPanic
{
    internal sealed class CryptoPanicNewsClient : INewsClient
    {
        private static readonly Dictionary<string, (DateTime LastFetched, string? Slug)> nameToSlugCache = new();

        private static readonly Dictionary<string, (DateTime LastFetched, List<NewsArticle> Articles)> inMemoryNewsCache = new();

        private static readonly string?[] filtersToTry = new[]
        {
            "important",
            "hot",
            "rising",
            "commented",
            "bullish",
            "bearish",
            null
        };

        private readonly CryptopanicWebClient webClient;
        private readonly OpenAIService openAIService;
        private readonly CryptoPanicNewsDecryptor decryptor;

        public string Name => "CryptoPanic";

        public CryptoPanicNewsClient(CryptopanicWebClient webClient, OpenAIService openAIService)
        {
            this.webClient = webClient;
            this.openAIService = openAIService;
            this.decryptor = new CryptoPanicNewsDecryptor(this.webClient);
        }

        public async IAsyncEnumerable<NewsArticle> GetNewsFromDateAsync(string name, DateTime fromDate)
        {
            var now = DateTime.UtcNow;
            var cacheKey = string.IsNullOrEmpty(name) ? "_all_" : name;
            if (inMemoryNewsCache.TryGetValue(cacheKey, out var entry))
            {
                if (now - entry.LastFetched < TimeSpan.FromMinutes(5))
                {
                    foreach (var old in entry.Articles)
                    {
                        if (old.DateTime >= fromDate) yield return old;
                    }

                    yield break;
                }
            }

            var slug = await this.TryGetSlugAsync(name);
            var gathered = new List<NewsArticle>();
            var seenIds = new HashSet<string>();
            var coverageReached = false;
            foreach (var filter in filtersToTry)
            {
                if (coverageReached) break;
                var currentPage = 0;
                while (true)
                {
                    var formFields = this.BuildFormFields(slug, filter, currentPage);
                    var responseContent = await this.webClient.PostMultipartForm("/web-api/posts/", formFields);
                    if (string.IsNullOrEmpty(responseContent)) break;
                    using var rootDoc = JsonDocument.Parse(responseContent);
                    if (!rootDoc.RootElement.TryGetProperty("status", out var stEl) || !stEl.GetBoolean()) break;
                    var allLoaded = false;
                    if (rootDoc.RootElement.TryGetProperty("all_loaded", out var alEl))
                    {
                        if (alEl.ValueKind == JsonValueKind.True) allLoaded = true;
                    }
                    var articles = ParseArticles(responseContent, fromDate)
                        .OrderByDescending(x => x.DateTime)
                        .ToList();
                    foreach (var news in articles)
                    {
                        if (!seenIds.Contains(news.Id))
                        {
                            seenIds.Add(news.Id);
                            gathered.Add(news);
                            yield return news;
                        }
                        if (news.DateTime < fromDate)
                        {
                            coverageReached = true;
                            break;
                        }
                    }
                    if (coverageReached || allLoaded) break;
                    currentPage++;
                }
            }
            inMemoryNewsCache[cacheKey] = (DateTime.UtcNow, gathered);
        }

        private Dictionary<string, string> BuildFormFields(string? slug, string? filter, int page)
        {
            var dict = new Dictionary<string, object?>
            {
                ["feed"] = "all",
                ["module"] = "news",
                ["page"] = page
            };
            if (page > 0) dict["posts_cnt"] = page * 50;
            if (!string.IsNullOrEmpty(slug)) dict["currency"] = slug;
            if (!string.IsNullOrEmpty(filter)) dict["filter"] = filter;
            var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { IgnoreNullValues = true });
            return new Dictionary<string, string> { ["filters"] = json };
        }

        private async Task<string?> TryGetSlugAsync(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var now = DateTime.UtcNow;
            if (nameToSlugCache.TryGetValue(name, out var cacheVal))
            {
                if (now - cacheVal.LastFetched < TimeSpan.FromMinutes(5)) return cacheVal.Slug;
            }
            var ticker = await GetTicker(name);
            if (string.IsNullOrEmpty(ticker)) return null;
            var responseJson = await this.webClient.Get($"/web-api/ac/?q={HttpUtility.UrlEncode(ticker)}");
            if (string.IsNullOrEmpty(responseJson)) return null;
            var slug = await GetSlugFromJson(ticker, responseJson);
            nameToSlugCache[name] = (DateTime.UtcNow, slug);
            return slug;
        }

        private async Task<string?> GetTicker(string name)
        {
            var prompt = "Convert the name or description enclosed within '|' into the single most relevant recognized asset or cryptocurrency ticker (e.g., BTC, ETH, USDT). The output must contain exactly one ticker, strictly, no extra text.";
            var userPrompt = $"{prompt}: |{name.Replace("|", "")}|";
            return await this.openAIService.PerformChatCompletion<string>("gpt-4o-mini", userPrompt);
        }

        private async Task<string?> GetSlugFromJson(string ticker, string responseJson)
        {
            var prompt = $@"Based on the provided JSON array, return the single most relevant 'slug' for the given ticker.
Focus only on entries where 'kind' is 'crypto'.
If multiple entries share the same 'code', prioritize the main or official entry.
Always provide exactly one slug, no extra text.
JSON Array:
{responseJson}
Ticker: |{ticker.Replace("|", "")}|";
            return await this.openAIService.PerformChatCompletion<string>("gpt-4o-mini", prompt);
        }

        private IEnumerable<NewsArticle> ParseArticles(string responseContent, DateTime fromDate)
        {
            using var rootDoc = JsonDocument.Parse(responseContent);
            if (!rootDoc.RootElement.TryGetProperty("s", out var sEl)) yield break;
            var enc = sEl.GetString();
            if (string.IsNullOrEmpty(enc)) yield break;
            var dec = this.decryptor.DecryptTextAsync(enc, this.webClient.CsrfToken).Result;
            if (dec == null) yield break;
            using var parsedDoc = JsonDocument.Parse(dec);
            if (!parsedDoc.RootElement.TryGetProperty("l", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array) yield break;
            if (!parsedDoc.RootElement.TryGetProperty("k", out var colsEl) || colsEl.ValueKind != JsonValueKind.Array) yield break;
            var colArr = colsEl.EnumerateArray().ToArray();
            var map = new Dictionary<string, int>();
            for (var i = 0; i < colArr.Length; i++)
            {
                var nm = colArr[i].GetString() ?? "";
                if (!map.ContainsKey(nm)) map[nm] = i;
            }
            if (!CheckCols(map)) yield break;
            foreach (var item in rowsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array) continue;
                var arr = item.EnumerateArray().ToArray();
                var kindVal = GetStr(arr, map["kind"]);
                if (kindVal != "link") continue;
                var pubStr = GetStr(arr, map["published_at"]);
                if (string.IsNullOrEmpty(pubStr)) continue;
                if (!DateTime.TryParse(pubStr, out var pTime)) continue;
                if (pTime < fromDate) continue;
                var pk = GetStr(arr, map["pk"]);
                var url = GetStr(arr, map["url"]);
                var title = GetStr(arr, map["title"]) ?? "";
                var body = GetStr(arr, map["body"]) ?? "";
                var id = !string.IsNullOrEmpty(pk) ? pk : url;
                if (string.IsNullOrEmpty(id)) continue;
                var doc = new HtmlDocument();
                doc.LoadHtml(body);
                var textContent = doc.DocumentNode.InnerText;
                yield return new NewsArticle(pTime, title, textContent, id, url ?? "");
            }
        }

        private bool CheckCols(Dictionary<string, int> m)
        {
            var needed = new[] { "published_at", "title", "body", "kind", "pk", "url" };
            foreach (var x in needed) if (!m.ContainsKey(x)) return false;
            return true;
        }

        private static string? GetStr(JsonElement[] arr, int idx)
        {
            if (idx < 0 || idx >= arr.Length) return null;
            var e = arr[idx];
            return e.ValueKind switch
            {
                JsonValueKind.String => e.GetString(),
                JsonValueKind.Number => e.GetRawText(),
                _ => null
            };
        }
    }
}