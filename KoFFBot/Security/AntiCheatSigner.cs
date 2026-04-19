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
}