using DotNetEnv;
using KoFFBot.Data;
using KoFFBot.Services;
using KoFFBot.Endpoints;
using KoFFBot.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Serilog;
using Serilog.Events;
using System;
using System.IO;

namespace KoFFBot;

public static class Program
{
    public static void Main(string[] args)
    {
        Env.Load();
        
        // Настройка Serilog с ротацией и лимитами
        string logPath = Path.Combine(AppContext.BaseDirectory, "Logs", "bot_diagnostics.log");
        
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.Async(a => a.File(
                logPath,
                rollingInterval: RollingInterval.Day, // Перезапись/новый файл раз в сутки
                fileSizeLimitBytes: 100 * 1024 * 1024, // Лимит 100МБ
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 7, // Храним логи за неделю
                shared: true))
            .WriteTo.Console()
            .CreateLogger();

        try
        {
            Log.Information("============= ЗАПУСК KoFFBot (v2.0 Modernized) =============");

            string? botToken = Environment.GetEnvironmentVariable("BOT_TOKEN")?.Trim('"', '\'', ' ');
            string? apiSecret = Environment.GetEnvironmentVariable("API_SECRET")?.Trim('"', '\'', ' ');
            string? adminIdStr = Environment.GetEnvironmentVariable("ADMIN_TG_ID")?.Trim('"', '\'', ' ');

            if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(apiSecret))
            {
                Log.Fatal("КРИТИЧЕСКАЯ ОШИБКА: Не найден BOT_TOKEN или API_SECRET!");
                return;
            }

            var builder = WebApplication.CreateBuilder(args);
            builder.Host.UseSerilog(); // Заменяем стандартный логгер

            builder.WebHost.ConfigureKestrel(serverOptions => { serverOptions.ListenAnyIP(5000); });
            builder.Services.AddCors(options => { options.AddPolicy("AllowWebApp", policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()); });

            string dbPath = Path.Combine(AppContext.BaseDirectory, "koffbot_data.db");
            
            // Регистрируем разделенные контексты (используют один файл БД, но разные наборы таблиц)
            builder.Services.AddDbContext<VpnDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
            builder.Services.AddDbContext<GameDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));

            builder.Services.AddSingleton<ITelegramBotClient>(sp => new TelegramBotClient(botToken));
            builder.Services.AddTransient<SubscriptionGenerator>();
            builder.Services.AddScoped<GameService>();
            builder.Services.AddSingleton<UpdateHandler>();
            builder.Services.AddHostedService<TelegramBotWorker>();

            var app = builder.Build();

            app.InitializeDatabase();

            app.UseSerilogRequestLogging(); // Логирование HTTP запросов через Serilog

            app.UseCors("AllowWebApp");
            app.UseDefaultFiles();
            app.UseStaticFiles();

            app.Use(async (context, next) =>
            {
                var path = context.Request.Path.Value?.ToLower() ?? "";
                if (path.StartsWith("/api/webapp") || path.StartsWith("/api/game") || path.StartsWith("/sub/") || path.EndsWith(".html") || path.EndsWith(".js") || path.EndsWith(".css") || path == "/")
                {
                    await next(context);
                    return;
                }

                string safeSecret = apiSecret?.Trim('\r', '\n', ' ') ?? "";
                if (!context.Request.Headers.TryGetValue("X-API-KEY", out var extractedApiKey) || extractedApiKey.ToString().Trim('\r', '\n', ' ') != safeSecret)
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync("Доступ запрещен.");
                    return;
                }
                await next(context);
            });

            app.MapSyncEndpoints();
            app.MapWebAppEndpoints(adminIdStr);
            app.MapGameEndpoints();

            app.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Приложение упало при запуске");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}