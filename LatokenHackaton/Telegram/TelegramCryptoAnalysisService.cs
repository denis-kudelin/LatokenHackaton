using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using LatokenHackaton.Analysis;
using LatokenHackaton.Api.Captcha;
using LatokenHackaton.Api.CryptoMarketData;
using LatokenHackaton.Api.News.CryptoPanic;
using LatokenHackaton.Common;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace LatokenHackaton.Telegram
{
    internal class TelegramCryptoAnalysisService
    {
        private const string ProcessingMessage = "Your request is being processed, please wait.";
        private const string InsufficientBalance = "Insufficient balance";
        private const string RequestCanceled = "Request canceled.";
        private const string InvalidInput = "Invalid input. Please type your request or /cancel.";

        private readonly string botToken;
        private readonly long adminId;
        private TelegramBotClient bot;
        private CancellationTokenSource cts;
        private readonly PersistentTypedDictionary<long, TelegramCryptoAnalysisUser> users
            = new(nameof(TelegramCryptoAnalysisUser));
        private readonly Channel<(long UserId, string RequestData)> requestChannel
            = Channel.CreateUnbounded<(long, string)>();

        public TelegramCryptoAnalysisService(string botToken, long adminId)
        {
            this.botToken = botToken;
            this.adminId = adminId;
        }

        public async Task StartAsync()
        {
            this.bot = new TelegramBotClient(this.botToken);
            this.cts = new CancellationTokenSource();
            _ = Task.Run(() => this.PollUpdates(this.cts.Token), this.cts.Token);
            _ = Task.Run(() => this.ProcessRequests(this.cts.Token), this.cts.Token);
            _ = Task.Run(() => this.RefreshActiveRequests(this.cts.Token), this.cts.Token);
        }

        public void Stop()
        {
            this.cts.Cancel();
        }

        private async Task PollUpdates(CancellationToken token)
        {
            var offset = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var updates = await this.bot.GetUpdatesAsync(offset, timeout: 20, cancellationToken: token);
                    foreach (var update in updates)
                    {
                        offset = update.Id + 1;
                        _ = Task.Run(() => this.HandleUpdate(update), token);
                    }
                }
                catch (Exception ex)
                {
                    await this.ReportError(ex);
                }
            }
        }

        private async Task HandleUpdate(Update update)
        {
            try
            {
                if (update.Type == UpdateType.Message && update.Message != null && update.Message.From != null)
                {
                    var msg = update.Message;
                    if (!await this.users.ContainsKeyAsync(msg.From.Id))
                    {
                        this.users[msg.From.Id] = new TelegramCryptoAnalysisUser(
                            msg.From.Id,
                            msg.From.Username + "|" + msg.From.FirstName + "|" + msg.From.LastName
                        );
                    }
                    var user = this.users[msg.From.Id];
                    user.LastActivity = DateTime.UtcNow;

                    if (msg.From.Id == this.adminId)
                    {
                        await this.HandleAdminCommand(msg);
                        return;
                    }
                    if (user.IsRequestActive)
                    {
                        await this.DeleteMessage(msg.Chat.Id, msg.MessageId);
                        await this.EditOrSendProcessingMessage(user);
                        return;
                    }
                    if (user.NextStep == TelegramCryptoAnalysisUser.UserStep.WaitForRequestText)
                    {
                        var txt = msg.Text?.Trim();
                        if (string.IsNullOrEmpty(txt))
                        {
                            await this.Send(msg.Chat.Id, "Please type your request or /cancel to exit.");
                            return;
                        }
                        if (txt.StartsWith("/"))
                        {
                            if (txt.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
                            {
                                user.NextStep = TelegramCryptoAnalysisUser.UserStep.None;
                                await this.Send(msg.Chat.Id, RequestCanceled);
                                return;
                            }
                            await this.Send(msg.Chat.Id, InvalidInput);
                            return;
                        }
                        if (user.Balance <= 0)
                        {
                            user.NextStep = TelegramCryptoAnalysisUser.UserStep.None;
                            await this.Send(msg.Chat.Id, InsufficientBalance);
                            return;
                        }
                        user.NextStep = TelegramCryptoAnalysisUser.UserStep.None;
                        user.IsRequestActive = true;
                        var m = await this.Send(msg.Chat.Id, ProcessingMessage);
                        user.ActiveMessageId = m.MessageId;
                        await this.requestChannel.Writer.WriteAsync((user.Id, txt));
                        return;
                    }
                    await this.HandleUserCommand(msg);
                }
            }
            catch (Exception ex)
            {
                await this.ReportError(ex);
            }
        }

        private async Task HandleAdminCommand(Message msg)
        {
            var parts = msg.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1 && parts[0].Equals("/adminhelp", StringComparison.OrdinalIgnoreCase))
            {
                await this.Send(msg.Chat.Id, "Admin commands:\n/adminhelp - show this help\n/users - list all users\n/setbalance <userId> <amount> - set user balance");
            }
            else if (parts.Length == 1 && parts[0].Equals("/users", StringComparison.OrdinalIgnoreCase))
            {
                await this.ShowUsersList(msg);
            }
            else if (parts.Length == 3 && parts[0].Equals("/setbalance", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(parts[1], out var id) && decimal.TryParse(parts[2], out var bal))
                {
                    if (this.users.TryGetValue(id, out var user))
                    {
                        user.Balance = bal;
                        await this.users.UpdateValueAsync(id, user);
                        await this.Send(msg.Chat.Id, "Balance updated");
                    }
                    else
                    {
                        await this.Send(msg.Chat.Id, "No such user");
                    }
                }
                else
                {
                    await this.Send(msg.Chat.Id, "Invalid format. Use /setbalance <userId> <amount>");
                }
            }
            else
            {
                await this.Send(msg.Chat.Id, "Unknown admin command. Use /adminhelp for the list of available admin commands.");
            }
        }

        private async Task ShowUsersList(Message msg)
        {
            if (await this.users.CountAsync() == 0)
            {
                await this.Send(msg.Chat.Id, "No users found.");
                return;
            }
            var text = "Current users:\n";
            await foreach (var u in this.users)
            {
                var displayName = string.IsNullOrEmpty(u.Name) ? "unknown" : u.Name;
                text += $"ID: {u.Id}, Name: {displayName}, Balance: {u.Balance}\n";
            }
            text += "\nAdmin Commands:\n/adminhelp - show admin commands\n/users - list all users\n/setbalance <userId> <amount> - set user balance\n";
            await this.Send(msg.Chat.Id, text);
        }

        private async Task HandleUserCommand(Message msg)
        {
            var user = this.users[msg.From.Id];
            var text = msg.Text?.Trim().ToLowerInvariant();
            switch (text)
            {
                case "/start":
                    await this.Send(msg.Chat.Id, "Use /balance to view your balance or /request to create a new request. Use /help for all commands.");
                    break;
                case "/help":
                    await this.Send(msg.Chat.Id, "/start - greet\n/balance - check balance\n/request - create request\n/help - list commands");
                    break;
                case "/balance":
                    await this.Send(msg.Chat.Id, $"Balance: {user.Balance}");
                    break;
                case "/request":
                    if (user.Balance > 0)
                    {
                        user.NextStep = TelegramCryptoAnalysisUser.UserStep.WaitForRequestText;
                        await this.Send(msg.Chat.Id, "Please type the details of your request or /cancel to exit.");
                    }
                    else
                    {
                        await this.Send(msg.Chat.Id, InsufficientBalance);
                    }
                    break;
                default:
                    await this.Send(msg.Chat.Id, "Use /help to see available commands.");
                    break;
            }
        }

        private async Task ProcessRequests(CancellationToken token)
        {
            while (await this.requestChannel.Reader.WaitToReadAsync(token))
            {
                while (this.requestChannel.Reader.TryRead(out var item))
                {
                    if (!await this.users.ContainsKeyAsync(item.UserId)) continue;
                    var user = this.users[item.UserId];
                    string result;
                    try
                    {
                        var bybitCryptoMarketDataService = new BybitCryptoMarketDataService(AppConfig.BybitApiKey, AppConfig.BybitApiSecret);
                        var openAIService = new OpenAIService(AppConfig.OpenAIApiKey);
                        var client = new CryptopanicWebClient(new CloudflareCaptchaSolver(AppConfig.TwoCaptchaApiKey), AppConfig.CryptopanicUserName, AppConfig.CryptopanicPassword);
                        var newsClient = new CryptoPanicNewsClient(client, openAIService);
                        var cryptoAnalysisService = new CryptoAnalysisService(openAIService, new[] { bybitCryptoMarketDataService }, new[] { newsClient });
                        result = await cryptoAnalysisService.ExecuteAnalysis(item.RequestData);
                        user.Balance -= 1;
                        await this.users.UpdateValueAsync(item.UserId, user);
                    }
                    catch (Exception ex)
                    {
                        result = ex.ToString();
                    }
                    user.IsRequestActive = false;
                    if (user.ActiveMessageId != 0)
                    {
                        var finalText = $"Request finished: Balance {user.Balance}$\n\n{result}";
                        try
                        {
                            await this.bot.EditMessageTextAsync(user.Id, user.ActiveMessageId, finalText);
                        }
                        catch { }
                        user.ActiveMessageId = 0;
                    }
                }
            }
        }

        private async Task RefreshActiveRequests(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await foreach (var u in this.users)
                    {
                        if (u.IsRequestActive)
                        {
                            if ((DateTime.UtcNow - u.LastActivity).TotalSeconds < 30)
                            {
                                await this.EditOrSendProcessingMessage(u);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    await this.ReportError(ex);
                }
                await Task.Delay(TimeSpan.FromSeconds(10), token);
            }
        }

        private async Task<Message> Send(long chatId, string text)
        {
            try
            {
                return await this.bot.SendTextMessageAsync(chatId, text);
            }
            catch { }
            return null;
        }

        private async Task EditOrSendProcessingMessage(TelegramCryptoAnalysisUser user)
        {
            try
            {
                await this.bot.EditMessageTextAsync(user.Id, user.ActiveMessageId, ProcessingMessage);
            }
            catch
            {
                try
                {
                    var m = await this.bot.SendTextMessageAsync(user.Id, ProcessingMessage);
                    user.ActiveMessageId = m.MessageId;
                }
                catch { }
            }
        }

        private async Task DeleteMessage(long chatId, int messageId)
        {
            try
            {
                await this.bot.DeleteMessageAsync(chatId, messageId);
            }
            catch { }
        }

        private async Task ReportError(Exception ex)
        {
            await this.Send(this.adminId, ex.ToString());
        }
    }
}