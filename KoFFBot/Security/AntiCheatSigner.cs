using System;
using System.Security.Cryptography;
using System.Text;

namespace KoFFBot.Security;

// Согласно Правилу 32: Один класс - одна роль (Генерация подписей для БД)
public static class AntiCheatSigner
{
    private static readonly string SecretKey = Environment.GetEnvironmentVariable("ANTI_CHEAT_SECRET") ?? "default_fallback_secret_123";

    public static string GenerateSignature(long telegramId, long valueToSign)
    {
        string payload = $"{telegramId}:{valueToSign}:{SecretKey}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SecretKey));
        byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }

    public static bool VerifySignature(long telegramId, long valueToSign, string signature)
    {
        string expectedSignature = GenerateSignature(telegramId, valueToSign);
        return expectedSignature == signature;
    }

    // === ZERO TRUST: ПРОВЕРКА ПОДЛИННОСТИ ОТ TELEGRAM ===
    public static bool ValidateTelegramInitData(string initData, string botToken)
    {
        if (string.IsNullOrWhiteSpace(initData) || string.IsNullOrWhiteSpace(botToken)) 
        {
            Serilog.Log.Warning("[AUTH] Missing initData or botToken.");
            return false;
        }
        try
        {
            var parsed = initData.Split('&')
                .Select(p => p.Split('='))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

            if (!parsed.TryGetValue("hash", out string? hash)) 
            {
                Serilog.Log.Warning("[AUTH] Hash not found in initData.");
                return false;
            }

            var dataCheckString = string.Join("\n", parsed
                .Where(kvp => kvp.Key != "hash")
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => $"{kvp.Key}={kvp.Value}"));

            using var hmacSha256 = new HMACSHA256(Encoding.UTF8.GetBytes("WebAppData"));
            byte[] secretKey = hmacSha256.ComputeHash(Encoding.UTF8.GetBytes(botToken));

            using var hmac = new HMACSHA256(secretKey);
            byte[] computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(dataCheckString));
            string computedHashHex = Convert.ToHexString(computedHash).ToLower();

            bool isValid = computedHashHex == hash;
            if (!isValid) Serilog.Log.Warning("[AUTH] Signature mismatch. Computed: {Computed}, Received: {Received}", computedHashHex, hash);
            return isValid;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[AUTH] Critical error during validation.");
            return false;
        }
    }
}