using KoFFBot.Contracts;
using KoFFBot.Security;
using KoFFBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System;
using System.Threading;

namespace KoFFBot.Endpoints;

public static class GameEndpoints
{
    public static void MapGameEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/game/profile", async (long tgId, GameService gameService) => {
            var profile = await gameService.GetOrCreateProfileAsync(tgId, CancellationToken.None);
            if (profile.IsBanned) return Results.BadRequest("Аккаунт заблокирован.");

            return Results.Ok(new
            {
                Energy = profile.CurrentEnergy,
                IsBanned = profile.IsBanned,
                BossKills = profile.BossKills,
                MonthlyBossKills = profile.MonthlyBossKills,
                CanClaimDaily = profile.LastDailyBonusDate.Date < DateTime.UtcNow.Date
            });
        });

        app.MapPost("/api/game/daily_bonus", async (GameActionRequest req, GameService gameService) => {
            string token = (Environment.GetEnvironmentVariable("BOT_TOKEN") ?? "").Trim('"', '\'', ' ');
            if (!AntiCheatSigner.ValidateTelegramInitData(req.Signature, token)) return Results.BadRequest("Ошибка авторизации.");
            var result = await gameService.ClaimDailyBonusAsync(req.TelegramId, CancellationToken.None);
            if (!result.Success) return Results.BadRequest(result.Message);
            return Results.Ok(new { Message = result.Message, NewEnergy = result.NewEnergy });
        });

        app.MapPost("/api/game/start", async (GameActionRequest req, GameService gameService) => {
            string token = (Environment.GetEnvironmentVariable("BOT_TOKEN") ?? "").Trim('"', '\'', ' ');
            if (!AntiCheatSigner.ValidateTelegramInitData(req.Signature, token)) return Results.BadRequest("Ошибка авторизации.");
            var result = await gameService.TryStartGameAsync(req.TelegramId, CancellationToken.None);
            if (!result.Success) return Results.BadRequest(result.Message);
            return Results.Ok(new { Message = result.Message, RemainingEnergy = result.RemainingEnergy });
        });

        app.MapPost("/api/game/submit", async (GameScoreRequest req, GameService gameService) => {
            string token = (Environment.GetEnvironmentVariable("BOT_TOKEN") ?? "").Trim('"', '\'', ' ');
            if (!AntiCheatSigner.ValidateTelegramInitData(req.Signature, token)) return Results.BadRequest("Ошибка авторизации.");
            var result = await gameService.SubmitScoreAsync(req.TelegramId, req.Score, CancellationToken.None);
            if (!result.Success) return Results.BadRequest(result.Message);
            return Results.Ok(new { Message = result.Message });
        });

        app.MapPost("/api/game/boss_victory", async (GameActionRequest req, GameService gameService) => {
            string token = (Environment.GetEnvironmentVariable("BOT_TOKEN") ?? "").Trim('"', '\'', ' ');
            if (!AntiCheatSigner.ValidateTelegramInitData(req.Signature, token)) return Results.BadRequest("Ошибка авторизации.");
            var result = await gameService.ProcessBossVictoryAsync(req.TelegramId, CancellationToken.None);
            if (!result.Success) return Results.BadRequest(result.Message);
            return Results.Ok(new { Message = result.Message, NewEnergy = result.NewEnergy });
        });

        app.MapPost("/api/game/chest_collect", async (GameScoreRequest req, GameService gameService) => {
            string token = (Environment.GetEnvironmentVariable("BOT_TOKEN") ?? "").Trim('"', '\'', ' ');
            if (!AntiCheatSigner.ValidateTelegramInitData(req.Signature, token)) return Results.BadRequest("Ошибка авторизации.");
            var result = await gameService.ProcessChestCollectionAsync(req.TelegramId, req.Score, CancellationToken.None);
            if (!result.Success) return Results.BadRequest(result.Message);
            return Results.Ok(new { Message = result.Message, NewEnergy = result.NewEnergy });
        });

        app.MapPost("/api/game/cheat", async (GameActionRequest req, GameService gameService) => {
            string token = (Environment.GetEnvironmentVariable("BOT_TOKEN") ?? "").Trim('"', '\'', ' ');
            if (!AntiCheatSigner.ValidateTelegramInitData(req.Signature, token)) return Results.BadRequest("Ошибка авторизации.");
            var result = await gameService.AddCheatEnergyAsync(req.TelegramId, 50, CancellationToken.None);
            return Results.Ok(new { Message = result.Message, NewEnergy = result.NewEnergy });
        });

        app.MapPost("/api/game/reset_boss", async (GameActionRequest req, GameService gameService) => {
            string token = (Environment.GetEnvironmentVariable("BOT_TOKEN") ?? "").Trim('"', '\'', ' ');
            if (!AntiCheatSigner.ValidateTelegramInitData(req.Signature, token)) return Results.BadRequest("Ошибка авторизации.");
            var result = await gameService.ResetBossStatsAsync(req.TelegramId, CancellationToken.None);
            return Results.Ok(new { Message = result.Message });
        });

        // НОВЫЙ ЭНДПОИНТ: Автоматическое начисление бонусов (Рефералы, Удержание, Счастливые часы)
        app.MapPost("/api/game/claim_bonus", async (ClaimBonusRequest req, GameService gameService) => {
            string token = (Environment.GetEnvironmentVariable("BOT_TOKEN") ?? "").Trim('"', '\'', ' ');
            if (!AntiCheatSigner.ValidateTelegramInitData(req.Signature, token)) return Results.BadRequest("Ошибка авторизации.");
            var result = await gameService.ClaimAdvancedBonusAsync(req.TelegramId, req.BonusType, CancellationToken.None);
            if (!result.Success) return Results.BadRequest(result.Message);
            return Results.Ok(new { Message = result.Message, NewEnergy = result.NewEnergy });
        });

        app.MapGet("/api/game/leaderboard", async (GameService gameService) => {
            var result = await gameService.GetLeaderboardAsync(CancellationToken.None);
            return Results.Ok(result);
        });
    }
}