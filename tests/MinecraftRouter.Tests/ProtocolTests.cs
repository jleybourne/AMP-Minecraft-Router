using System.Net;
using MinecraftRouter.Core;
using Xunit;

namespace MinecraftRouter.Tests
{
    public class ProtocolTests
    {
        [Theory]
        [InlineData("OceanBlock.Domain.COM", "oceanblock.domain.com")]
        [InlineData("survival.domain.com.", "survival.domain.com")]
        [InlineData("survival.domain.com\0FML2\0", "survival.domain.com")]
        [InlineData("survival.domain.com\0FML3\0", "survival.domain.com")]
        [InlineData("survival.domain.com:25565", "survival.domain.com")]
        [InlineData("survival.domain.com///1.2.3.4:5555///123", "survival.domain.com")]
        [InlineData("", "")]
        public void NormalizeHostname(string raw, string expected) => Assert.Equal(expected, McProtocol.NormalizeHostname(raw));

        [Fact]
        public async Task HandshakeRoundTrips()
        {
            var bytes = McProtocol.BuildHandshake(767, "OceanBlock.domain.com\0FML3\0", 25565, 2);
            var hs = await McProtocol.ReadHandshakeAsync(new MemoryStream(bytes), CancellationToken.None);
            Assert.Equal(767, hs.ProtocolVersion);
            Assert.Equal("oceanblock.domain.com", hs.Hostname);
            Assert.Equal(25565, hs.Port);
            Assert.Equal(2, hs.NextState);
            Assert.Equal(bytes, hs.RawBytes);
        }

        [Fact]
        public async Task LegacyPingReturnsNull()
        {
            var hs = await McProtocol.ReadHandshakeAsync(new MemoryStream(new byte[] { 0xFE, 0x01, 0xFA }), CancellationToken.None);
            Assert.Null(hs);
        }

        [Fact]
        public async Task OversizedPacketIsRejected()
        {
            var ms = new MemoryStream();
            McProtocol.WriteVarInt(ms, 100_000);
            ms.Position = 0;
            await Assert.ThrowsAsync<InvalidDataException>(() => McProtocol.ReadHandshakeAsync(ms, CancellationToken.None));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(127)]
        [InlineData(128)]
        [InlineData(25565)]
        [InlineData(int.MaxValue)]
        [InlineData(-1)]
        public void VarIntRoundTrips(int value)
        {
            var ms = new MemoryStream();
            McProtocol.WriteVarInt(ms, value);
            var buf = ms.ToArray();
            int pos = 0;
            Assert.Equal(value, McProtocol.ReadVarInt(buf, ref pos, buf.Length));
            Assert.Equal(buf.Length, pos);
        }

        [Fact]
        public void ProxyV2HeaderIpv4()
        {
            var h = ProxyProtocol.BuildV2(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 51234), new IPEndPoint(IPAddress.Parse("10.0.0.2"), 25565));
            Assert.Equal(28, h.Length);
            Assert.Equal(new byte[] { 0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A }, h.Take(12));
            Assert.Equal(0x21, h[12]);
            Assert.Equal(0x11, h[13]);
            Assert.Equal(12, (h[14] << 8) | h[15]);
            Assert.Equal(new byte[] { 203, 0, 113, 7 }, h.Skip(16).Take(4));
            Assert.Equal(new byte[] { 10, 0, 0, 2 }, h.Skip(20).Take(4));
            Assert.Equal(51234, (h[24] << 8) | h[25]);
            Assert.Equal(25565, (h[26] << 8) | h[27]);
        }

        [Fact]
        public void ProxyV2MapsIpv4MappedAddresses()
        {
            var h = ProxyProtocol.BuildV2(new IPEndPoint(IPAddress.Parse("::ffff:203.0.113.7"), 1), new IPEndPoint(IPAddress.Parse("::ffff:10.0.0.2"), 2));
            Assert.Equal(0x11, h[13]);
        }

        [Fact]
        public void ProxyV2Ipv6()
        {
            var h = ProxyProtocol.BuildV2(new IPEndPoint(IPAddress.Parse("2001:db8::1"), 1), new IPEndPoint(IPAddress.Parse("10.0.0.2"), 2));
            Assert.Equal(0x21, h[13]);
            Assert.Equal(16 + 36, h.Length);
        }
    }
}
