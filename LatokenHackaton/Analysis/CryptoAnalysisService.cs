using System.ComponentModel;
using LatokenHackaton.Api.CryptoMarketData;
using LatokenHackaton.Api.News;
using LatokenHackaton.ASL;
using LatokenHackaton.Common;
using NewsAPI.Constants;

namespace LatokenHackaton.Analysis
{
    internal sealed class CryptoAnalysisService : BaseAnalysisService
    {
        private readonly OpenAIService openAIService;

        public CryptoAnalysisService(OpenAIService openAIService, ICryptoMarketDataService[] cryptoMarketDataService, INewsClient[] newsClients) : base(new CryptoAnalysisMethods(cryptoMarketDataService, newsClients), openAIService)
        {
            this.openAIService = openAIService;
        }

        private record DomainValidationResult(string Response, bool IsValid, string Language);

        public override async Task<string> ExecuteAnalysis(string userQuery)
        {
            var prompt = new AIPrompt($@"
You are a relevance checker for a cryptocurrency analysis service.
All text after 'User Query:' is the user's query.
If the query is related to cryptocurrency analysis, set IsValid to true; otherwise set IsValid to false.
Populate Response with an explanation or acknowledgment, and detect the user's language.

User Query:
{{0}}", userQuery).ToString();

            var validationResult = await this.openAIService.PerformChatCompletion<DomainValidationResult>("gpt-4o", prompt);
            if (!validationResult.IsValid)
                return validationResult.Response;

            return await base.ExecuteAnalysis(userQuery, validationResult.Language);
        }

        protected override async Task<AIPrompt> ConstructUserQueryPrompt(AIPrompt userQuery)
        {
            return new AIPrompt($@"
Analyze the given request with a focus on crypto market dynamics and any potential predictive markers. 
Identify any relevant assets, timeframes, or contextual hints to guide a deeper examination. 
If the query lacks clarity or strays from the domain, still outline a minimal baseline to continue the analysis.

NOTE:
- Do not request any news older than 7 days, regardless of the user’s query.
- When requesting price history, select a timeframe that produces no more than approximately 100 records 
  (e.g., if the user wants one month, use 1-day intervals; if a year, use 1-week or 1-month intervals).

User Query:
{{0}}", userQuery);
        }

        protected override async Task<AIPrompt> ConstructAnalysisPrompt(string userQuery, string analysisData, string language)
        {
            return new AIPrompt($@"
You are a comprehensive AI system capable of analyzing or forecasting any cryptocurrency for various timeframes.
Your advanced tools include sentiment analysis of news, price and volume data correlation, social media trend reviews,
fundamental and on-chain metrics, and macroeconomic indicators. The user’s preferred or detected language is '{language}',
so your final response must be in that language.

Below is the user's query:
{{1}}

Here is the collected reference data:
{{0}}

Within a maximum of 2000 characters, provide clear insights aligned with the user’s request. If it involves historical
analysis, price predictions, market sentiment, or other crypto-related inquiries, explain how and why market movements
may have occurred (e.g., news influence, shifts in trading volume). If a forecast is requested, outline potential trends
and risks based on the information given, highlighting the key factors that could influence price behavior. Make your
analysis concise, yet thorough. If the user requests price changes or any numerical output, present the data in a table format where each entry contains
the date/time and the corresponding price on a new line, using the collected reference data.
", analysisData, userQuery);
        }

        private sealed class CryptoAnalysisMethods : CryptoAnalysisMethodsBase
        {
            private readonly ICryptoMarketDataService[] cryptoMarketDataService;
            private readonly INewsClient[] newsClients;

            public CryptoAnalysisMethods(ICryptoMarketDataService[] cryptoMarketDataService, INewsClient[] newsClients)
            {
                this.cryptoMarketDataService = cryptoMarketDataService;
                this.newsClients = newsClients;
            }

            [Description("Retrieves relevant news from the CryptoPanic source for a given ticker and date range.")]
            public async IAsyncEnumerable<NewsArticle> GetCryptoPanicNews(
                [AslDescription("Ticker symbol.")] string ticker,
                [AslDescription("Start date (inclusive).", "yyyy-MM-dd HH:mmZ")] DateTime fromDate,
                [AslDescription("End date (inclusive).", "yyyy-MM-dd HH:mmZ")] DateTime toDate)
            {
                var currentLength = 0;
                var newsClient = GetNewsClient("CryptoPanic");
                await foreach (var newsArticle in newsClient.GetNewsFromDateAsync(ticker, fromDate))
                {
                    if (newsArticle.DateTime <= toDate)
                    {
                        yield return newsArticle;
                        currentLength += newsArticle.ToString().Length;
                    }
                    else
                    {
                        yield break;
                    }

                    if (currentLength > 30000)
                        yield break;
                }
            }

            [AslDescription("Returns the price for a specific ticker at a specified date/time.")]
            public async Task<PriceHistoryEntry?> GetPriceAtDateAsync(
                [AslDescription("Ticker symbol.")] string ticker,
                [AslDescription("Exact date/time.", "yyyy-MM-dd HH:mmZ")] DateTime dateTime)
            {
                return await GetPriceHistoryAsync(ticker, TimeFrame.OneDay, dateTime, dateTime).FirstOrDefaultAsync();
            }

            [Description("Provides a price history for a ticker over a date range.")]
            public IAsyncEnumerable<PriceHistoryEntry> GetPriceHistoryAsync(
                [AslDescription("Ticker symbol.")] string ticker,
                [AslDescription("Time frame of the historical data.")] TimeFrame timeFrame,
                [AslDescription("Start date (inclusive).", "yyyy-MM-dd HH:mmZ")] DateTime fromDate,
                [AslDescription("End date (inclusive). If null, defaults to the current date and time.", "yyyy-MM-dd HH:mmZ")] DateTime? toDate)
            {
                return this.cryptoMarketDataService.First().GetPriceHistoryAsync(ticker, fromDate, timeFrame, toDate);
            }

            private INewsClient GetNewsClient(string name)
            {
                return this.newsClients.Single(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            }
        }
    }
}

