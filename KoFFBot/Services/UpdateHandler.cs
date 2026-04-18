using KoFFBot.Data;
using KoFFBot.Domain;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using System.Collections.Concurrent;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace KoFFBot.Services;

public class UpdateHandler : IUpdateHandler
{
    private readonly BotDbContext _dbContext;
    private readonly SubscriptionGenerator _subscriptionGenerator;

    private static readonly ConcurrentDictionary<long, string> _userStates = new();

    // ИСПРАВЛЕНИЕ: Убрали стандартный логгер, используем наш пуленепробиваемый BotLogger
    public UpdateHandler(BotDbContext dbContext, SubscriptionGenerator subscriptionGenerator)
    {
        _dbContext = dbContext;
        _subscriptionGenerator = subscriptionGenerator;
    }

    public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        try
        {
            BotLogger.Log("UPDATE", $"[ВХОДЯЩИЙ СИГНАЛ] Тип: {update.Type}");

            if (update.Type == UpdateType.Message && update.Message != null)
                await ProcessMessageAsync(botClient, update.Message, cancellationToken);
            else if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery != null)
                await ProcessCallbackQueryAsync(botClient, update.CallbackQuery, cancellationToken);
        }
        catch (Exception ex)
        {
            BotLogger.Log("UPDATE-CRITICAL", "Критическая ошибка в главном цикле обработки апдейта!", ex);
        }
    }

    private async Task ProcessMessageAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        if (message.Text == null || message.From == null) return;

        BotLogger.Log("MESSAGE", $"[ТЕКСТ] Пользователь {message.From.Id} (@{message.From.Username}) написал: {message.Text}");

        var user = message.From;
        long? referrerId = null;

        if (message.Text.StartsWith("/start "))
        {
            var parts = message.Text.Split(' ');
            if (parts.Length > 1 && long.TryParse(parts[1], out long refId) && refId != user.Id)
            {
                referrerId = refId;
            }
        }

        BotLogger.Log("DB", "Попытка сохранить/получить пользователя из БД...");
        var dbUser = await GetOrCreateUserAsync(user, referrerId, cancellationToken);
        BotLogger.Log("DB", "Успешная работа с БД пользователей.");

        if (_userStates.TryGetValue(user.Id, out var state) && state == "wait_email")
        {
            await HandleLinkAccountEmailAsync(botClient, message.Chat.Id, user.Id, message.Text, cancellationToken);
            return;
        }

        if (message.Text.StartsWith("/start"))
        {
            BotLogger.Log("MENU", "Отправка главного меню пользователю...");
            await SendMainMenuAsync(botClient, message.Chat.Id, dbUser, cancellationToken);
            BotLogger.Log("MENU", "Главное меню успешно отправлено.");
        }
    }

    private async Task ProcessCallbackQueryAsync(ITelegramBotClient botClient, CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        var user = callbackQuery.From;
        var chatId = callbackQuery.Message!.Chat.Id;

        BotLogger.Log("CALLBACK", $"[КНОПКА] Пользователь {user.Id} нажал: {callbackQuery.Data}");

        await botClient.AnswerCallbackQuery(callbackQueryId: callbackQuery.Id, cancellationToken: cancellationToken);
        var dbUser = await GetOrCreateUserAsync(user, null, cancellationToken);

        switch (callbackQuery.Data)
        {
            case "get_key":
                await HandleGetKeyAsync(botClient, chatId, dbUser, cancellationToken);
                break;
            case "link_account":
                _userStates[user.Id] = "wait_email";
                await botClient.SendMessage(chatId: chatId, text: "📧 *Привязка*\nОтправьте Email из панели:", parseMode: ParseMode.Markdown, replyMarkup: new ForceReplyMarkup { InputFieldPlaceholder = "email@example.com" }, cancellationToken: cancellationToken);
                break;
            case "referral":
                await botClient.SendMessage(chatId: chatId, text: $"🎁 *Реферальная программа*\n\nПригласите друга и получите бонус!\nВаша ссылка:\n`https://t.me/KoFFBot?start={user.Id}`\n\nПриглашено: {dbUser.ReferralCount} чел.", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                break;
        }
    }

    private async Task HandleLinkAccountEmailAsync(ITelegramBotClient bot, long chatId, long userId, string email, CancellationToken ct)
    {
        _userStates.TryRemove(userId, out _);
        email = email.Trim().ToLower();

        BotLogger.Log("LINK", $"Попытка привязки Email: {email}");

        var sub = await _dbContext.VpnSubscriptions.FirstOrDefaultAsync(s => s.Email.ToLower() == email && s.TelegramId == 0, ct);
        if (sub != null)
        {
            sub.TelegramId = userId;
            await _dbContext.SaveChangesAsync(ct);
            await bot.SendMessage(chatId: chatId, text: $"✅ Аккаунт привязан!", cancellationToken: ct);
            await SendMainMenuAsync(bot, chatId, await GetOrCreateUserAsync(new User { Id = userId }, null, ct), ct);
        }
        else
        {
            await bot.SendMessage(chatId: chatId, text: $"❌ Подписка не найдена. /start для отмены.", cancellationToken: ct);
        }
    }

    private async Task HandleGetKeyAsync(ITelegramBotClient bot, long chatId, TelegramUser user, CancellationToken ct)
    {
        BotLogger.Log("GENERATE", $"Запрос ключа для ID {user.TelegramId}");
        var sub = await _dbContext.VpnSubscriptions.FirstOrDefaultAsync(s => s.TelegramId == user.TelegramId && s.IsActive, ct);
        if (sub != null)
        {
            await bot.SendMessage(chatId: chatId, text: $"Ваша подписка активна (Остаток: {(sub.TrafficLimitBytes - sub.TrafficUsedBytes) / 1073741824} ГБ).\n\n`http://{sub.ServerIp}:8080/{sub.Uuid}`", parseMode: ParseMode.Markdown, cancellationToken: ct);
            return;
        }

        await bot.SendMessage(chatId: chatId, text: "⏳ Обработка...", cancellationToken: ct);
        var (success, uuid, serverIp, err) = await _subscriptionGenerator.GenerateNewSubscriptionAsync(user.TelegramId, $"tg_{user.TelegramId}", ct);

        if (!success)
        {
            BotLogger.Log("GENERATE-ERROR", $"Ошибка генерации: {err}");
            await bot.SendMessage(chatId: chatId, text: $"❌ {err}", cancellationToken: ct);
            return;
        }

        var newSub = await _dbContext.VpnSubscriptions.FirstAsync(s => s.Uuid == uuid, ct);
        await bot.SendMessage(chatId: chatId, text: $"✅ Готово! Профиль в очереди.\n\n`http://{newSub.ServerIp}:8080/{uuid}`", parseMode: ParseMode.Markdown, cancellationToken: ct);
    }

    private async Task<TelegramUser> GetOrCreateUserAsync(User user, long? referrerId, CancellationToken ct)
    {
        var dbUser = await _dbContext.TelegramUsers.FirstOrDefaultAsync(u => u.TelegramId == user.Id, ct);
        if (dbUser == null)
        {
            dbUser = new TelegramUser { TelegramId = user.Id, FirstName = user.FirstName, InvitedBy = referrerId };
            _dbContext.TelegramUsers.Add(dbUser);

            if (referrerId.HasValue)
            {
                var referrer = await _dbContext.TelegramUsers.FirstOrDefaultAsync(u => u.TelegramId == referrerId.Value, ct);
                if (referrer != null) referrer.ReferralCount++;
            }
            await _dbContext.SaveChangesAsync(ct);
        }
        return dbUser;
    }

    private async Task SendMainMenuAsync(ITelegramBotClient bot, long chatId, TelegramUser user, CancellationToken ct)
    {
        var buttons = new List<InlineKeyboardButton[]>();

        // ОСТАВЛЯЕМ ТОЛЬКО ОДНУ МЕГА-КНОПКУ
        buttons.Add(new[] { InlineKeyboardButton.WithWebApp("🌌 Открыть KoFFPanel", new WebAppInfo { Url = "https://7219efebba0418.lhr.life" }) });

        // ВАЖНО: Вставь сюда свою рабочую ссылку из ngrok или localhost.run!

        await bot.SendMessage(chatId: chatId, text: "Добро пожаловать в KoFFPanel ⚡️\nНажмите кнопку ниже, чтобы открыть приложение.", replyMarkup: new InlineKeyboardMarkup(buttons), cancellationToken: ct);
    }

    public Task HandleErrorAsync(ITelegramBotClient b, Exception e, HandleErrorSource s, CancellationToken c) => Task.CompletedTask;
}