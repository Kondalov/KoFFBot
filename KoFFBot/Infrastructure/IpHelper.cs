using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace KoFFBot.Infrastructure;

public static class IpHelper
{
    private static string? _cachedIp;

    public static async Task<string> GetPublicIpAsync()
    {
        if (!string.IsNullOrEmpty(_cachedIp)) return _cachedIp;

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            _cachedIp = await client.GetStringAsync("https://api.ipify.org");
            return _cachedIp?.Trim() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }
}
