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
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace KoFFBot;

public static class Program
{
    public static void Main(string[] args)
    {
        BotLogger.Log("SYSTEM", "============= ЗАПУСК KoFFBot =============");

        Env.Load();
        string? botToken = Environment.GetEnvironmentVariable("BOT_TOKEN")?.Trim('"', '\'', ' ');
        string? apiSecret = Environment.GetEnvironmentVariable("API_SECRET")?.Trim('"', '\'', ' ');

        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(apiSecret))
        {
            BotLogger.Log("SYSTEM", "КРИТИЧЕСКАЯ ОШИБКА: Не найден BOT_TOKEN или API_SECRET!");
            return;
        }

        var builder = WebApplication.CreateBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Trace); // Trace - это самый глубокий уровень из возможных

        // Включаем встроенный шпион ASP.NET Core (пишет все заголовки и пути)
        builder.Services.AddHttpLogging(logging =>
        {
            logging.LoggingFields = Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.All;
        });

        // Настройка Kestrel
        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.ListenAnyIP(5000);
        });

        builder.Services.AddCors(options =>
        {
            options.AddPolicy("AllowWebApp", policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
        });

        // 4. Подключение БД SQLite
        string dbPath = Path.Combine(AppContext.BaseDirectory, "koffbot_data.db");
        builder.Services.AddDbContext<BotDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));

        // 5. СЕРВИСЫ БОТА
        builder.Services.AddSingleton<ITelegramBotClient>(sp => new TelegramBotClient(botToken));
        builder.Services.AddTransient<SubscriptionGenerator>();
        builder.Services.AddScoped<UpdateHandler>();
        builder.Services.AddHostedService<TelegramBotWorker>();

        var app = builder.Build();
        app.UseHttpLogging();

        // 6. Инициализация БД
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            db.Database.EnsureCreated();
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");

            // ДОБАВЛЯЕМ ЭТО: Защита от пустой базы данных серверов
            /*if (!db.ServerTemplates.Any())
            {
                db.ServerTemplates.Add(new ServerTemplate { ServerIp = "45.132.89.12", CoreType = "V2Ray", InboundsConfigJson = "{}" });
                db.SaveChanges();
                BotLogger.Log("SYSTEM", "Создан тестовый сервер для выдачи ключей!");
            }*/
        }

        // === ТОТАЛЬНАЯ ПРОСЛУШКА ВСЕХ ЗАПРОСОВ (HTTP-SPY) ===
        app.Use(async (context, next) =>
        {
            string ip = context.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
            BotLogger.Log("HTTP-SPY", $"[ВХОДЯЩИЙ ЗАПРОС] {context.Request.Method} {context.Request.Host}{context.Request.Path}");

            try
            {
                await next(context);
                BotLogger.Log("HTTP-SPY", $"[ОТВЕТ] Статус: {context.Response.StatusCode} | Путь: {context.Request.Path}");
            }
            catch (Exception ex)
            {
                BotLogger.Log("HTTP-SPY", $"[СБОЙ СЕРВЕРА] {ex.Message}");
                throw;
            }
        });

        // Включаем CORS и раздачу статического сайта (Mini App)
        app.UseCors("AllowWebApp");
        app.UseDefaultFiles();
        app.UseStaticFiles();

        // Middleware: Умная защита API-запросов (Охранник)
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value?.ToLower() ?? "";

            // ПРОПУСКАЕМ БЕЗ ПАРОЛЯ: Статический сайт (Mini App)
            if (path.StartsWith("/api/webapp") || path.EndsWith(".html") || path.EndsWith(".js") || path.EndsWith(".css") || path == "/")
            {
                await next(context);
                return;
            }

            // ИСПРАВЛЕНИЕ: Жесткая очистка ключей от скрытых символов Windows (CRLF), которые ломают авторизацию
            string safeSecret = apiSecret?.Trim('\r', '\n', ' ') ?? "";

            if (!context.Request.Headers.TryGetValue("X-API-KEY", out var extractedApiKey) || extractedApiKey.ToString().Trim('\r', '\n', ' ') != safeSecret)
            {
                context.Response.StatusCode = 401; // Unauthorized
                await context.Response.WriteAsync("Доступ запрещен. Неверный X-API-KEY.");
                return;
            }

            await next(context);
        });

        // === ENDPOINTS (API ДЛЯ ПАНЕЛИ) ===
        app.MapGet("/api/sync/pending", async (BotDbContext db) =>
        {
            var pending = await db.VpnSubscriptions
                .Where(s => s.SyncStatus == SyncStatus.PendingAdd || s.SyncStatus == SyncStatus.PendingUpdate)
                .ToListAsync();
            return Results.Ok(pending);
        });

        app.MapPost("/api/sync/commit", async (CommitRequestDto request, BotDbContext db) =>
        {
            var subs = await db.VpnSubscriptions.Where(s => request.Uuids.Contains(s.Uuid)).ToListAsync();
            foreach (var sub in subs) sub.SyncStatus = SyncStatus.Synced;
            await db.SaveChangesAsync();
            return Results.Ok(new { message = $"Синхронизировано {subs.Count} клиентов" });
        });

        app.MapPost("/api/templates", async (ServerTemplate template, BotDbContext db) =>
        {
            var existing = await db.ServerTemplates.FirstOrDefaultAsync(t => t.ServerIp == template.ServerIp);
            if (existing != null)
            {
                existing.CoreType = template.CoreType;
                existing.InboundsConfigJson = template.InboundsConfigJson;
            }
            else db.ServerTemplates.Add(template);
            await db.SaveChangesAsync();
            return Results.Ok(new { message = "Шаблон обновлен!" });
        });

        app.MapPost("/api/legacy/sync", async (List<LegacyUserDto> legacyUsers, BotDbContext db) =>
        {
            foreach (var user in legacyUsers)
            {
                var existing = await db.VpnSubscriptions.FirstOrDefaultAsync(s => s.Uuid == user.Uuid);
                if (existing == null)
                {
                    db.VpnSubscriptions.Add(new VpnSubscription
                    {
                        Uuid = user.Uuid,
                        Email = user.Email,
                        ServerIp = user.ServerIp,
                        TrafficLimitBytes = user.TrafficLimitBytes,
                        IsActive = true,
                        SyncStatus = SyncStatus.Synced,
                        TelegramId = 0
                    });
                }
            }
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        app.MapGet("/api/stats", async (BotDbContext db) =>
        {
            try { return Results.Ok(new { TotalUsers = await db.TelegramUsers.CountAsync() }); }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        // === ПОПОЛНЕНИЕ РЕЗЕРВА (ОТ ПАНЕЛИ НА ПК) ===
        app.MapPost("/api/sync/pool", async (List<ReserveKeyDto> keys, BotDbContext db) =>
        {
            // ИСПРАВЛЕНИЕ: Удаляем ТОЛЬКО старые резервы, не трогая настоящих Legacy-юзеров!
            var ghostKeys = await db.VpnSubscriptions
                .Where(s => s.TelegramId == 0 && s.Email.StartsWith("reserve_"))
                .ToListAsync();
            db.VpnSubscriptions.RemoveRange(ghostKeys);

            int added = 0;
            foreach (var k in keys)
            {
                db.VpnSubscriptions.Add(new VpnSubscription
                {
                    Uuid = k.Uuid,
                    TelegramId = 0,
                    Email = $"reserve_{k.Uuid.Substring(0, 5)}",
                    ServerIp = k.ServerIp,
                    TrafficLimitBytes = k.TrafficLimitBytes,
                    TrafficUsedBytes = 0,
                    IsActive = true,
                    MaxDevices = 2,
                    ExpiryDate = DateTime.UtcNow.AddDays(3),
                    SyncStatus = SyncStatus.Synced,
                    LastModifiedAt = DateTime.UtcNow
                });
                added++;
            }
            await db.SaveChangesAsync();
            return Results.Ok(new { message = $"Синхронизировано {added} ключей резерва" });
        });

        app.MapGet("/api/sync/pool/count", async (BotDbContext db) =>
        {
            int count = await db.VpnSubscriptions.CountAsync(s => s.TelegramId == 0 && s.Email.StartsWith("reserve_") && s.IsActive);
            return Results.Ok(new { ReserveCount = count });
        });

        // === ENDPOINT ДЛЯ TELEGRAM MINI APP ===
        app.MapGet("/api/webapp/profile", async (long tgId, BotDbContext db) =>
        {
            var user = await db.TelegramUsers.FirstOrDefaultAsync(u => u.TelegramId == tgId);
            var sub = await db.VpnSubscriptions.FirstOrDefaultAsync(s => s.TelegramId == tgId && s.IsActive);
            if (user == null) return Results.NotFound("Пользователь не найден");

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
                ExpiryDate = sub?.ExpiryDate
            });
        });

        app.MapPost("/api/webapp/generate", async (WebAppActionRequest req, BotDbContext db, SubscriptionGenerator generator) =>
        {
            var user = await db.TelegramUsers.FirstOrDefaultAsync(u => u.TelegramId == req.TelegramId);
            if (user == null) return Results.BadRequest("Пользователь не найден.");

            var existingSub = await db.VpnSubscriptions.FirstOrDefaultAsync(s => s.TelegramId == req.TelegramId && s.IsActive);
            if (existingSub != null) return Results.Ok();

            var (success, uuid, serverIp, err) = await generator.GenerateNewSubscriptionAsync(req.TelegramId, $"tg_{req.TelegramId}", CancellationToken.None);
            if (!success) return Results.Problem($"Ошибка генерации: {err}");

            return Results.Ok();
        });

        // ВОЗВРАЩЕН ЭНДПОИНТ ДЛЯ КНОПКИ ПРОДЛЕНИЯ (Ты его случайно удалил ранее)
        app.MapPost("/api/webapp/action", async (WebAppActionRequest req, ITelegramBotClient bot) =>
        {
            if (req.Action == "renew")
            {
                await bot.SendMessage(chatId: req.TelegramId, text: "💳 *Продление подписки*\n\nСтоимость продления на 30 дней: 150 руб.\nВыберите способ оплаты:", parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown);
            }
            return Results.Ok();
        });

        BotLogger.Log("SYSTEM", "KoFFBot: Демон Telegram и защищенный REST API запущены на порту 5000.");
        app.Run(); // Бесконечный цикл сервера

        BotLogger.Log("SYSTEM", "KoFFBot: Демон Telegram и защищенный REST API запущены на порту 5000.");
        app.MapPost("/api/webapp/generate", async (WebAppActionRequest req, BotDbContext db, SubscriptionGenerator generator) =>
        {
            var user = await db.TelegramUsers.FirstOrDefaultAsync(u => u.TelegramId == req.TelegramId);
            if (user == null) return Results.BadRequest("Пользователь не найден.");

            // Проверяем, нет ли уже активной подписки
            var existingSub = await db.VpnSubscriptions.FirstOrDefaultAsync(s => s.TelegramId == req.TelegramId && s.IsActive);
            if (existingSub != null) return Results.Ok(); // Уже есть

            // Генерируем ключ
            var (success, uuid, serverIp, err) = await generator.GenerateNewSubscriptionAsync(req.TelegramId, $"tg_{req.TelegramId}", CancellationToken.None);
            if (!success) return Results.Problem($"Ошибка генерации: {err}");

            return Results.Ok();
        });

        // === ПОПОЛНЕНИЕ РЕЗЕРВА (ОТ ПАНЕЛИ НА ПК) ===
        app.MapPost("/api/sync/pool", async (List<ReserveKeyDto> keys, BotDbContext db) =>
        {
            // 1. ЖЕСТКАЯ ЗАЧИСТКА: Удаляем все старые призрачные ключи (TelegramId == 0),
            // чтобы бот не выдавал ссылки, которых физически нет на сервере.
            var ghostKeys = await db.VpnSubscriptions.Where(s => s.TelegramId == 0).ToListAsync();
            db.VpnSubscriptions.RemoveRange(ghostKeys);

            // 2. Добавляем свежий резерв от Панели
            int added = 0;
            foreach (var k in keys)
            {
                db.VpnSubscriptions.Add(new VpnSubscription
                {
                    Uuid = k.Uuid,
                    TelegramId = 0, // 0 означает, что это СВОБОДНЫЙ РЕЗЕРВ
                    Email = $"reserve_{k.Uuid.Substring(0, 5)}",
                    ServerIp = k.ServerIp,
                    TrafficLimitBytes = k.TrafficLimitBytes,
                    TrafficUsedBytes = 0,
                    IsActive = true,
                    MaxDevices = 2,
                    ExpiryDate = DateTime.UtcNow.AddDays(3), // Гостевой доступ на 3 дня
                    SyncStatus = SyncStatus.Synced,
                    LastModifiedAt = DateTime.UtcNow
                });
                added++;
            }
            await db.SaveChangesAsync();
            return Results.Ok(new { message = $"Синхронизировано {added} ключей резерва" });
        });

        app.MapGet("/api/sync/pending", async (BotDbContext db) =>
        {
            // ИСПРАВЛЕНИЕ: Отдаем панели не только новые заказы, но и тех, кто забрал резервный ключ (PendingUpdate)
            var pending = await db.VpnSubscriptions
                .Where(s => s.SyncStatus == SyncStatus.PendingAdd || s.SyncStatus == SyncStatus.PendingUpdate)
                .ToListAsync();
            return Results.Ok(pending);
        });

        BotLogger.Log("SYSTEM", "KoFFBot: Демон Telegram и защищенный REST API запущены на порту 5000.");
        app.Run(); // Бесконечный цикл сервера
    }

    // === КЛАССЫ DTO ===
    public class LegacyUserDto
    {
        public string Uuid { get; set; } = "";
        public string Email { get; set; } = "";
        public string ServerIp { get; set; } = "";
        public long TrafficLimitBytes { get; set; }
        public class ReserveCountDto { public int ReserveCount { get; set; } }
        public class ReserveKeyDto { public string Uuid { get; set; } = ""; public string ServerIp { get; set; } = ""; public long TrafficLimitBytes { get; set; } }
    }

    public class CommitRequestDto
    {
        public List<string> Uuids { get; set; } = new();
    }

    public class WebAppActionRequest
    {
        public long TelegramId { get; set; }
        public string Action { get; set; } = "";
    }

    public class ReserveKeyDto
    {
        public string Uuid { get; set; } = "";
        public string ServerIp { get; set; } = "";
        public long TrafficLimitBytes { get; set; }
    }
}

