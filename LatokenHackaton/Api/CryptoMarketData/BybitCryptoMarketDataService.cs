using System;
using Bybit.Net.Clients;
using Bybit.Net.Enums;
using CryptoExchange.Net.CommonObjects;

namespace LatokenHackaton.Api.CryptoMarketData
{
    internal class BybitCryptoMarketDataService : CryptoMarketDataServiceBase
    {
        private readonly string apiKey;
        private readonly string apiSecret;

        public BybitCryptoMarketDataService(string apiKey, string apiSecret)
        {
            this.apiKey = apiKey;
            this.apiSecret = apiSecret;
        }

        public override IAsyncEnumerable<PriceHistoryEntry> GetPriceHistoryAsync(
            string ticker,
            DateTime fromDate,
            TimeFrame timeFrame,
            DateTime? toDate = null
        )
        {
            var actualToDate = toDate ?? DateTime.UtcNow;
            var interval = this.ConvertTimeFrame(timeFrame);
            return this.GetPriceHistoryWithCachingAsync(
                ticker,
                fromDate,
                actualToDate,
                timeFrame,
                () => this.GetSpotKlinesAsync(ticker + "USDT", fromDate, interval, actualToDate)
            );
        }

        private async IAsyncEnumerable<PriceHistoryEntry> GetSpotKlinesAsync(string ticker, DateTime fromDate, KlineInterval interval, DateTime actualToDate)
        {
            using var client = new BybitRestClient(options =>
            {
                options.ApiCredentials = new CryptoExchange.Net.Authentication.ApiCredentials(this.apiKey, this.apiSecret);
            });

            var candlesResult = await client.V5Api.ExchangeData.GetKlinesAsync(
                Category.Spot,
                ticker,
                interval,
                startTime: fromDate,
                endTime: actualToDate
            );

            if (candlesResult.Success && candlesResult.Data?.List != null)
            {
                foreach (var candle in candlesResult.Data.List)
                {
                    yield return new PriceHistoryEntry(candle.StartTime, candle.ClosePrice);
                }
            }
        }

        private KlineInterval ConvertTimeFrame(TimeFrame timeFrame)
        {
            return timeFrame switch
            {
                TimeFrame.OneMinute => KlineInterval.OneMinute,
                TimeFrame.FiveMinutes => KlineInterval.FiveMinutes,
                TimeFrame.FifteenMinutes => KlineInterval.FifteenMinutes,
                TimeFrame.ThirtyMinutes => KlineInterval.ThirtyMinutes,
                TimeFrame.OneHour => KlineInterval.OneHour,
                TimeFrame.FourHours => KlineInterval.FourHours,
                TimeFrame.OneDay => KlineInterval.OneDay,
                _ => throw new ArgumentOutOfRangeException(nameof(timeFrame), timeFrame, null)
            };
        }
    }
}