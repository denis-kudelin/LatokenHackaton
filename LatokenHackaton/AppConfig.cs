using System;
using DenisKudelin.Config;

namespace LatokenHackaton
{
	internal static class AppConfig
	{
        static AppConfig()
        {
            _ = new ConfigInitializer(typeof(AppConfig), "config.json", 5000);
        }

        public static string TelegramBotToken { get; set; }
        public static long TelegramAdminId { get; set; }
        public static string BybitApiKey { get; set; }
        public static string BybitApiSecret { get; set; }
        public static string OpenAIApiKey { get; set; }
        public static string TwoCaptchaApiKey { get; set; }
        public static string CryptopanicUserName { get; set; }
        public static string CryptopanicPassword { get; set; }
    }
}

