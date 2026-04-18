using System;
using System.IO;

namespace KoFFBot.Services;

public static class BotLogger
{
    private static readonly string LogDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
    private static readonly string LogFile = Path.Combine(LogDir, "bot_diagnostics.log");

    static BotLogger()
    {
        if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir);
    }

    public static void Log(string module, string message, Exception? ex = null)
    {
        try
        {
            string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{module}] {message}";
            if (ex != null)
            {
                logEntry += $"\n   ОШИБКА: {ex.Message}\n   ТРЕЙС: {ex.StackTrace}";
            }

            // Пишем в файл
            File.AppendAllText(LogFile, logEntry + "\n");

            // Дублируем в консоль для наглядности
            Console.WriteLine(logEntry);
        }
        catch { /* Игнорируем ошибки самого логгера */ }
    }
}