using Telegram.Bot;
using Telegram.Bot.Polling;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading;
using System.Threading.Tasks;

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
        BotLogger.Log("WORKER", "🚀 Демон Telegram пытается запуститься в фоновом режиме...");

        try
        {
            using var scope = _serviceProvider.CreateScope();

            var botClient = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>();
            var updateHandler = scope.ServiceProvider.GetRequiredService<UpdateHandler>();

            BotLogger.Log("WORKER", "Соединяемся с серверами Telegram...");

            var me = await botClient.GetMe(cancellationToken: stoppingToken);

            BotLogger.Log("WORKER", $"✅ Бот успешно авторизован! Имя: @{me.Username}");

            var receiverOptions = new ReceiverOptions
            {
                DropPendingUpdates = true
            };

            BotLogger.Log("WORKER", "Слушатель сообщений запущен.");

            await botClient.ReceiveAsync(
                updateHandler: updateHandler,
                receiverOptions: receiverOptions,
                cancellationToken: stoppingToken
            );
        }
        catch (Exception ex)
        {
            // ЕСЛИ ЧТО-ТО УПАЛО, МЫ УВИДИМ ЭТО В ЛОГАХ!
            BotLogger.Log("WORKER", "❌ КРИТИЧЕСКИЙ СБОЙ В ДЕМОНЕ!", ex);
        }
    }
}