using KoFFBot.Data;
using KoFFBot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace KoFFBot.Services;

public class UpdateHandler : IUpdateHandler
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IServiceScopeFactory _scopeFactory;
    private static readonly ConcurrentDictionary<long, string> _userStates = new();

    public UpdateHandler(IServiceProvider serviceProvider, IServiceScopeFactory scopeFactory)
    {
        _serviceProvider = serviceProvider;
        _scopeFactory = scopeFactory;
    }

    public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        Log.Debug("=== НОВЫЙ АПДЕЙТ ОТ TELEGRAM: {UpdateType} ===", update.Type);

        using var scope = _scopeFactory.CreateScope();
        var vpnDb = scope.ServiceProvider.GetRequiredService<VpnDbContext>();

        try
        {
            if (update.Message is { } message) await HandleMessageAsync(botClient, message, vpnDb, cancellationToken);
            else if (update.CallbackQuery is { } callbackQuery) await HandleCallbackQueryAsync(botClient, callbackQuery, vpnDb, cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Критическая ошибка при обработке Telegram Update!");
        }
    }

    private async Task HandleMessageAsync(ITelegramBotClient botClient, Message message, VpnDbContext dbContext, CancellationToken cancellationToken)
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

        // Авто-регистрация/обновление пользователя в БД
        var dbUser = await GetOrCreateUserAsync(dbContext, user, cancellationToken);

        if (message.Text.StartsWith("/start"))
        {
            var parts = message.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
            {
                await ProcessReferralAsync(botClient, dbContext, user.Id, parts[1], cancellationToken);
            }

            string webAppUrl = Environment.GetEnvironmentVariable("WEBAPP_URL")?.Trim() ?? "https://gecko.makeup";
            webAppUrl = $"{webAppUrl}?t={DateTime.UtcNow.Ticks}";
            var buttons = new List<InlineKeyboardButton[]> { new[] { InlineKeyboardButton.WithWebApp("🌌 Открыть KoFFPanel", new WebAppInfo { Url = webAppUrl }) } };
            //var buttons = new List<InlineKeyboardButton[]> { new[] { InlineKeyboardButton.WithWebApp("🌌 Открыть KoFFPanel", new WebAppInfo { Url = "https://3d34096cff96f0.lhr.life" }) } };
            await botClient.SendMessage(chatId: message.Chat.Id, text: "Добро пожаловать в KoFFPanel ⚡️\nНажмите кнопку ниже, чтобы открыть приложение.", replyMarkup: new InlineKeyboardMarkup(buttons), cancellationToken: cancellationToken);
        }
    }

    private async Task ProcessReferralAsync(ITelegramBotClient botClient, VpnDbContext dbContext, long invitedId, string startParam, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(startParam)) return;

        // Smart Anti-Fraud: Проверка на корректность ID и попытку саморефералки
        if (long.TryParse(startParam, out long inviterId))
        {
            if (inviterId == invitedId)
            {
                Log.Warning("[ANTI-FRAUD] Пользователь {UserId} пытался использовать свою же ссылку.", invitedId);
                return;
            }

            // Ищем пригласившего
            var inviter = await dbContext.TelegramUsers.FirstOrDefaultAsync(u => u.TelegramId == inviterId, ct);
            if (inviter == null)
            {
                Log.Warning("[REFERRAL] Пригласивший {InviterId} не найден для {UserId}.", inviterId, invitedId);
                return;
            }

            // Проверка: не был ли этот пользователь уже приглашен кем-то другим (защита от дублей)
            var alreadyReferred = await dbContext.Referrals.AnyAsync(r => r.InvitedTelegramId == invitedId, ct);
            if (alreadyReferred)
            {
                Log.Warning("[ANTI-FRAUD] Пользователь {UserId} уже был приглашен ранее.", invitedId);
                return;
            }

            // Регистрируем реферал (Smart Logic - Апрель 2026)
            dbContext.Referrals.Add(new Referral
            {
                InviterTelegramId = inviterId,
                InvitedTelegramId = invitedId,
                CreatedAt = DateTime.UtcNow,
                IsActivated = true
            });

            inviter.ReferralCount += 1;
            await dbContext.SaveChangesAsync(ct);
            
            Log.Information("[REFERRAL] Успешная рефералка! {InviterId} пригласил {UserId}. Всего друзей: {Count}", inviterId, invitedId, inviter.ReferralCount);

            try
            {
                await botClient.SendMessage(chatId: inviterId, text: $"🎉 *Новый друг!*\nПо вашей ссылке присоединился новый пользователь.\nТеперь у вас друзей: {inviter.ReferralCount}", parseMode: ParseMode.Markdown, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Не удалось отправить уведомление о рефералке пользователю {InviterId}", inviterId);
            }
        }
    }

    private async Task HandleCallbackQueryAsync(ITelegramBotClient botClient, CallbackQuery callbackQuery, VpnDbContext vpnDb, CancellationToken cancellationToken)
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
            if (data.StartsWith("reply_")) await HandleReplyCommandAsync(botClient, chatId, user.Id, data, callbackQuery, cancellationToken);
            else if (data.StartsWith("req_")) await HandleReqCommandAsync(botClient, chatId, data, callbackQuery, vpnDb, cancellationToken);
            else if (data.StartsWith("renew_")) await HandleRenewCommandAsync(botClient, chatId, data, callbackQuery, cancellationToken);
            else if (data.StartsWith("t1_") || data.StartsWith("t3_") || data.StartsWith("t6_")) await HandleRenewConfirmAsync(botClient, chatId, data, callbackQuery, vpnDb, cancellationToken);
            else if (data.StartsWith("e100_") || data.StartsWith("e300_") || data.StartsWith("e1000_")) await HandleEnergyChargeAsync(botClient, chatId, data, callbackQuery, cancellationToken);
            else if (data.StartsWith("hide_")) await botClient.DeleteMessage(chatId, callbackQuery.Message!.MessageId, cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка при обработке кнопки {Data}", data);
        }
    }

    private async Task HandleReplyCommandAsync(ITelegramBotClient botClient, long chatId, long userId, string data, CallbackQuery callbackQuery, CancellationToken cancellationToken)
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

    private async Task HandleRenewConfirmAsync(ITelegramBotClient botClient, long chatId, string data, CallbackQuery callbackQuery, VpnDbContext dbContext, CancellationToken cancellationToken)
    {
        int months = data[1] - '0';
        string targetIdStr = data.Substring(3);
        if (long.TryParse(targetIdStr, out long targetId))
        {
            var sub = await dbContext.VpnSubscriptions.FirstOrDefaultAsync(s => s.TelegramId == targetId && s.IsActive, cancellationToken);
            if (sub == null) { await botClient.AnswerCallbackQuery(callbackQuery.Id, "Подписка не найдена!", showAlert: true, cancellationToken: cancellationToken); return; }

            int energyBonus = months switch { 1 => 100, 3 => 350, 6 => 1000, _ => 0 };
            DateTime baseDate = sub.ExpiryDate > DateTime.UtcNow ? sub.ExpiryDate.Value : DateTime.UtcNow;
            sub.ExpiryDate = baseDate.AddDays(months * 30);
            sub.SyncStatus = SyncStatus.PendingUpdate;

            using var scope = _scopeFactory.CreateScope();
            var gameService = scope.ServiceProvider.GetRequiredService<GameService>();
            await gameService.AddEnergyAsync(targetId, energyBonus, cancellationToken);

            dbContext.SupportMessages.Add(new SupportMessage { TelegramId = targetId, Text = $"✅ Ваша подписка продлена на {months} мес!\n⚡ Энергия пополнена: +{energyBonus}", IsFromAdmin = true, IsRead = false, CreatedAt = DateTime.UtcNow });
            await dbContext.SaveChangesAsync(cancellationToken);

            await botClient.SendMessage(chatId: targetId, text: "✅ *Подписка продлена!*", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
            await botClient.DeleteMessage(chatId, callbackQuery.Message!.MessageId, cancellationToken);
            await botClient.AnswerCallbackQuery(callbackQuery.Id, "Успешно!", cancellationToken: cancellationToken);
        }
    }

    private async Task HandleEnergyChargeAsync(ITelegramBotClient botClient, long chatId, string data, CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        string prefix = data.StartsWith("e1000_") ? "e1000_" : (data.StartsWith("e300_") ? "e300_" : "e100_");
        int energyBonus = int.Parse(prefix.Replace("e", "").Replace("_", ""));

        if (!long.TryParse(data.Replace(prefix, ""), out long targetId)) return;

        using var scope = _scopeFactory.CreateScope();
        var gameService = scope.ServiceProvider.GetRequiredService<GameService>();
        var result = await gameService.AddEnergyAsync(targetId, energyBonus, cancellationToken);

        if (result.Success)
        {
            using var scope2 = _scopeFactory.CreateScope();
            var dbContext = scope2.ServiceProvider.GetRequiredService<VpnDbContext>();
            dbContext.SupportMessages.Add(new SupportMessage { TelegramId = targetId, Text = $"✅ Энергоблок активирован!\n⚡ Начислено: +{energyBonus}", IsFromAdmin = true, IsRead = false, CreatedAt = DateTime.UtcNow });
            await dbContext.SaveChangesAsync(cancellationToken);

            await botClient.SendMessage(chatId: targetId, text: $"✅ *Энергия зачислена!*", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
            await botClient.DeleteMessage(chatId, callbackQuery.Message!.MessageId, cancellationToken);
            await botClient.AnswerCallbackQuery(callbackQuery.Id, "Выдано!", cancellationToken: cancellationToken);
        }
    }

    private async Task<TelegramUser> GetOrCreateUserAsync(VpnDbContext dbContext, User user, CancellationToken ct)
    {
        var dbUser = await dbContext.TelegramUsers.FirstOrDefaultAsync(u => u.TelegramId == user.Id, ct);
        if (dbUser == null)
        {
            dbUser = new TelegramUser { TelegramId = user.Id, FirstName = user.FirstName, LanguageCode = user.LanguageCode ?? "ru" };
            dbContext.TelegramUsers.Add(dbUser);
            await dbContext.SaveChangesAsync(ct);
        }
        else if (dbUser.FirstName != user.FirstName)
        {
            dbUser.FirstName = user.FirstName;
            await dbContext.SaveChangesAsync(ct);
        }
        return dbUser;
    }

    public Task HandleErrorAsync(ITelegramBotClient b, Exception e, HandleErrorSource s, CancellationToken c) => Task.CompletedTask;
}
