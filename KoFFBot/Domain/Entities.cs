using System;
using System.ComponentModel.DataAnnotations;

namespace KoFFBot.Domain;

public enum SyncStatus
{
    Synced = 0,
    PendingAdd = 1,
    PendingUpdate = 2,
    PendingDelete = 3
}

public sealed record TelegramUser
{
    [Key]
    public long TelegramId { get; set; }
    public string? FirstName { get; set; }
    public string LanguageCode { get; set; } = "ru";
    public int ReferralCount { get; set; }
    public long BonusBalance { get; set; }
}

public sealed record Referral
{
    [Key]
    public int Id { get; set; }
    public long InviterTelegramId { get; set; }
    public long InvitedTelegramId { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsActivated { get; set; }
}

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

public sealed record ServerTemplate
{
    [Key]
    public string ServerIp { get; init; } = string.Empty;
    public string CoreType { get; set; } = "sing-box";
    public string InboundsConfigJson { get; set; } = string.Empty;
}

public sealed record SupportMessage
{
    [Key]
    public int Id { get; set; }
    public long TelegramId { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool IsFromAdmin { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed record GameProfile
{
    [Key]
    public long TelegramId { get; set; }

    public int CurrentEnergy { get; set; } = 50;
    public DateTime LastEnergyUpdate { get; set; } = DateTime.UtcNow;

    public int BossKills { get; set; } = 0;

    // === АНТИ-ФРОД: Лимит 2 победы в месяц ===
    public int MonthlyBossKills { get; set; } = 0;
    public DateTime LastBossKillDate { get; set; } = DateTime.UtcNow;

    // === Защищенный ежедневный бонус ===
    public DateTime LastDailyBonusDate { get; set; } = DateTime.MinValue;

    // === НОВОЕ (ZERO TRUST): Таймер сессии для защиты от SpeedHack/Replay ===
    public DateTime CurrentGameStartTime { get; set; } = DateTime.MinValue;

    // Античит для энергии
    public string EnergySignature { get; set; } = string.Empty;

    public bool IsBanned { get; set; }
    public string BanReason { get; set; } = string.Empty;
}

public sealed record LeaderboardRecord
{
    [Key]
    public long TelegramId { get; set; }

    public long MaxScore { get; set; }
    public DateTime AchievedAt { get; set; }

    // Античит для очков
    public string ScoreSignature { get; set; } = string.Empty;
}