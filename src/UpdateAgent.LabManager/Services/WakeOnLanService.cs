using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using UpdateAgent.LabManager.Configuration;

namespace UpdateAgent.LabManager.Services;

public sealed record WakeResult
{
    public required string MacAddress { get; init; }
    public bool Sent { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>Wake-on-LAN 매직 패킷 전송. 같은 서브넷의 학생 PC에만 유효하다.</summary>
public sealed class WakeOnLanService
{
    private readonly IOptionsMonitor<LabManagerOptions> _options;
    private readonly ILogger<WakeOnLanService> _logger;

    public WakeOnLanService(IOptionsMonitor<LabManagerOptions> options, ILogger<WakeOnLanService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WakeResult>> WakeAsync(
        IEnumerable<string> macAddresses,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;
        var results = new List<WakeResult>();

        var targets = new List<IPEndPoint>
        {
            new(IPAddress.Broadcast, 9),
        };

        foreach (var port in options.WakePorts.Distinct())
        {
            targets.Add(new IPEndPoint(IPAddress.Broadcast, port));
        }

        // 같은 /24 서브넷의 지향 브로드캐스트도 함께 보낸다.
        if (IPAddress.TryParse(ipAddress, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            bytes[3] = 255;
            var directed = new IPAddress(bytes);
            foreach (var port in options.WakePorts.Distinct())
            {
                targets.Add(new IPEndPoint(directed, port));
            }
        }

        targets = targets.Distinct().ToList();

        foreach (var macText in macAddresses.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TryParseMac(macText, out var mac))
            {
                results.Add(new WakeResult
                {
                    MacAddress = macText,
                    Sent = false,
                    Message = "MAC 주소 형식이 올바르지 않습니다.",
                });
                continue;
            }

            var packet = BuildMagicPacket(mac);
            var sent = false;
            string message = "";

            for (var repeat = 0; repeat < Math.Max(1, options.WakeRepeatCount) && !sent; repeat++)
            {
                foreach (var target in targets)
                {
                    try
                    {
                        using var client = new UdpClient(AddressFamily.InterNetwork);
                        client.EnableBroadcast = true;
                        await client.SendAsync(packet, target, cancellationToken);
                        sent = true;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        message = ex.Message;
                    }
                }

                if (!sent && repeat < options.WakeRepeatCount - 1)
                {
                    try
                    {
                        await Task.Delay(150, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                }
            }

            if (sent)
            {
                message = $"{targets.Count}개 대상으로 매직 패킷을 전송했습니다.";
                _logger.LogInformation("WoL 전송: {Mac} ({Targets} targets)", macText, targets.Count);
            }
            else
            {
                _logger.LogWarning("WoL 실패: {Mac} - {Message}", macText, message);
            }

            results.Add(new WakeResult
            {
                MacAddress = macText,
                Sent = sent,
                Message = message,
            });
        }

        return results;
    }

    private static byte[] BuildMagicPacket(byte[] mac)
    {
        var packet = new byte[6 + 16 * mac.Length];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var i = 1; i <= 16; i++)
        {
            Buffer.BlockCopy(mac, 0, packet, i * mac.Length, mac.Length);
        }

        return packet;
    }

    private static bool TryParseMac(string text, out byte[] mac)
    {
        mac = [];
        var cleaned = new string(text.Where(Uri.IsHexDigit).ToArray());
        if (cleaned.Length != 12) return false;

        mac = Convert.FromHexString(cleaned);
        // 00:00:00:00:00:00 같은 가짜 MAC은 제외한다.
        return mac.Any(b => b != 0);
    }
}
