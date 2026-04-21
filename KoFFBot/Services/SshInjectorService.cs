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
        string rootPassword, // Игнорируется для безопасности. Используем SSH-ключи.
        string uuid,
        string base64Payload,
        string email)
    {
        try
        {
            _logger.LogInformation($"Начинаем защищенный Hot-Inject (SSH Key) для {email} на сервер {serverIp}...");

            // === ZERO TRUST: АВТОРИЗАЦИЯ ПО КЛЮЧАМ ===
            string sshKeyPath = Environment.GetEnvironmentVariable("SSH_PRIVATE_KEY_PATH") ?? "/root/.ssh/id_ed25519";
            var privateKeyFile = new PrivateKeyFile(sshKeyPath);
            using var client = new SshClient(serverIp, "root", new[] { privateKeyFile });

            await client.ConnectAsync(CancellationToken.None);

            if (!client.IsConnected)
            {
                _logger.LogError("Не удалось подключиться по SSH.");
                return false;
            }

            // 1. Записываем Base64 подписку для Nginx
            var cmd1 = client.CreateCommand($"echo '{base64Payload}' > /var/www/xray-sub/{uuid}");
            await Task.Run(() => cmd1.Execute());

            string injectScript = $@"
if ! command -v jq &> /dev/null; then apt-get install -y jq; fi
CONFIG_PATH='/etc/sing-box/config.json'

jq '(.inbounds[] | select(.type == ""vless"") | .users) += [{{""name"": ""KoFFBot_{email}"", ""uuid"": ""{uuid}"", ""flow"": ""xtls-rprx-vision""}}]' $CONFIG_PATH > /tmp/sb_tmp.json && mv /tmp/sb_tmp.json $CONFIG_PATH
jq '(.inbounds[] | select(.type == ""hysteria2"") | .users) += [{{""name"": ""KoFFBot_{email}"", ""password"": ""{uuid}""}}]' $CONFIG_PATH > /tmp/sb_tmp.json && mv /tmp/sb_tmp.json $CONFIG_PATH
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