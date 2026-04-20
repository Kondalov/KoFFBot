using DotNetEnv;
using KoFFBot.Data;
using KoFFBot.Domain;
using KoFFBot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Collections.Concurrent;

namespace KoFFBot;

public static class Program
{
    private static readonly ConcurrentDictionary<long, DateTime> _spamFilter = new();

    public static void Main(string[] args)
    {
        BotLogger.Log("SYSTEM", "============= ЗАПУСК KoFFBot =============");

        Env.Load();
        string? botToken = Environment.GetEnvironmentVariable("BOT_TOKEN")?.Trim('"', '\'', ' ');
        string? apiSecret = Environment.GetEnvironmentVariable("API_SECRET")?.Trim('"', '\'', ' ');
        string? adminIdStr = Environment.GetEnvironmentVariable("ADMIN_TG_ID")?.Trim('"', '\'', ' ');

        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(apiSecret))
        {
            BotLogger.Log("SYSTEM", "КРИТИЧЕСКАЯ ОШИБКА: Не найден BOT_TOKEN или API_SECRET!");
            return;
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);

        builder.WebHost.ConfigureKestrel(serverOptions => { serverOptions.ListenAnyIP(5000); });
        builder.Services.AddCors(options => { options.AddPolicy("AllowWebApp", policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()); });

        string dbPath = Path.Combine(AppContext.BaseDirectory, "koffbot_data.db");
        builder.Services.AddDbContext<BotDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));

        builder.Services.AddSingleton<ITelegramBotClient>(sp => new TelegramBotClient(botToken));
        builder.Services.AddTransient<SubscriptionGenerator>();
        builder.Services.AddScoped<GameService>();
        builder.Services.AddSingleton<UpdateHandler>();
        builder.Services.AddHostedService<TelegramBotWorker>();

        var app = builder.Build();

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            db.Database.EnsureCreated();
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");

            string createTablesSql = @"
                CREATE TABLE IF NOT EXISTS ""GameProfiles"" (
                    ""TelegramId"" INTEGER NOT NULL CONSTRAINT ""PK_GameProfiles"" PRIMARY KEY,
                    ""CurrentEnergy"" INTEGER NOT NULL,
                    ""LastEnergyUpdate"" TEXT NOT NULL,
                    ""EnergySignature"" TEXT NOT NULL,
                    ""IsBanned"" INTEGER NOT NULL,
                    ""BanReason"" TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS ""LeaderboardRecords"" (
                    ""TelegramId"" INTEGER NOT NULL CONSTRAINT ""PK_LeaderboardRecords"" PRIMARY KEY,
                    ""MaxScore"" INTEGER NOT NULL,
                    ""AchievedAt"" TEXT NOT NULL,
                    ""ScoreSignature"" TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ""IX_LeaderboardRecords_MaxScore"" ON ""LeaderboardRecords"" (""MaxScore"" DESC);
            ";
            db.Database.ExecuteSqlRaw(createTablesSql);

            // === ИСПРАВЛЕНИЕ: Безопасное добавление колонки BossKills ===
            try { db.Database.ExecuteSqlRaw("ALTER TABLE \"GameProfiles\" ADD COLUMN \"BossKills\" INTEGER NOT NULL DEFAULT 0;"); } catch { }
            try { db.Database.ExecuteSqlRaw("ALTER TABLE \"GameProfiles\" ADD COLUMN \"MonthlyBossKills\" INTEGER NOT NULL DEFAULT 0;"); } catch { }
            try { db.Database.ExecuteSqlRaw("ALTER TABLE \"GameProfiles\" ADD COLUMN \"LastBossKillDate\" TEXT NOT NULL DEFAULT '2000-01-01 00:00:00';"); } catch { }
        }

        app.Use(async (context, next) =>
        {
            BotLogger.Log("HTTP-SPY", $"[ВХОДЯЩИЙ] {context.Request.Method} {context.Request.Path}");
            try { await next(context); }
            catch (Exception ex)
            {
                BotLogger.Log("HTTP-SPY-CRITICAL", $"[КРИТИЧЕСКОЕ ПАДЕНИЕ]\nПУТЬ: {context.Request.Path}\nОШИБКА: {ex.Message}\n{ex.StackTrace}");
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync($"server_error: {ex.Message}");
            }
        });

        app.UseCors("AllowWebApp");
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value?.ToLower() ?? "";
            if (path.StartsWith("/api/webapp") || path.StartsWith("/api/game") || path.EndsWith(".html") || path.EndsWith(".js") || path.EndsWith(".css") || path == "/")
            {
                await next(context);
                return;
            }

            string safeSecret = apiSecret?.Trim('\r', '\n', ' ') ?? "";
            if (!context.Request.Headers.TryGetValue("X-API-KEY", out var extractedApiKey) || extractedApiKey.ToString().Trim('\r', '\n', ' ') != safeSecret)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Доступ запрещен.");
                return;
            }
            await next(context);
        });

        app.MapGet("/api/sync/pending", async (BotDbContext db) => Results.Ok(await db.VpnSubscriptions.Where(s => s.SyncStatus == SyncStatus.PendingAdd || s.SyncStatus == SyncStatus.PendingUpdate).ToListAsync()));
        app.MapPost("/api/sync/commit", async (CommitRequestDto request, BotDbContext db) => { var subs = await db.VpnSubscriptions.Where(s => request.Uuids.Contains(s.Uuid)).ToListAsync(); foreach (var sub in subs) sub.SyncStatus = SyncStatus.Synced; await db.SaveChangesAsync(); return Results.Ok(new { message = $"Синхронизировано {subs.Count} клиентов" }); });
        app.MapPost("/api/sync/traffic", async (List<TrafficSyncDto> trafficData, BotDbContext db) => { if (trafficData == null || !trafficData.Any()) return Results.Ok(); var uuids = trafficData.Select(t => t.Uuid).ToList(); var dbSubs = await db.VpnSubscriptions.Where(s => uuids.Contains(s.Uuid)).ToListAsync(); int updatedCount = 0; foreach (var incoming in trafficData) { var sub = dbSubs.FirstOrDefault(s => s.Uuid == incoming.Uuid); if (sub != null) { sub.TrafficLimitBytes = incoming.TrafficLimitBytes; sub.TrafficUsedBytes = incoming.TrafficUsedBytes; updatedCount++; } } if (updatedCount > 0) { await db.SaveChangesAsync(); } return Results.Ok(); });
        app.MapPost("/api/templates", async (ServerTemplate template, BotDbContext db) => { var existing = await db.ServerTemplates.FirstOrDefaultAsync(t => t.ServerIp == template.ServerIp); if (existing != null) { existing.CoreType = template.CoreType; existing.InboundsConfigJson = template.InboundsConfigJson; } else db.ServerTemplates.Add(template); await db.SaveChangesAsync(); return Results.Ok(); });
        app.MapPost("/api/legacy/sync", async (List<LegacyUserDto> legacyUsers, BotDbContext db) => { foreach (var user in legacyUsers) { var existing = await db.VpnSubscriptions.FirstOrDefaultAsync(s => s.Uuid == user.Uuid); if (existing == null) db.VpnSubscriptions.Add(new VpnSubscription { Uuid = user.Uuid, Email = user.Email, ServerIp = user.ServerIp, TrafficLimitBytes = user.TrafficLimitBytes, IsActive = true, SyncStatus = SyncStatus.Synced, TelegramId = 0 }); } await db.SaveChangesAsync(); return Results.Ok(); });
        app.MapGet("/api/stats", async (BotDbContext db) => Results.Ok(new { TotalUsers = await db.TelegramUsers.CountAsync() }));
        app.MapPost("/api/sync/pool", async (List<ReserveKeyDto> keys, BotDbContext db) => { var ghostKeys = await db.VpnSubscriptions.Where(s => s.TelegramId == 0 && s.Email.StartsWith("reserve_")).ToListAsync(); db.VpnSubscriptions.RemoveRange(ghostKeys); foreach (var k in keys) { db.VpnSubscriptions.Add(new VpnSubscription { Uuid = k.Uuid, TelegramId = 0, Email = $"reserve_{k.Uuid.Substring(0, 5)}", ServerIp = k.ServerIp, TrafficLimitBytes = k.TrafficLimitBytes, TrafficUsedBytes = 0, IsActive = true, MaxDevices = 2, ExpiryDate = DateTime.UtcNow.AddDays(3), SyncStatus = SyncStatus.Synced, LastModifiedAt = DateTime.UtcNow }); } await db.SaveChangesAsync(); return Results.Ok(); });
        app.MapGet("/api/sync/pool/count", async (BotDbContext db) => Results.Ok(new { ReserveCount = await db.VpnSubscriptions.CountAsync(s => s.TelegramId == 0 && s.Email.StartsWith("reserve_") && s.IsActive) }));

        app.MapGet("/api/webapp/profile", async (long tgId, BotDbContext db) => {
            var user = await db.TelegramUsers.FirstOrDefaultAsync(u => u.TelegramId == tgId);
            var sub = await db.VpnSubscriptions.FirstOrDefaultAsync(s => s.TelegramId == tgId && s.IsActive);
            if (user == null) return Results.NotFound("Пользователь не найден");

            // Проверяем, является ли пользователь админом
            bool isAdmin = long.TryParse(adminIdStr, out long adminId) && tgId == adminId;

            // ПРАВИЛО 238: Достаем секрет для античита строго из .env!
            string gameSecret = Environment.GetEnvironmentVariable("ANTI_CHEAT_SECRET") ?? "";

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
                GameSecret = gameSecret // Передаем в UI только в оперативной памяти
            });
        });

        app.MapPost("/api/webapp/generate", async (WebAppActionRequest req, BotDbContext db, SubscriptionGenerator generator) => { var user = await db.TelegramUsers.FirstOrDefaultAsync(u => u.TelegramId == req.TelegramId); if (user == null) return Results.BadRequest(); var existingSub = await db.VpnSubscriptions.FirstOrDefaultAsync(s => s.TelegramId == req.TelegramId && s.IsActive); if (existingSub != null) return Results.Ok(); int reserveCount = await db.VpnSubscriptions.CountAsync(s => s.TelegramId == 0 && s.IsActive); if (reserveCount == 0) return Results.Problem("Нет резервных серверов."); var (success, uuid, serverIp, err) = await generator.GenerateNewSubscriptionAsync(req.TelegramId, $"tg_{req.TelegramId}", CancellationToken.None); if (!success) return Results.Problem(err); return Results.Ok(); });
        app.MapGet("/api/webapp/inbox", async (long tgId, BotDbContext db) => { var msgs = await db.SupportMessages.Where(m => m.TelegramId == tgId).OrderBy(m => m.CreatedAt).ToListAsync(); var unread = msgs.Where(m => m.IsFromAdmin && !m.IsRead).ToList(); foreach (var u in unread) u.IsRead = true; if (unread.Any()) await db.SaveChangesAsync(); return Results.Ok(msgs.Select(m => new { m.Id, m.Text, m.IsFromAdmin, CreatedAt = m.CreatedAt.ToString("HH:mm dd.MM") })); });
        app.MapGet("/api/webapp/inbox/unread", async (long tgId, BotDbContext db) => { return Results.Ok(new { UnreadCount = await db.SupportMessages.CountAsync(m => m.TelegramId == tgId && m.IsFromAdmin && !m.IsRead) }); });
        app.MapPost("/api/webapp/buy", async (BuyRequest req, BotDbContext db, ITelegramBotClient bot) => { var user = await db.TelegramUsers.FirstOrDefaultAsync(u => u.TelegramId == req.TelegramId); if (user == null) return Results.BadRequest(); db.SupportMessages.Add(new SupportMessage { TelegramId = req.TelegramId, Text = $"🛒 Заявка отправлена: {req.TariffName}\nОжидайте ответа администратора с реквизитами.", IsFromAdmin = false, IsRead = true, CreatedAt = DateTime.UtcNow }); await db.SaveChangesAsync(); if (long.TryParse(adminIdStr, out long adminId)) { var kb = new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("↩️ Ответить", $"reply_{req.TelegramId}"), InlineKeyboardButton.WithCallbackData("💰 Продлить", $"renew_{req.TelegramId}") }, new[] { InlineKeyboardButton.WithCallbackData("💳 Мои реквизиты", $"req_{req.TelegramId}"), InlineKeyboardButton.WithCallbackData("🙈 Скрыть", $"hide_{req.TelegramId}") } }); string safeName = System.Net.WebUtility.HtmlEncode(user.FirstName ?? "Без имени"); string adminText = $"🛒 <b>ЗАЯВКА НА ТАРИФ</b>\nОт: {safeName}\nID: <code>{req.TelegramId}</code>\nТариф: <b>{req.TariffName}</b>"; try { await bot.SendMessage(chatId: adminId, text: adminText, parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, replyMarkup: kb); } catch { } } return Results.Ok(); });
        app.MapPost("/api/webapp/send_message", async (UserMessageRequest req, BotDbContext db, ITelegramBotClient bot) => { var user = await db.TelegramUsers.FirstOrDefaultAsync(u => u.TelegramId == req.TelegramId); if (user == null || string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(); if (req.Text.Length > 500) return Results.BadRequest("Слишком длинное сообщение."); if (_spamFilter.TryGetValue(req.TelegramId, out DateTime lastMsg) && (DateTime.UtcNow - lastMsg).TotalSeconds < 10) return Results.BadRequest("Пожалуйста, подождите 10 секунд перед отправкой следующего сообщения."); _spamFilter[req.TelegramId] = DateTime.UtcNow; db.SupportMessages.Add(new SupportMessage { TelegramId = req.TelegramId, Text = req.Text, IsFromAdmin = false, IsRead = true, CreatedAt = DateTime.UtcNow }); await db.SaveChangesAsync(); if (long.TryParse(adminIdStr, out long adminId)) { var kb = new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("↩️ Ответить", $"reply_{req.TelegramId}"), InlineKeyboardButton.WithCallbackData("💰 Продлить", $"renew_{req.TelegramId}") }, new[] { InlineKeyboardButton.WithCallbackData("💳 Мои реквизиты", $"req_{req.TelegramId}"), InlineKeyboardButton.WithCallbackData("🙈 Скрыть", $"hide_{req.TelegramId}") } }); string safeName = System.Net.WebUtility.HtmlEncode(user.FirstName ?? "Без имени"); string safeText = System.Net.WebUtility.HtmlEncode(req.Text); string adminText = $"💬 <b>НОВОЕ СООБЩЕНИЕ</b>\nОт: {safeName}\nID: <code>{req.TelegramId}</code>\n\nТекст: {safeText}"; try { await bot.SendMessage(chatId: adminId, text: adminText, parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, replyMarkup: kb); } catch { } } return Results.Ok(); });

        app.MapGet("/api/game/profile", async (long tgId, GameService gameService) => {
            var profile = await gameService.GetOrCreateProfileAsync(tgId, CancellationToken.None);
            if (profile.IsBanned) return Results.BadRequest("Аккаунт заблокирован.");

            // Сброс месяца при загрузке профиля (если месяц прошел)
            if (profile.LastBossKillDate.Month != DateTime.UtcNow.Month || profile.LastBossKillDate.Year != DateTime.UtcNow.Year)
            {
                profile.MonthlyBossKills = 0;
            }

            return Results.Ok(new
            {
                Energy = profile.CurrentEnergy,
                IsBanned = profile.IsBanned,
                BossKills = profile.BossKills,
                MonthlyBossKills = profile.MonthlyBossKills // Передаем лимит на фронт
            });
        });
        app.MapPost("/api/game/start", async (GameActionRequest req, GameService gameService) => { var result = await gameService.TryStartGameAsync(req.TelegramId, CancellationToken.None); if (!result.Success) return Results.BadRequest(result.Message); return Results.Ok(new { Message = result.Message, RemainingEnergy = result.RemainingEnergy }); });
        app.MapPost("/api/game/submit", async (GameScoreRequest req, GameService gameService) => { var result = await gameService.SubmitScoreAsync(req.TelegramId, req.Score, req.Signature, CancellationToken.None); if (!result.Success) return Results.BadRequest(result.Message); return Results.Ok(new { Message = result.Message }); });
        app.MapPost("/api/game/boss_victory", async (GameActionRequest req, GameService gameService) => { var result = await gameService.ProcessBossVictoryAsync(req.TelegramId, req.Signature, CancellationToken.None); if (!result.Success) return Results.BadRequest(result.Message); return Results.Ok(new { Message = result.Message }); });
        app.MapPost("/api/game/cheat", async (GameActionRequest req, GameService gameService) => { if (!long.TryParse(adminIdStr, out long adminId) || req.TelegramId != adminId) return Results.BadRequest("У вас нет прав."); var result = await gameService.AddCheatEnergyAsync(req.TelegramId, 50, CancellationToken.None); return Results.Ok(new { Message = result.Message, NewEnergy = result.NewEnergy }); });

        app.MapGet("/api/game/leaderboard", async (GameService gameService) =>
        {
            var result = await gameService.GetLeaderboardAsync(CancellationToken.None);
            return Results.Ok(result);
        });

        app.Run();
    }
}

public class LegacyUserDto { public string Uuid { get; set; } = ""; public string Email { get; set; } = ""; public string ServerIp { get; set; } = ""; public long TrafficLimitBytes { get; set; } }
public class CommitRequestDto { public List<string> Uuids { get; set; } = new(); }
public class WebAppActionRequest { public long TelegramId { get; set; } public string Action { get; set; } = ""; }
public class ReserveCountDto { public int ReserveCount { get; set; } }
public class ReserveKeyDto { public string Uuid { get; set; } = ""; public string ServerIp { get; set; } = ""; public long TrafficLimitBytes { get; set; } }
public class BuyRequest { public long TelegramId { get; set; } public string TariffName { get; set; } = ""; }
public class UserMessageRequest { public long TelegramId { get; set; } public string Text { get; set; } = ""; }
public class GameActionRequest { public long TelegramId { get; set; } public string Signature { get; set; } = string.Empty; }
public class GameScoreRequest { public long TelegramId { get; set; } public long Score { get; set; } public string Signature { get; set; } = string.Empty; }
public class TrafficSyncDto { public string Uuid { get; set; } = ""; public long TrafficUsedBytes { get; set; } public long TrafficLimitBytes { get; set; } }