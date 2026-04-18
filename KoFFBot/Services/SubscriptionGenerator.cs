using KoFFBot.Data;
using KoFFBot.Domain;
using Microsoft.EntityFrameworkCore;

namespace KoFFBot.Services;

public class SubscriptionGenerator
{
    private readonly BotDbContext _dbContext;
    private readonly ILogger<SubscriptionGenerator> _logger;

    public SubscriptionGenerator(BotDbContext dbContext, ILogger<SubscriptionGenerator> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<(bool IsSuccess, string? Uuid, string? ServerIp, string? ErrorMessage)> GenerateNewSubscriptionAsync(
        long telegramId, string email, CancellationToken ct)
    {
        try
        {
            // УМНЫЙ АЛГОРИТМ: Ищем свободный ключ в резервном пуле (TelegramId == 0)
            var reserveSub = await _dbContext.VpnSubscriptions
                .FirstOrDefaultAsync(s => s.TelegramId == 0 && s.IsActive, ct);

            if (reserveSub == null)
            {
                _logger.LogWarning("Закончились резервные ключи в пуле!");
                return (false, null, null, "⏳ Идет пополнение резервных серверов. Повторите попытку через пару минут.");
            }

            // Бронируем ключ за пользователем
            reserveSub.TelegramId = telegramId;
            reserveSub.Email = email;
            // Ставим статус PendingUpdate, чтобы панель на ПК поняла: "Ага, временный ключ забрали, надо настроить ему постоянные лимиты!"
            reserveSub.SyncStatus = SyncStatus.PendingUpdate;
            reserveSub.LastModifiedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation($"Выдан резервный ключ (UUID: {reserveSub.Uuid}) для ID: {telegramId}");
            return (true, reserveSub.Uuid, reserveSub.ServerIp, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при выдаче резервного ключа.");
            return (false, null, null, "Внутренняя ошибка. Попробуйте позже.");
        }
    }
}