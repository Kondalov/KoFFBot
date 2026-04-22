using KoFFBot.Contracts;
using KoFFBot.Data;
using KoFFBot.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;

namespace KoFFBot.Endpoints;

public static class SyncEndpoints
{
    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/sync/pending", async (VpnDbContext db) => Results.Ok(await db.VpnSubscriptions.Where(s => s.SyncStatus == SyncStatus.PendingAdd || s.SyncStatus == SyncStatus.PendingUpdate).ToListAsync()));
        
        app.MapPost("/api/sync/commit", async (CommitRequestDto request, VpnDbContext db) => { 
            // Оптимизация: ExecuteUpdateAsync для массового сброса статуса
            int updated = await db.VpnSubscriptions
                .Where(s => request.Uuids.Contains(s.Uuid))
                .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.SyncStatus, SyncStatus.Synced));
                
            return Results.Ok(new { message = $"Синхронизировано {updated} клиентов" }); 
        });
        
        app.MapPost("/api/sync/traffic", async (List<TrafficSyncDto> trafficData, VpnDbContext db) => { 
            if (trafficData == null || !trafficData.Any()) return Results.Ok(); 
            
            foreach (var incoming in trafficData) { 
                // Массовое обновление лимитов без загрузки в память (для каждого UUID)
                await db.VpnSubscriptions
                    .Where(s => s.Uuid == incoming.Uuid)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(s => s.TrafficLimitBytes, incoming.TrafficLimitBytes)
                        .SetProperty(s => s.TrafficUsedBytes, incoming.TrafficUsedBytes));
            } 
            return Results.Ok(); 
        });
        
        app.MapPost("/api/templates", async (ServerTemplate template, VpnDbContext db) => { 
            var existing = await db.ServerTemplates.FirstOrDefaultAsync(t => t.ServerIp == template.ServerIp); 
            if (existing != null) { 
                existing.CoreType = template.CoreType; 
                existing.InboundsConfigJson = template.InboundsConfigJson; 
            } 
            else db.ServerTemplates.Add(template); 
            await db.SaveChangesAsync(); 
            return Results.Ok(); 
        });
        
        app.MapPost("/api/legacy/sync", async (List<LegacyUserDto> legacyUsers, VpnDbContext db) => { 
            foreach (var user in legacyUsers) { 
                var existing = await db.VpnSubscriptions.FirstOrDefaultAsync(s => s.Uuid == user.Uuid); 
                if (existing == null) db.VpnSubscriptions.Add(new VpnSubscription { Uuid = user.Uuid, Email = user.Email, ServerIp = user.ServerIp, TrafficLimitBytes = user.TrafficLimitBytes, IsActive = true, SyncStatus = SyncStatus.Synced, TelegramId = 0 }); 
            } 
            await db.SaveChangesAsync(); 
            return Results.Ok(); 
        });
        
        app.MapGet("/api/stats", async (VpnDbContext db) => Results.Ok(new { TotalUsers = await db.TelegramUsers.CountAsync() }));
        
        app.MapPost("/api/sync/pool", async (List<ReserveKeyDto> keys, VpnDbContext db) => { 
            // Очищаем старый пул
            await db.VpnSubscriptions
                .Where(s => s.TelegramId == 0 && s.Email.StartsWith("reserve_"))
                .ExecuteDeleteAsync();

            foreach (var k in keys) { 
                db.VpnSubscriptions.Add(new VpnSubscription { Uuid = k.Uuid, TelegramId = 0, Email = $"reserve_{k.Uuid.Substring(0, 5)}", ServerIp = k.ServerIp, TrafficLimitBytes = k.TrafficLimitBytes, TrafficUsedBytes = 0, IsActive = true, MaxDevices = 2, ExpiryDate = DateTime.UtcNow.AddDays(3), SyncStatus = SyncStatus.Synced, LastModifiedAt = DateTime.UtcNow }); 
            } 
            await db.SaveChangesAsync(); 
            return Results.Ok(); 
        });
        
        app.MapGet("/api/sync/pool/count", async (VpnDbContext db) => Results.Ok(new { ReserveCount = await db.VpnSubscriptions.CountAsync(s => s.TelegramId == 0 && s.Email.StartsWith("reserve_") && s.IsActive) }));
    }
}