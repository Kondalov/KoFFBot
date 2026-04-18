using KoFFBot.Domain;
using Renci.SshNet;

namespace KoFFBot.Services;

public class SshInjectorService
{
    private readonly ILogger<SshInjectorService> _logger;

    public SshInjectorService(ILogger<SshInjectorService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Выполняет "горячее" внедрение подписки на сервер.
    /// Записывает файл для Nginx и, если это Sing-box, перезагружает конфиг без обрыва других соединений.
    /// </summary>
    public async Task<bool> InjectSubscriptionAsync(
        string serverIp,
        string rootPassword, // В реальном проекте пароли лучше хранить в защищенном хранилище, но для демона на том же сервере можно брать из конфига
        string uuid,
        string base64Payload,
        string email)
    {
        try
        {
            _logger.LogInformation($"Начинаем Hot-Inject для {email} на сервер {serverIp}...");

            using var client = new SshClient(serverIp, "root", rootPassword);
            await client.ConnectAsync(CancellationToken.None);

            if (!client.IsConnected)
            {
                _logger.LogError("Не удалось подключиться по SSH.");
                return false;
            }

            // 1. Записываем Base64 подписку для Nginx
            var cmd1 = client.CreateCommand($"echo '{base64Payload}' > /var/www/xray-sub/{uuid}");
            await Task.Run(() => cmd1.Execute());

            // 2. Горячее обновление Sing-box (Без перезагрузки службы!)
            // В Sing-box нет удобного gRPC API как в Xray, но можно изменять config.json скриптом (jq) и делать reload.
            // ВАЖНО: Это временный "грязный" инжект. Когда KoFFPanel включится, она перепишет конфиг красиво.

            string injectScript = $@"
# Проверяем, установлен ли jq (утилита для работы с JSON в bash)
if ! command -v jq &> /dev/null; then apt-get install -y jq; fi

CONFIG_PATH='/etc/sing-box/config.json'

# Внедряем пользователя в VLESS (ищем первый inbound типа vless)
jq '(.inbounds[] | select(.type == ""vless"") | .users) += [{{""name"": ""KoFFBot_{email}"", ""uuid"": ""{uuid}"", ""flow"": ""xtls-rprx-vision""}}]' $CONFIG_PATH > /tmp/sb_tmp.json && mv /tmp/sb_tmp.json $CONFIG_PATH

# Внедряем пользователя в Hysteria 2
jq '(.inbounds[] | select(.type == ""hysteria2"") | .users) += [{{""name"": ""KoFFBot_{email}"", ""password"": ""{uuid}""}}]' $CONFIG_PATH > /tmp/sb_tmp.json && mv /tmp/sb_tmp.json $CONFIG_PATH

# Мягко перезагружаем Sing-box (не прерывает текущие соединения)
systemctl reload sing-box || systemctl restart sing-box
";
            var cmd2 = client.CreateCommand(injectScript);
            await Task.Run(() => cmd2.Execute());

            client.Disconnect();
            _logger.LogInformation($"Hot-Inject для {email} прошел успешно!");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Ошибка SSH Инжекта для {email}");
            return false;
        }
    }
}