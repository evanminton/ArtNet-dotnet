using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ArtNet.Networking;

/// <summary>An IPv4 address of a local network interface with its directed broadcast address.</summary>
public sealed record ArtNetNetworkInterface(string Name, string Description, IPAddress Address, IPAddress Mask, IPAddress Broadcast, byte[] Mac, bool IsUp, bool IsLoopback)
{
    public string MacText => Mac.Length == 6 ? string.Join(":", Mac.Select(b => b.ToString("X2"))) : string.Empty;

    /// <summary>True for the Art-Net primary (2.x.x.x) or secondary (10.x.x.x) class A networks.</summary>
    public bool IsArtNetNetwork => Address.GetAddressBytes()[0] is 2 or 10;

    public override string ToString() => $"{Name} – {Address}/{Mask} (broadcast {Broadcast})";

    /// <summary>IPv4 interfaces (up ones first).</summary>
    public static IReadOnlyList<ArtNetNetworkInterface> GetAll(bool includeLoopback = false)
    {
        var list = new List<ArtNetNetworkInterface>();
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return list; }
        catch (PlatformNotSupportedException) { return list; }

        foreach (var nic in nics)
        {
            bool loopback = nic.NetworkInterfaceType == NetworkInterfaceType.Loopback;
            if (loopback && !includeLoopback) continue;
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); } catch { continue; }
            byte[] mac;
            try { mac = nic.GetPhysicalAddress().GetAddressBytes(); } catch { mac = []; }
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                IPAddress mask;
                try { mask = ua.IPv4Mask; } catch { mask = IPAddress.Any; }
                if (mask is null || mask.Equals(IPAddress.Any)) mask = MaskFromPrefix(ua.PrefixLength);
                list.Add(new ArtNetNetworkInterface(nic.Name, nic.Description, ua.Address, mask, BroadcastOf(ua.Address, mask),
                    mac, nic.OperationalStatus == OperationalStatus.Up, loopback));
            }
        }
        return list.OrderByDescending(i => i.IsUp).ThenBy(i => i.IsLoopback).ToArray();
    }

    /// <summary>The first up, non-loopback interface (preferring the Art-Net 2.x / 10.x networks), or null.</summary>
    public static ArtNetNetworkInterface? GetDefault() =>
        GetAll().Where(i => i.IsUp && !i.IsLoopback).OrderByDescending(i => i.IsArtNetNetwork).FirstOrDefault();

    /// <summary>The interface owning <paramref name="address"/>, or null.</summary>
    public static ArtNetNetworkInterface? Find(IPAddress address) =>
        GetAll(includeLoopback: true).FirstOrDefault(i => i.Address.Equals(address));

    /// <summary>Directed broadcast address: address | ~mask.</summary>
    public static IPAddress BroadcastOf(IPAddress address, IPAddress mask)
    {
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        var b = new byte[4];
        for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
        return new IPAddress(b);
    }

    public static IPAddress MaskFromPrefix(int prefix)
    {
        prefix = Math.Clamp(prefix, 0, 32);
        uint m = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        return new IPAddress([(byte)(m >> 24), (byte)(m >> 16), (byte)(m >> 8), (byte)m]);
    }

    /// <summary>True when <paramref name="a"/> and <paramref name="b"/> are on the same network for <paramref name="mask"/>.</summary>
    public static bool SameNetwork(IPAddress a, IPAddress b, IPAddress mask)
    {
        var x = a.GetAddressBytes(); var y = b.GetAddressBytes(); var m = mask.GetAddressBytes();
        for (int i = 0; i < 4; i++) if ((x[i] & m[i]) != (y[i] & m[i])) return false;
        return true;
    }

    /// <summary>
    /// The factory default Art-Net IP: A = 2 (network switch off) or 10 (on), B = MAC[3] + OEM Hi + OEM Lo,
    /// C = MAC[4], D = MAC[5]; mask 255.0.0.0.
    /// </summary>
    public static IPAddress DefaultArtNetAddress(ReadOnlySpan<byte> mac, ushort oem, bool networkSwitch = false)
    {
        if (mac.Length < 6) throw new ArgumentException("MAC address must be 6 bytes.", nameof(mac));
        byte a = networkSwitch ? (byte)10 : (byte)2;
        byte b = (byte)(mac[3] + (oem >> 8) + (oem & 0xFF));
        return new IPAddress([a, b, mac[4], mac[5]]);
    }

    /// <summary>Subnet mask of the factory default Art-Net addressing (255.0.0.0).</summary>
    public static IPAddress DefaultArtNetMask => IPAddress.Parse("255.0.0.0");
}
