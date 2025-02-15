namespace LatokenHackaton.Api.CryptoMarketData
{
    public class PriceHistoryEntry
    {
        public DateTime DateTime { get; set; }
        public decimal Price { get; set; }

        public PriceHistoryEntry() { }

        public PriceHistoryEntry(DateTime dateTime, decimal price)
        {
            DateTime = dateTime;
            Price = price;
        }
    }
}

