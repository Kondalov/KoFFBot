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
        BotLogger.Log("WORKER", "🚀 Демон Telegram запускается...");

        // Запускаем слушателя сообщений в фоне
        _ = StartPollingAsync(stoppingToken);

        // Запускаем умный мониторинг сроков подписок
        await StartExpiryNotifierAsync(stoppingToken);
    }

    private async Task StartPollingAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var botClient = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>();
            var updateHandler = scope.ServiceProvider.GetRequiredService<UpdateHandler>();

            var me = await botClient.GetMe(cancellationToken: stoppingToken);
            BotLogger.Log("WORKER", $"✅ Бот успешно авторизован! Имя: @{me.Username}");

            // ИСПРАВЛЕНИЕ АРХИТЕКТУРЫ: 
            // 1. DropPendingUpdates = false (Чтобы не терять нажатия кнопок при перезапусках!)
            // 2. AllowedUpdates = Array.Empty (Разрешаем принимать ВСЕ типы апдейтов)
            var receiverOptions = new ReceiverOptions
            {
                DropPendingUpdates = false,
                AllowedUpdates = Array.Empty<Telegram.Bot.Types.Enums.UpdateType>()
            };

            await botClient.ReceiveAsync(updateHandler, receiverOptions, stoppingToken);
        }
        catch (Exception ex)
        {
            BotLogger.Log("WORKER", "❌ КРИТИЧЕСКИЙ СБОЙ POLLING!", ex);
        }
    }

    private async Task StartExpiryNotifierAsync(CancellationToken stoppingToken)
    {
        BotLogger.Log("NOTIFIER", "Сканер сроков подписок активирован (проверка каждый час).");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
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
                        await SendNotice(bot, sub.TelegramId, "⚠️ *Внимание!*\nДо окончания подписки осталось *3 дня*.\nЗайдите в приложение, чтобы продлить доступ!", stoppingToken);
                    }
                    else if (timeLeft.TotalHours > 23 && timeLeft.TotalHours <= 24)
                    {
                        await SendNotice(bot, sub.TelegramId, "🔴 *СРОЧНО!*\nВаша подписка истекает *завтра*.\nПродлите тариф сейчас, чтобы остаться на связи!", stoppingToken);
                    }
                }
            }
            catch (Exception ex)
            {
                BotLogger.Log("NOTIFIER", "Ошибка сканирования сроков", ex);
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private async Task SendNotice(ITelegramBotClient bot, long chatId, string text, CancellationToken ct)
    {
        try { await bot.SendMessage(chatId: chatId, text: text, parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, cancellationToken: ct); } catch { }
    }
}