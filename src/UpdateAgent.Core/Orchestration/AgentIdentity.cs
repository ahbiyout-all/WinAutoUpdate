using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace UpdateAgent.Core.Orchestration;

/// <summary>PC 고유 식별자와 네트워크 정보.</summary>
public static class AgentIdentity
{
    private static readonly Lazy<string> CachedId = new(ComputeId);
    private static readonly Lazy<IReadOnlyList<string>> CachedMacs = new(GetMacAddressesCore);

    public static string GetAgentId() => CachedId.Value;

    public static string GetMachineName() => Environment.MachineName;

    public static IReadOnlyList<string> GetMacAddresses() => CachedMacs.Value;

    public static string? GetIpAddress()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                var address = nic.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (address is not null) return address.Address.ToString();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string ComputeId()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (key?.GetValue("MachineGuid") is string guid && !string.IsNullOrWhiteSpace(guid))
            {
                return guid.Trim().ToLowerInvariant();
            }
        }
        catch
        {
            // fallback
        }

        var text = $"{Environment.MachineName}|{string.Join(",", CachedMacs.Value)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    private static IReadOnlyList<string> GetMacAddressesCore()
    {
        var results = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var mac = nic.GetPhysicalAddress()?.ToString();
                if (!string.IsNullOrWhiteSpace(mac) && mac != "000000000000")
                {
                    results.Add(mac);
                }
            }
        }
        catch
        {
            // ignore
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
