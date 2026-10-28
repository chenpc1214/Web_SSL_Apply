using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace SslWebConsole.Services;

/// <summary>
/// 取代 nicsrs_common.ps1 的 Invoke-NicSrsApi（原本用 curl.exe，避免 PS5.1 Invoke-RestMethod
/// 卡在 proxy 偵測/憑證撤銷檢查）。這裡直接用 HttpClient 複刻同樣的連線行為：
/// 不走 proxy、強制 IPv4、關閉憑證撤銷檢查、TLS1.2、連線 15s / 整體 120s 逾時。
/// </summary>
public sealed class NicSrsApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly NicSrsConfigLoader _configLoader;

    public NicSrsApiClient(NicSrsConfigLoader configLoader)
    {
        _configLoader = configLoader;

        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            },
            ConnectCallback = async (context, cancellationToken) =>
            {
                // 強制 IPv4（對應 curl 的 -4），避免 IPv6 連不到時的 fallback 等待。
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, cancellationToken);
                if (addresses.Length == 0)
                    throw new SocketException((int)SocketError.HostNotFound);

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(addresses[0], context.DnsEndPoint.Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
    }

    /// <summary>回傳解析後的 JSON（等同 PS 的 ConvertFrom-Json 物件），失敗回 null（不丟例外，呼叫端沿用「查詢失敗當 ERROR」的行為）。</summary>
    public async Task<JsonNode?> CallAsync(string endpoint, Dictionary<string, string>? fields = null)
    {
        var cfg = _configLoader.LoadRequireApiToken();
        var uri = cfg.ApiBase.TrimEnd('/') + endpoint;

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(cfg.ApiToken, Encoding.UTF8), "api_token");
        if (fields is not null)
        {
            foreach (var (k, v) in fields)
                content.Add(new StringContent(v ?? "", Encoding.UTF8), k);
        }

        try
        {
            using var response = await _http.PostAsync(uri, content);
            var raw = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            return JsonNode.Parse(raw);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
