using System.Net.Sockets;
using System.Reflection;
using UnityEngine;

// Unity's Mono takes Socket.OSSupportsIPv6 from a build-time platform flag instead of probing the kernel. On a
// host booted with ipv6.disable=1, SocketsHttpHandler then creates an AF_INET6 socket for every HttpClient connect
// and fails (map upload, map image upload, analytics). Temporary: drop this mod once Unity's runtime probes IPv6
// support or the host enables the IPv6 address family again.
public class Ipv4Only : IHarmonyModHooks
{
    public void OnLoaded(OnHarmonyModLoadedArgs args)
    {
        if (!Socket.OSSupportsIPv6)
        {
            return;
        }
        try
        {
            new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp).Close();
            return;
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressFamilyNotSupported)
        {
        }

        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        FieldInfo osSupportsIPv6 = typeof(Socket).GetField("s_OSSupportsIPv6", flags);
        FieldInfo supportsIPv6 = typeof(Socket).GetField("s_SupportsIPv6", flags);
        if (osSupportsIPv6 == null || supportsIPv6 == null)
        {
            Debug.LogError("[Ipv4Only] Socket IPv6 flags not found in this Mono build; HTTP requests will keep failing");
            return;
        }
        osSupportsIPv6.SetValue(null, false);
        supportsIPv6.SetValue(null, false);
        Debug.Log("[Ipv4Only] Kernel has no IPv6 address family; Mono sockets now use IPv4 only");
    }

    public void OnUnloaded(OnHarmonyModUnloadedArgs args)
    {
    }
}
