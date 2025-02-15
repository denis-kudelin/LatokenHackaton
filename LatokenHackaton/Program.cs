using LatokenHackaton.Analysis;
using LatokenHackaton.Api.Captcha;
using LatokenHackaton.Api.CryptoMarketData;
using LatokenHackaton.Api.News;
using LatokenHackaton.Api.News.CryptoPanic;
using LatokenHackaton.Common;
using LatokenHackaton.Telegram;

namespace LatokenHackaton;

class Program
{
    static void Main(string[] args)
    {
        Task.Run(async () =>
        {
            var telegramCryptoAnalysisService = new TelegramCryptoAnalysisService(AppConfig.TelegramBotToken, AppConfig.TelegramAdminId);
            await telegramCryptoAnalysisService.StartAsync();
        }).Wait();

        Thread.Sleep(Timeout.Infinite);
    }
}

