using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;
using static Telegram.Bot.TelegramBotClient;

namespace KoFFBot.Services;

public class TelegramBotBackgroundService : BackgroundService
{
    private readonly ILogger<TelegramBotBackgroundService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _botToken;

    public TelegramBotBackgroundService(
        ILogger<TelegramBotBackgroundService> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;

        // Безопасно получаем токен из окружения (он был загружен в Program.cs из .env)
        _botToken = Environment.GetEnvironmentVariable("BOT_TOKEN")
            ?? throw new InvalidOperationException("BOT_TOKEN не найден!");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Инициализация Telegram Bot Client...");

        // ИСПРАВЛЕНИЕ: Умная настройка HttpClient для работы по IPv6 и IPv4 (Dual-Stack)
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15), // Избегаем stale DNS
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };

        var httpClient = new HttpClient(handler);
        var botClient = new TelegramBotClient(_botToken, httpClient);

        // Настраиваем ReceiverOptions (какие типы обновлений мы хотим получать)
        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery },
            DropPendingUpdates = true // Очищаем старые сообщения при запуске
        };

        _logger.LogInformation("Telegram Bot запущен и готов к приему сообщений.");

        // Запускаем бесконечный цикл приема сообщений (Long Polling)
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Для обработки каждого сообщения мы создаем свой Scope. 
                // Это необходимо, так как DbContext (База Данных) живет в пределах одного запроса.
                using var scope = _serviceProvider.CreateScope();
                var updateHandler = scope.ServiceProvider.GetRequiredService<UpdateHandler>();

                await botClient.ReceiveAsync(
                    updateHandler: updateHandler,
                    receiverOptions: receiverOptions,
                    cancellationToken: stoppingToken
                );
            }
            catch (OperationCanceledException)
            {
                // Нормальное завершение при остановке службы
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Критическая ошибка в цикле ReceiveAsync. Перезапуск через 5 секунд...");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}