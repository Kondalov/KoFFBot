using KoFFBot.Contracts;
using KoFFBot.Data;
using KoFFBot.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace KoFFBot.Endpoints;

public record ServerMigrationDto(string OldIp, string NewIp);

public static class SyncEndpoints
{
    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/sync/pending", async (VpnDbContext db, CancellationToken ct) =>
            Results.Ok(await db.VpnSubscriptions
                .Where(s => s.SyncStatus == SyncStatus.PendingAdd || s.SyncStatus == SyncStatus.PendingUpdate)
                .ToListAsync(ct)));

        app.MapPost("/api/sync/commit", async (CommitRequestDto request, VpnDbContext db, CancellationToken ct) => {
            int updated = await db.VpnSubscriptions
                .Where(s => request.Uuids.Contains(s.Uuid))
                .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.SyncStatus, SyncStatus.Synced), ct);

            return Results.Ok(new { message = $"Синхронизировано {updated} клиентов" });
        });

        // FOOLPROOF: Токен отмены защищает от зависших (zombie) запросов при обрыве связи с панелью
        app.MapPost("/api/sync/traffic", async (List<TrafficSyncDto> trafficData, VpnDbContext db, CancellationToken ct) => {
            if (trafficData == null || !trafficData.Any()) return Results.Ok();

            var incomingUuids = trafficData.Select(t => t.Uuid).Where(u => !string.IsNullOrEmpty(u)).ToList();
            var existingSubs = await db.VpnSubscriptions
                .Where(s => incomingUuids.Contains(s.Uuid) || incomingUuids.Contains(s.Email))
                .ToListAsync(ct); // Передали CancellationToken

            foreach (var incoming in trafficData)
            {
                var sub = existingSubs.FirstOrDefault(s => s.Uuid == incoming.Uuid)
                       ?? existingSubs.FirstOrDefault(s => s.Email == incoming.Uuid);

                if (sub != null)
                {
                    sub.TrafficLimitBytes = incoming.TrafficLimitBytes;
                    sub.TrafficUsedBytes = incoming.TrafficUsedBytes;
                    sub.ExpiryDate = incoming.ExpiryDate;
                    sub.LastModifiedAt = DateTime.UtcNow;
                }
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        app.MapPost("/api/templates", async (ServerTemplate template, VpnDbContext db, CancellationToken ct) => {
            var existing = await db.ServerTemplates.FirstOrDefaultAsync(t => t.ServerIp == template.ServerIp, ct);
            if (existing != null)
            {
                existing.CoreType = template.CoreType;
                existing.InboundsConfigJson = template.InboundsConfigJson;
            }
            else db.ServerTemplates.Add(template);

            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        app.MapPost("/api/legacy/sync", async (List<LegacyUserDto> legacyUsers, VpnDbContext db, CancellationToken ct) => {
            if (legacyUsers == null || !legacyUsers.Any()) return Results.BadRequest("Invalid data");

            var deduplicatedUsers = legacyUsers
                .Where(u => !string.IsNullOrEmpty(u.Email) && u.Email.StartsWith("tg_"))
                .GroupBy(u => u.Email)
                .Select(g => g.OrderByDescending(u => u.ExpiryDate ?? DateTime.MinValue).First())
                .ToList();

            deduplicatedUsers.AddRange(legacyUsers.Where(u => string.IsNullOrEmpty(u.Email) || !u.Email.StartsWith("tg_")));

            var incomingUuids = deduplicatedUsers.Select(u => u.Uuid).ToList();
            var incomingEmails = deduplicatedUsers.Select(u => u.Email).Where(e => !string.IsNullOrEmpty(e)).ToList();

            var existingSubs = await db.VpnSubscriptions
                .Where(s => incomingUuids.Contains(s.Uuid) || incomingEmails.Contains(s.Email))
                .ToListAsync(ct);

            foreach (var user in deduplicatedUsers)
            {
                var existing = existingSubs.FirstOrDefault(s => s.Uuid == user.Uuid)
                            ?? existingSubs.FirstOrDefault(s => !string.IsNullOrEmpty(user.Email) && user.Email.StartsWith("tg_") && s.Email == user.Email);

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
                        TelegramId = 0,
                        ExpiryDate = user.ExpiryDate
                    });
                }
                else
                {
                    existing.Uuid = user.Uuid;
                    existing.ExpiryDate = user.ExpiryDate;
                    existing.TrafficLimitBytes = user.TrafficLimitBytes;
                    existing.ServerIp = user.ServerIp;
                    existing.IsActive = true;
                    existing.LastModifiedAt = DateTime.UtcNow;
                }
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        app.MapGet("/api/stats", async (VpnDbContext db, CancellationToken ct) =>
            Results.Ok(new { TotalUsers = await db.TelegramUsers.CountAsync(ct) }));

        app.MapPost("/api/sync/pool", async (List<ReserveKeyDto> keys, VpnDbContext db, CancellationToken ct) => {
            await db.VpnSubscriptions
                .Where(s => s.TelegramId == 0 && s.Email.StartsWith("reserve_"))
                .ExecuteDeleteAsync(ct);

            foreach (var k in keys)
            {
                db.VpnSubscriptions.Add(new VpnSubscription { Uuid = k.Uuid, TelegramId = 0, Email = $"reserve_{k.Uuid.Substring(0, 5)}", ServerIp = k.ServerIp, TrafficLimitBytes = k.TrafficLimitBytes, TrafficUsedBytes = 0, IsActive = true, MaxDevices = 2, ExpiryDate = DateTime.UtcNow.AddDays(3), SyncStatus = SyncStatus.Synced, LastModifiedAt = DateTime.UtcNow });
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        app.MapGet("/api/sync/pool/count", async (VpnDbContext db, CancellationToken ct) =>
            Results.Ok(new { ReserveCount = await db.VpnSubscriptions.CountAsync(s => s.TelegramId == 0 && s.Email.StartsWith("reserve_") && s.IsActive, ct) }));

        app.MapPost("/api/sync/migrate-server", async (ServerMigrationDto req, VpnDbContext db, CancellationToken ct) => {
            if (string.IsNullOrWhiteSpace(req.OldIp) || string.IsNullOrWhiteSpace(req.NewIp))
                return Results.BadRequest("IP адреса не могут быть пустыми");

            if (req.OldIp == req.NewIp)
                return Results.Ok(new { message = "IP адреса совпадают, миграция не требуется.", count = 0 });

            int updatedCount = await db.VpnSubscriptions
                .Where(s => s.ServerIp == req.OldIp)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(s => s.ServerIp, req.NewIp)
                    .SetProperty(s => s.SyncStatus, SyncStatus.PendingUpdate)
                    .SetProperty(s => s.LastModifiedAt, DateTime.UtcNow), ct); // Передали CancellationToken

            return Results.Ok(new { message = $"Успешно перенесено {updatedCount} подписок.", count = updatedCount });
        });
    }
}
