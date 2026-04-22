using KoFFBot.Contracts;
using KoFFBot.Data;
using KoFFBot.Domain;
using KoFFBot.Services;
using KoFFBot.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading;
using System.Collections.Concurrent;
using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;
using Serilog;

namespace KoFFBot.Endpoints;

public static class WebAppEndpoints
{
    private static readonly ConcurrentDictionary<long, DateTime> _spamFilter = new();

    public static void MapWebAppEndpoints(this IEndpointRouteBuilder app, string? adminIdStr)
    {
        app.MapGet("/api/webapp/profile", async (long tgId, VpnDbContext db) => {
            var user = await db.TelegramUsers.FirstOrDefaultAsync(u => u.TelegramId == tgId);
            var sub = await db.VpnSubscriptions.FirstOrDefaultAsync(s => s.TelegramId == tgId && s.IsActive);
            if (user == null) return Results.NotFound("Пользователь не найден");

            bool isAdmin = long.TryParse(adminIdStr, out long adminId) && tgId == adminId;

            return Results.Ok(new
            {
                TelegramId = user.TelegramId,
                FirstName = user.FirstName,
                ReferralCount = user.ReferralCount,
                HasSubscription = sub != null,
                TrafficLimit = sub?.TrafficLimitBytes ?? 0,
                TrafficUsed = sub?.TrafficUsedBytes ?? 0,
                ServerIp = sub?.ServerIp ?? "",
                Uuid = sub?.Uuid ?? "",
                ExpiryDate = sub?.ExpiryDate,
                IsAdmin = isAdmin,
                GameSecret = ""
            });
        });

        app.MapPost("/api/webapp/generate", async (WebAppActionRequest req, VpnDbContext db, SubscriptionGenerator generator) => { 
            var user = await db.TelegramUsers.FirstOrDefaultAsync(u => u.TelegramId == req.TelegramId); 
            if (user == null) return Results.BadRequest(); 
            var existingSub = await db.VpnSubscriptions.FirstOrDefaultAsync(s => s.TelegramId == req.TelegramId && s.IsActive); 
            if (existingSub != null) return Results.Ok(); 
            int reserveCount = await db.VpnSubscriptions.CountAsync(s => s.TelegramId == 0 && s.IsActive); 
            if (reserveCount == 0) return Results.Problem("Нет резервных серверов."); 
            var (success, uuid, serverIp, err) = await generator.GenerateNewSubscriptionAsync(req.TelegramId, $"tg_{req.TelegramId}", CancellationToken.None); 
            if (!success) return Results.Problem(err); 
            return Results.Ok(); 
        });
        
        app.MapGet("/api/webapp/inbox", async (long tgId, VpnDbContext db) => { 
            var msgs = await db.SupportMessages.Where(m => m.TelegramId == tgId).OrderBy(m => m.CreatedAt).ToListAsync(); 
            var unread = msgs.Where(m => m.IsFromAdmin && !m.IsRead).ToList(); 
            foreach (var u in unread) u.IsRead = true; 
            if (unread.Any()) await db.SaveChangesAsync(); 
            return Results.Ok(msgs.Select(m => new { m.Id, m.Text, m.IsFromAdmin, CreatedAt = m.CreatedAt.ToString("HH:mm dd.MM") })); 
        });
        
        app.MapGet("/api/webapp/inbox/unread", async (long tgId, VpnDbContext db) => { 
            return Results.Ok(new { UnreadCount = await db.SupportMessages.CountAsync(m => m.TelegramId == tgId && m.IsFromAdmin && !m.IsRead) }); 
        });

        app.MapPost("/api/webapp/buy", async (BuyRequest req, VpnDbContext db, ITelegramBotClient bot) => {
            var user = await db.TelegramUsers.FirstOrDefaultAsync(u => u.TelegramId == req.TelegramId);
            if (user == null) return Results.BadRequest();
            db.SupportMessages.Add(new SupportMessage { TelegramId = req.TelegramId, Text = $"🛒 Заявка отправлена: {req.TariffName}\nОжидайте ответа администратора с реквизитами.", IsFromAdmin = false, IsRead = true, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();

            if (long.TryParse(adminIdStr, out long adminId))
            {
                var kb = new InlineKeyboardMarkup(new[] { 
                    new[] { InlineKeyboardButton.WithCallbackData("↩️ Ответить", $"reply_{req.TelegramId}"), InlineKeyboardButton.WithCallbackData("💰 Продлить", $"renew_{req.TelegramId}") }, 
                    new[] { InlineKeyboardButton.WithCallbackData("💳 Мои реквизиты", $"req_{req.TelegramId}"), InlineKeyboardButton.WithCallbackData("🙈 Скрыть", $"hide_{req.TelegramId}") } 
                });
                string safeName = System.Net.WebUtility.HtmlEncode(user.FirstName ?? "Без имени");
                string adminText = $"🛒 <b>ЗАЯВКА НА ТАРИФ</b>\nОт: {safeName}\nID: <code>{req.TelegramId}</code>\nТариф: <b>{req.TariffName}</b>";
                try { await bot.SendMessage(chatId: adminId, text: adminText, parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, replyMarkup: kb); } 
                catch (Exception ex) { Log.Error(ex, "Ошибка отправки в ТГ"); }
            }
            return Results.Ok();
        });

        app.MapPost("/api/webapp/send_message", async (UserMessageRequest req, VpnDbContext db, ITelegramBotClient bot) => {
            var user = await db.TelegramUsers.FirstOrDefaultAsync(u => u.TelegramId == req.TelegramId);
            if (user == null || string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest();
            if (req.Text.Length > 500) return Results.BadRequest("Слишком длинное сообщение.");
            if (_spamFilter.TryGetValue(req.TelegramId, out DateTime lastMsg) && (DateTime.UtcNow - lastMsg).TotalSeconds < 10) return Results.BadRequest("Пожалуйста, подождите 10 секунд перед отправкой следующего сообщения.");
            _spamFilter[req.TelegramId] = DateTime.UtcNow;
            db.SupportMessages.Add(new SupportMessage { TelegramId = req.TelegramId, Text = req.Text, IsFromAdmin = false, IsRead = true, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();

            if (long.TryParse(adminIdStr, out long adminId))
            {
                var kb = new InlineKeyboardMarkup(new[] { 
                    new[] { InlineKeyboardButton.WithCallbackData("↩️ Ответить", $"reply_{req.TelegramId}"), InlineKeyboardButton.WithCallbackData("💰 Продлить", $"renew_{req.TelegramId}") }, 
                    new[] { InlineKeyboardButton.WithCallbackData("💳 Мои реквизиты", $"req_{req.TelegramId}"), InlineKeyboardButton.WithCallbackData("🙈 Скрыть", $"hide_{req.TelegramId}") } 
                });
                string safeName = System.Net.WebUtility.HtmlEncode(user.FirstName ?? "Без имени");
                string safeText = System.Net.WebUtility.HtmlEncode(req.Text);
                string adminText = $"💬 <b>НОВОЕ СООБЩЕНИЕ</b>\nОт: {safeName}\nID: <code>{req.TelegramId}</code>\n\nТекст: {safeText}";
                try { await bot.SendMessage(chatId: adminId, text: adminText, parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, replyMarkup: kb); } 
                catch (Exception ex) { Log.Error(ex, "Ошибка отправки в ТГ"); }
            }
            return Results.Ok();
        });
    }
}