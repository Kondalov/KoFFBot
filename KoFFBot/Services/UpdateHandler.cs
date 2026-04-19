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

namespace KoFFBot.Services;

public class UpdateHandler : IUpdateHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private static readonly ConcurrentDictionary<long, string> _userStates = new();

    // ИСПРАВЛЕНИЕ АРХИТЕКТУРЫ: Передаем фабрику, чтобы создавать изолированные потоки БД
    public UpdateHandler(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        BotLogger.Log("DEEP-TRACE", $"=== НОВЫЙ АПДЕЙТ ОТ TELEGRAM: {update.Type} ===");

        // МАГИЯ ЗДЕСЬ: Создаем изолированный контекст БД для КАЖДОГО запроса
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var subscriptionGenerator = scope.ServiceProvider.GetRequiredService<SubscriptionGenerator>();

        try
        {
            if (update.Type == UpdateType.Message && update.Message != null)
                await ProcessMessageAsync(botClient, update.Message, dbContext, cancellationToken);
            else if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery != null)
                await ProcessCallbackQueryAsync(botClient, update.CallbackQuery, dbContext, cancellationToken);
        }
        catch (Exception ex)
        {
            BotLogger.Log("UPDATE-CRITICAL", "Критическая ошибка при обработке Telegram Update!", ex);
        }
    }

    private async Task ProcessMessageAsync(ITelegramBotClient botClient, Message message, BotDbContext dbContext, CancellationToken cancellationToken)
    {
        if (message.Text == null || message.From == null) return;
        var user = message.From;

        BotLogger.Log("DEEP-TRACE", $"[ТЕКСТ] Пользователь {user.Id} пишет: {message.Text}");

        // ПРОВЕРКА СОСТОЯНИЯ: Ожидание текста ответа от Админа
        if (_userStates.TryGetValue(user.Id, out var state) && state.StartsWith("wait_admin_reply_"))
        {
            BotLogger.Log("ADMIN-REPLY", $"Перехват ответа админа. Состояние: {state}");
            if (long.TryParse(state.Replace("wait_admin_reply_", ""), out long targetId))
            {
                BotLogger.Log("ADMIN-REPLY", $"Сохранение ответа для юзера {targetId} в БД...");
                dbContext.SupportMessages.Add(new SupportMessage { TelegramId = targetId, Text = message.Text, IsFromAdmin = true, IsRead = false, CreatedAt = DateTime.UtcNow });
                await dbContext.SaveChangesAsync(cancellationToken);

                await botClient.SendMessage(chatId: targetId, text: "💬 *Новое сообщение от поддержки!*\nОткройте приложение, чтобы прочитать.", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                await botClient.SendMessage(chatId: user.Id, text: $"✅ Ответ успешно отправлен пользователю.", cancellationToken: cancellationToken);

                _userStates.TryRemove(user.Id, out _);
                BotLogger.Log("ADMIN-REPLY", $"Ответ успешно доставлен.");
                return;
            }
        }

        long? referrerId = null;
        if (message.Text.StartsWith("/start "))
        {
            var parts = message.Text.Split(' ');
            if (parts.Length > 1 && long.TryParse(parts[1], out long refId) && refId != user.Id) referrerId = refId;
        }

        var dbUser = await GetOrCreateUserAsync(user, referrerId, dbContext, cancellationToken);

        if (message.Text.StartsWith("/start"))
        {
            var buttons = new List<InlineKeyboardButton[]> { new[] { InlineKeyboardButton.WithWebApp("🌌 Открыть KoFFPanel", new WebAppInfo { Url = "https://8c82691684a8f0.lhr.life" }) } }; // ВСТАВЬ СВОЮ ССЫЛКУ
            await botClient.SendMessage(chatId: message.Chat.Id, text: "Добро пожаловать в KoFFPanel ⚡️\nНажмите кнопку ниже, чтобы открыть приложение.", replyMarkup: new InlineKeyboardMarkup(buttons), cancellationToken: cancellationToken);
        }
    }

    private async Task ProcessCallbackQueryAsync(ITelegramBotClient botClient, CallbackQuery callbackQuery, BotDbContext dbContext, CancellationToken cancellationToken)
    {
        var user = callbackQuery.From;
        var chatId = callbackQuery.Message!.Chat.Id;
        string data = callbackQuery.Data ?? "";

        BotLogger.Log("DEEP-TRACE", $"[КНОПКА] Админ {user.Id} нажал кнопку: '{data}'");

        try
        {
            if (data.StartsWith("hide_"))
            {
                BotLogger.Log("ACTION", $"Удаление сообщения в чате {chatId}");
                await botClient.DeleteMessage(chatId, callbackQuery.Message.MessageId, cancellationToken);
                return;
            }
            else if (data.StartsWith("reply_"))
            {
                string targetId = data.Replace("reply_", "");
                BotLogger.Log("ACTION", $"Включение режима 'Ожидание ответа' для админа на ID: {targetId}");
                _userStates[user.Id] = $"wait_admin_reply_{targetId}";
                await botClient.SendMessage(chatId, $"Напишите ответ для пользователя `{targetId}`. Следующее ваше сообщение будет отправлено ему.", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                await botClient.AnswerCallbackQuery(callbackQuery.Id, "Ожидаю текст...", cancellationToken: cancellationToken);
                return;
            }
            else if (data.StartsWith("req_"))
            {
                BotLogger.Log("ACTION", $"Отправка реквизитов. Парсинг ID: {data}");
                if (long.TryParse(data.Replace("req_", ""), out long targetId))
                {
                    string requisites = "Здравствуйте! Оплата по номеру телефона: 8 909 01 00 473 (Сбербанк).\nПосле оплаты отправьте скриншот чека сюда.";

                    BotLogger.Log("ACTION", $"Запись реквизитов в БД для юзера {targetId}");
                    dbContext.SupportMessages.Add(new SupportMessage { TelegramId = targetId, Text = requisites, IsFromAdmin = true, IsRead = false, CreatedAt = DateTime.UtcNow });
                    await dbContext.SaveChangesAsync(cancellationToken);

                    BotLogger.Log("ACTION", $"Уведомление в ТГ юзера {targetId}");
                    await botClient.SendMessage(chatId: targetId, text: "💳 *Вам отправлены реквизиты!*\nОткройте Инбокс в приложении.", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                    await botClient.AnswerCallbackQuery(callbackQuery.Id, "Реквизиты отправлены!", cancellationToken: cancellationToken);
                }
                else { BotLogger.Log("ERROR", $"Не удалось распарсить ID из {data}"); }
                return;
            }
            else if (data.StartsWith("renew_"))
            {
                string targetId = data.Replace("renew_", "");
                BotLogger.Log("ACTION", $"Смена клавиатуры на тарифы для ID: {targetId}");
                var kb = new InlineKeyboardMarkup(new[] {
                    new[] { InlineKeyboardButton.WithCallbackData("1 Месяц", $"t1_{targetId}"), InlineKeyboardButton.WithCallbackData("3 Месяца", $"t3_{targetId}") },
                    new[] { InlineKeyboardButton.WithCallbackData("6 Месяцев", $"t6_{targetId}"), InlineKeyboardButton.WithCallbackData("❌ Отмена", $"hide_{targetId}") }
                });
                await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: kb, cancellationToken: cancellationToken);
                return;
            }
            else if (data.StartsWith("t1_") || data.StartsWith("t3_") || data.StartsWith("t6_"))
            {
                int months = data.StartsWith("t1_") ? 1 : (data.StartsWith("t3_") ? 3 : 6);
                BotLogger.Log("ACTION", $"Запрос на продление на {months} мес. Data: {data}");

                if (long.TryParse(data.Substring(3), out long targetId))
                {
                    var sub = await dbContext.VpnSubscriptions.FirstOrDefaultAsync(s => s.TelegramId == targetId && s.IsActive, cancellationToken);
                    if (sub != null)
                    {
                        DateTime baseDate = (sub.ExpiryDate.HasValue && sub.ExpiryDate.Value > DateTime.UtcNow) ? sub.ExpiryDate.Value : DateTime.UtcNow;
                        sub.ExpiryDate = baseDate.AddDays(months * 30);
                        sub.SyncStatus = SyncStatus.PendingUpdate;

                        BotLogger.Log("ACTION", $"Обновление ключа в БД до {sub.ExpiryDate.Value:dd.MM.yyyy}");
                        dbContext.SupportMessages.Add(new SupportMessage { TelegramId = targetId, Text = $"✅ Ваша подписка успешно продлена на {months} мес!\nНовый срок: {sub.ExpiryDate.Value:dd.MM.yyyy}", IsFromAdmin = true, IsRead = false, CreatedAt = DateTime.UtcNow });

                        var referral = await dbContext.Referrals.FirstOrDefaultAsync(r => r.InvitedTelegramId == targetId && !r.IsActivated, cancellationToken);
                        if (referral != null) referral.IsActivated = true;

                        await dbContext.SaveChangesAsync(cancellationToken);

                        await botClient.SendMessage(chatId: targetId, text: "✅ *Подписка продлена!*\nПроверьте приложение.", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                        await botClient.DeleteMessage(chatId, callbackQuery.Message.MessageId, cancellationToken);
                        await botClient.AnswerCallbackQuery(callbackQuery.Id, "Продлено успешно!", cancellationToken: cancellationToken);
                        BotLogger.Log("ACTION", $"Продление успешно завершено.");
                    }
                    else
                    {
                        BotLogger.Log("ERROR", $"Активный ключ для {targetId} не найден в БД!");
                        await botClient.AnswerCallbackQuery(callbackQuery.Id, "Ошибка: Активный ключ не найден.", showAlert: true, cancellationToken: cancellationToken);
                    }
                }
                return;
            }

            await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            BotLogger.Log("CALLBACK-CRITICAL", $"Ошибка при обработке кнопки {data}", ex);
        }
    }

    private async Task<TelegramUser> GetOrCreateUserAsync(User user, long? referrerId, BotDbContext dbContext, CancellationToken ct)
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