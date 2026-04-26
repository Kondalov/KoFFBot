using KoFFBot.Contracts;
using KoFFBot.Data;
using KoFFBot.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace KoFFBot.Endpoints;

public static class SyncEndpoints
{
    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/sync/pending", async (VpnDbContext db) => Results.Ok(await db.VpnSubscriptions.Where(s => s.SyncStatus == SyncStatus.PendingAdd || s.SyncStatus == SyncStatus.PendingUpdate).ToListAsync()));

        app.MapPost("/api/sync/commit", async (CommitRequestDto request, VpnDbContext db) => {
            int updated = await db.VpnSubscriptions
                .Where(s => request.Uuids.Contains(s.Uuid))
                .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.SyncStatus, SyncStatus.Synced));

            return Results.Ok(new { message = $"Синхронизировано {updated} клиентов" });
        });

        app.MapPost("/api/sync/traffic", async (HttpContext context, VpnDbContext db) => {
            string body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            await System.IO.File.WriteAllTextAsync("/opt/koffbot/Logs/last_traffic_body.json", body);

            var trafficData = System.Text.Json.JsonSerializer.Deserialize<List<TrafficSyncDto>>(body, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (trafficData == null || !trafficData.Any()) return Results.Ok();

            foreach (var incoming in trafficData)
            {
                // Умная синхронизация: пытаемся найти по UUID
                var sub = await db.VpnSubscriptions.FirstOrDefaultAsync(s => s.Uuid == incoming.Uuid);

                // Если по UUID не нашли, ищем по Email (для тех, у кого сменился UUID в панели)
                if (sub == null && !string.IsNullOrEmpty(incoming.Uuid))
                {
                    // В случае если панель прислала Email (tg_ID) вместо UUID
                    sub = await db.VpnSubscriptions.FirstOrDefaultAsync(s => s.Email == incoming.Uuid);
                }

                if (sub != null)
                {
                    sub.TrafficLimitBytes = incoming.TrafficLimitBytes;
                    sub.TrafficUsedBytes = incoming.TrafficUsedBytes;
                    sub.ExpiryDate = incoming.ExpiryDate;
                    sub.LastModifiedAt = DateTime.UtcNow;
                }
            }
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        app.MapPost("/api/templates", async (ServerTemplate template, VpnDbContext db) => {
            var existing = await db.ServerTemplates.FirstOrDefaultAsync(t => t.ServerIp == template.ServerIp);
            if (existing != null)
            {
                existing.CoreType = template.CoreType;
                existing.InboundsConfigJson = template.InboundsConfigJson;
            }
            else db.ServerTemplates.Add(template);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        app.MapPost("/api/legacy/sync", async (HttpContext context, VpnDbContext db) => {
            string body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            await System.IO.File.WriteAllTextAsync("/opt/koffbot/Logs/last_legacy_body.json", body);

            var legacyUsers = System.Text.Json.JsonSerializer.Deserialize<List<LegacyUserDto>>(body, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (legacyUsers == null) return Results.BadRequest("Invalid data");

            // Умная дедупликация: если в панели случайно создались дубликаты с одинаковым Email (tg_ID), 
            // мы берем только ту запись, у которой самая большая дата окончания (ExpiryDate).
            var deduplicatedUsers = legacyUsers
                .Where(u => !string.IsNullOrEmpty(u.Email) && u.Email.StartsWith("tg_"))
                .GroupBy(u => u.Email)
                .Select(g => g.OrderByDescending(u => u.ExpiryDate ?? DateTime.MinValue).First())
                .ToList();

            deduplicatedUsers.AddRange(legacyUsers.Where(u => string.IsNullOrEmpty(u.Email) || !u.Email.StartsWith("tg_")));

            foreach (var user in deduplicatedUsers)
            {
                // Сначала ищем по UUID
                var existing = await db.VpnSubscriptions.FirstOrDefaultAsync(s => s.Uuid == user.Uuid);

                // Если не нашли по UUID, ищем по Email (т.к. email в формате tg_ID уникален)
                if (existing == null && !string.IsNullOrEmpty(user.Email) && user.Email.StartsWith("tg_"))
                {
                    existing = await db.VpnSubscriptions.FirstOrDefaultAsync(s => s.Email == user.Email);
                }

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
                    // Обновляем всё, включая UUID если он изменился в панели
                    existing.Uuid = user.Uuid;
                    existing.ExpiryDate = user.ExpiryDate;
                    existing.TrafficLimitBytes = user.TrafficLimitBytes;
                    existing.ServerIp = user.ServerIp;
                    existing.IsActive = true;
                    existing.LastModifiedAt = DateTime.UtcNow;
                }
            }
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        app.MapGet("/api/stats", async (VpnDbContext db) => Results.Ok(new { TotalUsers = await db.TelegramUsers.CountAsync() }));

        app.MapGet("/sub/{uuid}", async (string uuid, VpnDbContext db) => {
            var sub = await db.VpnSubscriptions.FirstOrDefaultAsync(s => s.Uuid == uuid);
            if (sub == null || !sub.IsActive) return Results.NotFound("Подписка не найдена или неактивна.");

            var template = await db.ServerTemplates.FirstOrDefaultAsync(t => t.ServerIp == sub.ServerIp);
            if (template == null) return Results.NotFound("Конфигурация сервера не найдена.");

            try
            {
                var inbounds = System.Text.Json.JsonSerializer.Deserialize<List<dynamic>>(template.InboundsConfigJson);
                var vless = inbounds?.FirstOrDefault(i => i.GetProperty("Protocol").GetString() == "VLESS");

                if (vless == null) return Results.NotFound("VLESS inbound не найден.");

                string port = vless.GetProperty("Port").ToString();
                string sni = vless.GetProperty("Sni").GetString();
                string pbk = vless.GetProperty("PublicKey").GetString();
                string sid = vless.GetProperty("ShortId").GetString();

                // Формируем современную VLESS REALITY ссылку (Стандарт Апрель 2026)
                // Используем VISION flow для лучшей проходимости и совместимости с Hiddify
                string vlessLink = $"vless://{uuid}@{sub.ServerIp}:{port}?type=tcp&security=reality&pbk={pbk}&fp=chrome&sni={sni}&sid={sid}&spx=%2F&flow=xtls-rprx-vision#{Uri.EscapeDataString("KoFFBot_" + sub.Email)}";

                // Возвращаем в чистом виде (Hiddify поймет и так)
                return Results.Content(vlessLink, "text/plain; charset=utf-8");
            }
            catch (Exception ex)
            {
                return Results.Problem("Ошибка генерации конфига: " + ex.Message);
            }
        });

        app.MapPost("/api/sync/pool", async (List<ReserveKeyDto> keys, VpnDbContext db) => {
            await db.VpnSubscriptions
                .Where(s => s.TelegramId == 0 && s.Email.StartsWith("reserve_"))
                .ExecuteDeleteAsync();

            foreach (var k in keys)
            {
                db.VpnSubscriptions.Add(new VpnSubscription { Uuid = k.Uuid, TelegramId = 0, Email = $"reserve_{k.Uuid.Substring(0, 5)}", ServerIp = k.ServerIp, TrafficLimitBytes = k.TrafficLimitBytes, TrafficUsedBytes = 0, IsActive = true, MaxDevices = 2, ExpiryDate = DateTime.UtcNow.AddDays(3), SyncStatus = SyncStatus.Synced, LastModifiedAt = DateTime.UtcNow });
            }
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        app.MapGet("/api/sync/pool/count", async (VpnDbContext db) => Results.Ok(new { ReserveCount = await db.VpnSubscriptions.CountAsync(s => s.TelegramId == 0 && s.Email.StartsWith("reserve_") && s.IsActive) }));
    }
}
