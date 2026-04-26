using System;
using System.Collections.Generic;

namespace KoFFBot.Contracts;

public class LegacyUserDto { public string Uuid { get; set; } = ""; public string Email { get; set; } = ""; public string ServerIp { get; set; } = ""; public long TrafficLimitBytes { get; set; } public DateTime? ExpiryDate { get; set; } }
public class CommitRequestDto { public List<string> Uuids { get; set; } = new(); }
public class WebAppActionRequest { public long TelegramId { get; set; } public string Action { get; set; } = ""; }
public class ReserveCountDto { public int ReserveCount { get; set; } }
public class ReserveKeyDto { public string Uuid { get; set; } = ""; public string ServerIp { get; set; } = ""; public long TrafficLimitBytes { get; set; } }
public class BuyRequest { public long TelegramId { get; set; } public string TariffName { get; set; } = ""; }
public class UserMessageRequest { public long TelegramId { get; set; } public string Text { get; set; } = ""; }
public class GameActionRequest { public long TelegramId { get; set; } public string Signature { get; set; } = string.Empty; }
public class GameScoreRequest { public long TelegramId { get; set; } public long Score { get; set; } public string Signature { get; set; } = string.Empty; }
public class ClaimBonusRequest { public long TelegramId { get; set; } public string BonusType { get; set; } = string.Empty; public string Signature { get; set; } = string.Empty; }
public class TrafficSyncDto { public string Uuid { get; set; } = ""; public long TrafficUsedBytes { get; set; } public long TrafficLimitBytes { get; set; } public DateTime? ExpiryDate { get; set; } }
