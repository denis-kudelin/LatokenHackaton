using System;
namespace LatokenHackaton.Api.News
{
	internal interface INewsClient
	{
        IAsyncEnumerable<NewsArticle> GetNewsFromDateAsync(string ticker, DateTime fromDate);
        string Name { get; }
    }
}

