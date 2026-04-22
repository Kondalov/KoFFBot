using KoFFBot.Data;
using KoFFBot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
using Serilog;

namespace KoFFBot.Services;

public class UpdateHandler : IUpdateHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private static readonly ConcurrentDictionary<long, string> _userStates = new();

    public UpdateHandler(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        Log.Debug("=== НОВЫЙ АПДЕЙТ ОТ TELEGRAM: {UpdateType} ===", update.Type);

        using var scope = _scopeFactory.CreateScope();
        var vpnDb = scope.ServiceProvider.GetRequiredService<VpnDbContext>();
        
        try
        {
            if (update.Type == UpdateType.Message && update.Message != null)
                await ProcessMessageAsync(botClient, update.Message, vpnDb, cancellationToken);
            else if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery != null)
                await ProcessCallbackQueryAsync(botClient, update.CallbackQuery, vpnDb, cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Критическая ошибка при обработке Telegram Update!");
        }
    }

    private async Task ProcessMessageAsync(ITelegramBotClient botClient, Message message, VpnDbContext dbContext, CancellationToken cancellationToken)
    {
        if (message.Text == null || message.From == null) return;
        var user = message.From;

        Log.Debug("[ТЕКСТ] Пользователь {UserId} пишет: {Text}", user.Id, message.Text);

        if (_userStates.TryGetValue(user.Id, out var state) && state.StartsWith("wait_admin_reply_"))
        {
            if (long.TryParse(state.Replace("wait_admin_reply_", ""), out long targetId))
            {
                dbContext.SupportMessages.Add(new SupportMessage { TelegramId = targetId, Text = message.Text, IsFromAdmin = true, IsRead = false, CreatedAt = DateTime.UtcNow });
                await dbContext.SaveChangesAsync(cancellationToken);

                await botClient.SendMessage(chatId: targetId, text: "💬 *Новое сообщение от поддержки!*\nОткройте приложение, чтобы прочитать.", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                await botClient.SendMessage(chatId: user.Id, text: $"✅ Ответ успешно отправлен пользователю.", cancellationToken: cancellationToken);

                _userStates.TryRemove(user.Id, out _);
                return;
            }
        }

        long? referrerId = null;
        if (message.Text.StartsWith("/start "))
        {
            var parts = message.Text.Split(' ');
            if (parts.Length > 1 && long.TryParse(parts[1], out long refId) && refId != user.Id) referrerId = refId;
        }

        await GetOrCreateUserAsync(user, referrerId, dbContext, cancellationToken);

        if (message.Text.StartsWith("/start"))
        {
            var buttons = new List<InlineKeyboardButton[]> { new[] { InlineKeyboardButton.WithWebApp("🌌 Открыть KoFFPanel", new WebAppInfo { Url = "https://61fe5ed40684db.lhr.life" }) } };
            await botClient.SendMessage(chatId: message.Chat.Id, text: "Добро пожаловать в KoFFPanel ⚡️\nНажмите кнопку ниже, чтобы открыть приложение.", replyMarkup: new InlineKeyboardMarkup(buttons), cancellationToken: cancellationToken);
        }
    }

    private async Task ProcessCallbackQueryAsync(ITelegramBotClient botClient, CallbackQuery callbackQuery, VpnDbContext dbContext, CancellationToken cancellationToken)
    {
        var user = callbackQuery.From;
        var chatId = callbackQuery.Message!.Chat.Id;
        string data = callbackQuery.Data ?? "";

        Log.Debug("[КНОПКА] Пользователь {UserId} нажал кнопку: '{Data}'", user.Id, data);

        string? adminIdStr = Environment.GetEnvironmentVariable("ADMIN_TG_ID")?.Trim('"', '\'', ' ');
        if (!string.IsNullOrEmpty(adminIdStr) && user.Id.ToString() != adminIdStr)
        {
            Log.Warning("[SECURITY] Попытка несанкционированного доступа к админ-панели от {UserId}", user.Id);
            await botClient.AnswerCallbackQuery(callbackQuery.Id, "⛔ Отказано в доступе!", showAlert: true, cancellationToken: cancellationToken);
            return;
        }

        try
        {
            if (data.StartsWith("hide_")) await HandleHideCommandAsync(botClient, chatId, callbackQuery, cancellationToken);
            else if (data.StartsWith("reply_")) await HandleReplyCommandAsync(botClient, user.Id, chatId, data, callbackQuery, cancellationToken);
            else if (data.StartsWith("req_")) await HandleReqCommandAsync(botClient, chatId, data, callbackQuery, dbContext, cancellationToken);
            else if (data.StartsWith("renew_")) await HandleRenewCommandAsync(botClient, chatId, data, callbackQuery, cancellationToken);
            else if (data.StartsWith("t1_") || data.StartsWith("t3_") || data.StartsWith("t6_")) await HandleTariffCommandAsync(botClient, chatId, data, callbackQuery, dbContext, cancellationToken);
            else if (data.StartsWith("e100_") || data.StartsWith("e300_") || data.StartsWith("e1000_")) await HandleEnergyCommandAsync(botClient, chatId, data, callbackQuery, dbContext, cancellationToken);
            else await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка при обработке кнопки {Data}", data);
        }
    }

    private async Task HandleHideCommandAsync(ITelegramBotClient botClient, long chatId, CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        await botClient.DeleteMessage(chatId, callbackQuery.Message!.MessageId, cancellationToken);
    }

    private async Task HandleReplyCommandAsync(ITelegramBotClient botClient, long userId, long chatId, string data, CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        string targetId = data.Replace("reply_", "");
        _userStates[userId] = $"wait_admin_reply_{targetId}";
        await botClient.SendMessage(chatId, $"Напишите ответ для пользователя `{targetId}`.", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
        await botClient.AnswerCallbackQuery(callbackQuery.Id, "Ожидаю текст...", cancellationToken: cancellationToken);
    }

    private async Task HandleReqCommandAsync(ITelegramBotClient botClient, long chatId, string data, CallbackQuery callbackQuery, VpnDbContext dbContext, CancellationToken cancellationToken)
    {
        if (!long.TryParse(data.Replace("req_", ""), out long targetId)) return;

        string requisites = "Здравствуйте! Оплата по номеру телефона: 8 909 01 00 473 (Сбербанк).\nПосле оплаты отправьте скриншот чека сюда.";
        dbContext.SupportMessages.Add(new SupportMessage { TelegramId = targetId, Text = requisites, IsFromAdmin = true, IsRead = false, CreatedAt = DateTime.UtcNow });
        await dbContext.SaveChangesAsync(cancellationToken);

        await botClient.SendMessage(chatId: targetId, text: "💳 *Вам отправлены реквизиты!*", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
        await botClient.AnswerCallbackQuery(callbackQuery.Id, "Реквизиты отправлены!", cancellationToken: cancellationToken);
    }

    private async Task HandleRenewCommandAsync(ITelegramBotClient botClient, long chatId, string data, CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        string targetId = data.Replace("renew_", "");
        var kb = new InlineKeyboardMarkup(new[] {
            new[] { InlineKeyboardButton.WithCallbackData("1 Мес (+100⚡)", $"t1_{targetId}"), InlineKeyboardButton.WithCallbackData("3 Мес (+350⚡)", $"t3_{targetId}") },
            new[] { InlineKeyboardButton.WithCallbackData("6 Мес (+1000⚡)", $"t6_{targetId}") },
            new[] { InlineKeyboardButton.WithCallbackData("🔋 +100⚡", $"e100_{targetId}"), InlineKeyboardButton.WithCallbackData("⚡ +300⚡", $"e300_{targetId}"), InlineKeyboardButton.WithCallbackData("☢️ +1000⚡", $"e1000_{targetId}") },
            new[] { InlineKeyboardButton.WithCallbackData("❌ Отмена", $"hide_{targetId}") }
        });
        await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message!.MessageId, replyMarkup: kb, cancellationToken: cancellationToken);
    }

    private async Task HandleTariffCommandAsync(ITelegramBotClient botClient, long chatId, string data, CallbackQuery callbackQuery, VpnDbContext dbContext, CancellationToken cancellationToken)
    {
        int months = data.StartsWith("t1_") ? 1 : (data.StartsWith("t3_") ? 3 : 6);
        int energyBonus = data.StartsWith("t1_") ? 100 : (data.StartsWith("t3_") ? 350 : 1000);

        if (!long.TryParse(data.Substring(3), out long targetId)) return;

        var sub = await dbContext.VpnSubscriptions.FirstOrDefaultAsync(s => s.TelegramId == targetId && s.IsActive, cancellationToken);
        if (sub != null)
        {
            DateTime baseDate = (sub.ExpiryDate.HasValue && sub.ExpiryDate.Value > DateTime.UtcNow) ? sub.ExpiryDate.Value : DateTime.UtcNow;
            sub.ExpiryDate = baseDate.AddDays(months * 30);
            sub.SyncStatus = SyncStatus.PendingUpdate;

            // Используем ExecuteUpdate для обновления энергии в Game DB (через raw SQL, т.к. контексты разные)
            await dbContext.Database.ExecuteSqlRawAsync(
                "UPDATE GameProfiles SET CurrentEnergy = CurrentEnergy + {0} WHERE TelegramId = {1}", 
                energyBonus, targetId);

            dbContext.SupportMessages.Add(new SupportMessage { TelegramId = targetId, Text = $"✅ Ваша подписка продлена на {months} мес!\n⚡ Энергия пополнена: +{energyBonus}", IsFromAdmin = true, IsRead = false, CreatedAt = DateTime.UtcNow });

            await dbContext.Referrals
                .Where(r => r.InvitedTelegramId == targetId && !r.IsActivated)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsActivated, true), cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);

            await botClient.SendMessage(chatId: targetId, text: "✅ *Подписка продлена!*", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
            await botClient.DeleteMessage(chatId, callbackQuery.Message!.MessageId, cancellationToken);
            await botClient.AnswerCallbackQuery(callbackQuery.Id, "Успешно!", cancellationToken: cancellationToken);
        }
    }

    private async Task HandleEnergyCommandAsync(ITelegramBotClient botClient, long chatId, string data, CallbackQuery callbackQuery, VpnDbContext dbContext, CancellationToken cancellationToken)
    {
        int energyBonus = data.StartsWith("e100_") ? 100 : (data.StartsWith("e300_") ? 300 : 1000);
        string prefix = data.StartsWith("e100_") ? "e100_" : (data.StartsWith("e300_") ? "e300_" : "e1000_");

        if (!long.TryParse(data.Replace(prefix, ""), out long targetId)) return;

        int affected = await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE GameProfiles SET CurrentEnergy = CurrentEnergy + {0} WHERE TelegramId = {1}", 
            energyBonus, targetId);

        if (affected > 0)
        {
            dbContext.SupportMessages.Add(new SupportMessage { TelegramId = targetId, Text = $"✅ Энергоблок активирован!\n⚡ Начислено: +{energyBonus}", IsFromAdmin = true, IsRead = false, CreatedAt = DateTime.UtcNow });
            await dbContext.SaveChangesAsync(cancellationToken);

            await botClient.SendMessage(chatId: targetId, text: $"✅ *Энергия зачислена!*", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
            await botClient.DeleteMessage(chatId, callbackQuery.Message!.MessageId, cancellationToken);
            await botClient.AnswerCallbackQuery(callbackQuery.Id, "Выдано!", cancellationToken: cancellationToken);
        }
    }

    private async Task<TelegramUser> GetOrCreateUserAsync(User user, long? referrerId, VpnDbContext dbContext, CancellationToken ct)
    {
        var dbUser = await dbContext.TelegramUsers.FirstOrDefaultAsync(u => u.TelegramId == user.Id, ct);
        if (dbUser == null)
        {
            dbUser = new TelegramUser { TelegramId = user.Id, FirstName = user.FirstName };
            dbContext.TelegramUsers.Add(dbUser);

            if (referrerId.HasValue && referrerId.Value != user.Id)
            {
                bool refExists = await dbContext.Referrals.AnyAsync(r => r.InvitedTelegramId == user.Id, ct);
                if (!refExists)
                {
                    dbContext.Referrals.Add(new Referral { InviterTelegramId = referrerId.Value, InvitedTelegramId = user.Id, CreatedAt = DateTime.UtcNow, IsActivated = false });
                }
            }
            await dbContext.SaveChangesAsync(ct);
        }
        return dbUser;
    }

    public Task HandleErrorAsync(ITelegramBotClient b, Exception e, HandleErrorSource s, CancellationToken c) => Task.CompletedTask;
}
