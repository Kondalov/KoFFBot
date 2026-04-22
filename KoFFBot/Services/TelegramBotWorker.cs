using Telegram.Bot;
using Telegram.Bot.Polling;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading;
using System.Threading.Tasks;
using KoFFBot.Data;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using Serilog;

namespace KoFFBot.Services;

public class TelegramBotWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;

    public TelegramBotWorker(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("🚀 Демон Telegram запускается...");

        _ = StartPollingAsync(stoppingToken);
        _ = StartEnergyRegenAsync(stoppingToken);

        await StartExpiryNotifierAsync(stoppingToken);
    }

    private async Task StartEnergyRegenAsync(CancellationToken stoppingToken)
    {
        Log.Information("Генератор энергии запущен (восстановление каждые 30 минут через PeriodicTimer).");

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();

                // Оптимизация: Массовое обновление через ExecuteUpdateAsync для снижения нагрузки на память
                int updated = await db.GameProfiles
                    .Where(p => p.CurrentEnergy < 5 && !p.IsBanned)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(p => p.CurrentEnergy, p => p.CurrentEnergy + 1)
                        .SetProperty(p => p.LastEnergyUpdate, DateTime.UtcNow), 
                        stoppingToken);

                if (updated > 0)
                {
                    Log.Information("Восстановлена энергия для {UpdatedCount} игроков.", updated);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка восстановления энергии");
            }
        }
    }

    private async Task StartPollingAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var botClient = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>();
            var updateHandler = scope.ServiceProvider.GetRequiredService<UpdateHandler>();

            var me = await botClient.GetMe(cancellationToken: stoppingToken);
            Log.Information("✅ Бот успешно авторизован! Имя: @{Username}", me.Username);

            var receiverOptions = new ReceiverOptions
            {
                DropPendingUpdates = false,
                AllowedUpdates = Array.Empty<Telegram.Bot.Types.Enums.UpdateType>()
            };

            await botClient.ReceiveAsync(updateHandler, receiverOptions, stoppingToken);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "❌ КРИТИЧЕСКИЙ СБОЙ POLLING!");
        }
    }

    private async Task StartExpiryNotifierAsync(CancellationToken stoppingToken)
    {
        Log.Information("Сканер сроков подписок активирован (проверка каждый час через PeriodicTimer).");

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<VpnDbContext>();
                var bot = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>();

                var now = DateTime.UtcNow;

                var expiringSubs = await db.VpnSubscriptions
                    .Where(s => s.IsActive && s.ExpiryDate.HasValue)
                    .ToListAsync(stoppingToken);

                foreach (var sub in expiringSubs)
                {
                    var timeLeft = sub.ExpiryDate!.Value - now;
                    if (timeLeft.TotalHours > 71 && timeLeft.TotalHours <= 72)
                    {
                        await SendNotice(bot, sub.TelegramId, "⚠️ *Внимание!*\nДо окончания подписки осталось *3 дня*.", stoppingToken);
                    }
                    else if (timeLeft.TotalHours > 23 && timeLeft.TotalHours <= 24)
                    {
                        await SendNotice(bot, sub.TelegramId, "🔴 *СРОЧНО!*\nВаша подписка истекает *завтра*.", stoppingToken);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка сканирования сроков");
            }
        }
    }

    private async Task SendNotice(ITelegramBotClient bot, long chatId, string text, CancellationToken ct)
    {
        try { await bot.SendMessage(chatId: chatId, text: text, parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, cancellationToken: ct); } catch { }
    }
}