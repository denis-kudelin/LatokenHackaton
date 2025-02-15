using System;
using System.Text.Json.Serialization;

namespace LatokenHackaton.Telegram
{
	internal class TelegramCryptoAnalysisUser
    {
        public long Id { get; }
        [JsonIgnore]
        public string Name { get; }

        public decimal Balance { get; set; }
        [JsonIgnore]
        public bool IsRequestActive { get; set; }
        [JsonIgnore]
        public int ActiveMessageId { get; set; }
        [JsonIgnore]
        public DateTime LastActivity { get; set; }
        [JsonIgnore]
        public UserStep NextStep { get; set; }

        public TelegramCryptoAnalysisUser(long id, string name)
        {
            this.Id = id;
            this.Name = name;
            this.Balance = 0;
        }

        internal enum UserStep
        {
            None,
            WaitForRequestText
        }
    }
}

