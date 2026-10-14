using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace MinecraftRouter.Core
{
    /// <summary>
    /// HAProxy PROXY protocol v2 header, so a backend that understands it (Paper with
    /// proxy-protocol enabled, Velocity, Bungee, or a mod on Forge/Fabric) sees the
    /// player's real IP instead of the router's.
    /// </summary>
    public static class ProxyProtocol
    {
        private static readonly byte[] Signature = { 0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A };

        public static byte[] BuildV2(IPEndPoint source, IPEndPoint destination)
        {
            if (source == null || destination == null) return BuildLocal();

            var src = source.Address.IsIPv4MappedToIPv6 ? source.Address.MapToIPv4() : source.Address;
            var dst = destination.Address.IsIPv4MappedToIPv6 ? destination.Address.MapToIPv4() : destination.Address;

            // Both addresses must share a family. Promote the IPv4 one if they differ.
            if (src.AddressFamily != dst.AddressFamily)
            {
                if (src.AddressFamily == AddressFamily.InterNetwork) src = src.MapToIPv6();
                if (dst.AddressFamily == AddressFamily.InterNetwork) dst = dst.MapToIPv6();
            }

            bool v4 = src.AddressFamily == AddressFamily.InterNetwork;
            var ms = new MemoryStream();
            ms.Write(Signature, 0, Signature.Length);
            ms.WriteByte(0x21);                 // version 2, PROXY command
            ms.WriteByte(v4 ? (byte)0x11 : (byte)0x21); // TCP over IPv4 / IPv6
            int addrLen = v4 ? 12 : 36;
            ms.WriteByte((byte)(addrLen >> 8));
            ms.WriteByte((byte)(addrLen & 0xFF));
            var s = src.GetAddressBytes();
            var d = dst.GetAddressBytes();
            ms.Write(s, 0, s.Length);
            ms.Write(d, 0, d.Length);
            ms.WriteByte((byte)(source.Port >> 8));
            ms.WriteByte((byte)(source.Port & 0xFF));
            ms.WriteByte((byte)(destination.Port >> 8));
            ms.WriteByte((byte)(destination.Port & 0xFF));
            return ms.ToArray();
        }

        /// <summary>A LOCAL header (no address), used when the client's address is unknown.</summary>
        public static byte[] BuildLocal()
        {
            var b = new byte[16];
            Array.Copy(Signature, b, Signature.Length);
            b[12] = 0x20; // version 2, LOCAL
            b[13] = 0x00; // UNSPEC
            return b;
        }
    }
}
