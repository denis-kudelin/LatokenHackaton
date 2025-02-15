using System;
namespace LatokenHackaton.Api.CryptoMarketData
{
    internal interface ICryptoMarketDataService
    {
        IAsyncEnumerable<PriceHistoryEntry> GetPriceHistoryAsync(
            string ticker,
            DateTime fromDate,
            TimeFrame timeFrame,
            DateTime? toDate = null
        );
    }
}

