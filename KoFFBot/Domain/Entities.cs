using System;
using System.ComponentModel.DataAnnotations;

namespace KoFFBot.Domain;

// Статусы синхронизации (Outbox Pattern)
public enum SyncStatus
{
    Synced = 0,         // Полностью синхронизировано с KoFFPanel
    PendingAdd = 1,     // Создан ботом (KoFFPanel должна забрать)
    PendingUpdate = 2,  // Изменен ботом (оплачен, продлен)
    PendingDelete = 3   // Удален/Забанен ботом
}

// Пользователь Telegram (строго отделен от VPN-клиента)
public sealed record TelegramUser
{
    [Key]
    public long TelegramId { get; set; }
    public string? Username { get; set; }
    public string? FirstName { get; set; }
    public string LanguageCode { get; set; } = "ru";
    public long? InvitedBy { get; set; }
    public int ReferralCount { get; set; }
    public long BonusBalance { get; set; }
}

// VPN Подписка (То, что бот продает и чем управляет)
public sealed record VpnSubscription
{
    [Key]
    public string Uuid { get; set; } = string.Empty;
    public long TelegramId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string ServerIp { get; set; } = string.Empty;
    public long TrafficLimitBytes { get; set; }
    public long TrafficUsedBytes { get; set; }
    public bool IsActive { get; set; }
    public int MaxDevices { get; set; } = 2;
    public DateTime? ExpiryDate { get; set; }
    public bool GracePeriodUsed { get; set; }

    public SyncStatus SyncStatus { get; set; }
    public DateTime LastModifiedAt { get; set; }
}

// Кэш шаблонов серверов (Бот читает это, чтобы знать, как делать ссылки)
public sealed record ServerTemplate
{
    [Key]
    public string ServerIp { get; init; } = string.Empty;
    public string CoreType { get; set; } = "sing-box"; // xray или sing-box

    // JSON слепок Inbounds (порты, SNI, PubKey)
    public string InboundsConfigJson { get; set; } = string.Empty;
}