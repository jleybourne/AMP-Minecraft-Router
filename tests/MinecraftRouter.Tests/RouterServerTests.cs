using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MinecraftRouter.Core;
using Xunit;

namespace MinecraftRouter.Tests
{
    public class RouterServerTests : IDisposable
    {
        private sealed class FakeHost : IRouterHost
        {
            public RouteTable Routes { get; set; } = RouteTable.Empty;
            public RouterMessages Messages { get; } = new RouterMessages();
            public bool WakeOnJoin { get; set; } = true;
            public readonly List<string> Woken = new List<string>();
            public bool IsWaking(Route route) => Woken.Contains(route.InstanceName);
            public void RequestWake(Route route, string playerAddress) { lock (Woken) Woken.Add(route.InstanceName); }
            public void Log(RouterLogLevel level, string message) { }
        }

        private readonly FakeHost _host = new FakeHost();
        private readonly RouterServer _server;
        private readonly TcpListener _backend;

        public RouterServerTests()
        {
            _backend = new TcpListener(IPAddress.Loopback, 0);
            _backend.Start();
            _server = new RouterServer(_host) { BackendConnectTimeout = TimeSpan.FromSeconds(1) };
            _server.Start(IPAddress.Loopback, 0);
        }

        public void Dispose()
        {
            _server.Dispose();
            _backend.Stop();
        }

        private int BackendPort => ((IPEndPoint)_backend.LocalEndpoint).Port;

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        private void UseRoutes(string overrides = "", bool proxy = false, int? port = null, string def = "")
        {
            var inst = new InstanceDescriptor
            {
                InstanceId = Guid.NewGuid(), InstanceName = "OceanBlock01", FriendlyName = "OceanBlock",
                Module = "Minecraft", GamePort = port ?? BackendPort, BindAddress = "0.0.0.0"
            };
            _host.Routes = RouteBuilder.Build(new[] { inst }, new RoutingOptions
            {
                PrimaryDomain = "domain.com", Overrides = overrides, ProxyProtocolForAll = proxy, DefaultInstance = def
            });
        }

        private async Task<NetworkStream> ConnectAsync()
        {
            var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, _server.LocalEndpoint.Port);
            return c.GetStream();
        }

        private static async Task<byte[]> ReadAtLeast(Stream s, int count, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            var buf = new byte[count];
            await s.ReadExactlyAsync(buf, cts.Token);
            return buf;
        }

        private static async Task<string> ReadStatusJson(Stream s)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var (_, body) = await McProtocol.ReadPacketAsync(s, 1 << 16, cts.Token);
            int pos = 0;
            Assert.Equal(0x00, McProtocol.ReadVarInt(body, ref pos, body.Length));
            return McProtocol.ReadString(body, ref pos, body.Length, 1 << 16);
        }

        [Fact]
        public async Task ForwardsHandshakeAndTrafficToTheMatchingServer()
        {
            UseRoutes();
            var handshake = McProtocol.BuildHandshake(767, "OceanBlock.domain.com", 25565, 2);
            var loginStart = McProtocol.BuildPacket(0x00, b => McProtocol.WriteString(b, "Steve"));

            var acceptTask = _backend.AcceptTcpClientAsync();
            var client = await ConnectAsync();
            await client.WriteAsync(handshake.Concat(loginStart).ToArray());

            using var backendConn = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
            var bs = backendConn.GetStream();
            var got = await ReadAtLeast(bs, handshake.Length + loginStart.Length, TimeSpan.FromSeconds(5));
            Assert.Equal(handshake.Concat(loginStart).ToArray(), got);

            // And back the other way.
            var reply = Encoding.ASCII.GetBytes("hello player");
            await bs.WriteAsync(reply);
            Assert.Equal(reply, await ReadAtLeast(client, reply.Length, TimeSpan.FromSeconds(5)));
            client.Dispose();
        }

        [Fact]
        public async Task SendsProxyProtocolHeaderFirstWhenEnabled()
        {
            UseRoutes(proxy: true);
            var handshake = McProtocol.BuildHandshake(767, "oceanblock.domain.com", 25565, 2);

            var acceptTask = _backend.AcceptTcpClientAsync();
            var client = await ConnectAsync();
            await client.WriteAsync(handshake);

            using var backendConn = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
            var got = await ReadAtLeast(backendConn.GetStream(), 28 + handshake.Length, TimeSpan.FromSeconds(5));
            Assert.Equal(0x0D, got[0]);
            Assert.Equal(0x11, got[13]);
            Assert.Equal(new byte[] { 127, 0, 0, 1 }, got.Skip(16).Take(4));
            Assert.Equal(handshake, got.Skip(28).ToArray());
            client.Dispose();
        }

        [Fact]
        public async Task UnknownHostGetsAStatusReplyAndPong()
        {
            UseRoutes();
            var client = await ConnectAsync();
            await client.WriteAsync(McProtocol.BuildHandshake(767, "nope.domain.com", 25565, 1));
            await client.WriteAsync(McProtocol.BuildPacket(0x00, null));

            var json = await ReadStatusJson(client);
            using var doc = JsonDocument.Parse(json);
            Assert.Contains("nope.domain.com", doc.RootElement.GetProperty("description").GetProperty("text").GetString());
            Assert.Equal(767, doc.RootElement.GetProperty("version").GetProperty("protocol").GetInt32());

            var payload = BitConverter.GetBytes(123456789L);
            await client.WriteAsync(McProtocol.BuildPacket(0x01, b => b.Write(payload)));
            var pong = await ReadAtLeast(client, 10, TimeSpan.FromSeconds(5));
            Assert.Equal(9, pong[0]);
            Assert.Equal(0x01, pong[1]);
            Assert.Equal(payload, pong.Skip(2).ToArray());
            client.Dispose();
        }

        [Fact]
        public async Task OfflineServerShowsSleepingInServerList()
        {
            UseRoutes(port: FreePort());
            var client = await ConnectAsync();
            await client.WriteAsync(McProtocol.BuildHandshake(767, "oceanblock.domain.com", 25565, 1));
            await client.WriteAsync(McProtocol.BuildPacket(0x00, null));
            var json = await ReadStatusJson(client);
            Assert.Contains("OceanBlock is asleep", json);
            Assert.Empty(_host.Woken); // pings never wake
            client.Dispose();
        }

        [Fact]
        public async Task JoiningAnOfflineServerWakesItAndKicksWithMessage()
        {
            UseRoutes(port: FreePort());
            var client = await ConnectAsync();
            await client.WriteAsync(McProtocol.BuildHandshake(767, "oceanblock.domain.com", 25565, 2));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var (_, body) = await McProtocol.ReadPacketAsync(client, 1 << 16, cts.Token);
            int pos = 0;
            Assert.Equal(0x00, McProtocol.ReadVarInt(body, ref pos, body.Length));
            var reason = McProtocol.ReadString(body, ref pos, body.Length, 1 << 16);
            Assert.Contains("starting up", reason);
            Assert.Equal(new[] { "OceanBlock01" }, _host.Woken);
            client.Dispose();
        }

        [Fact]
        public async Task OfflineWithoutWakeSaysOffline()
        {
            UseRoutes(port: FreePort());
            _host.WakeOnJoin = false;
            var client = await ConnectAsync();
            await client.WriteAsync(McProtocol.BuildHandshake(767, "oceanblock.domain.com", 25565, 2));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var (_, body) = await McProtocol.ReadPacketAsync(client, 1 << 16, cts.Token);
            Assert.Contains("is offline", Encoding.UTF8.GetString(body));
            Assert.Empty(_host.Woken);
            client.Dispose();
        }

        [Fact]
        public async Task BareDomainGoesToDefaultServer()
        {
            UseRoutes(def: "OceanBlock01");
            var acceptTask = _backend.AcceptTcpClientAsync();
            var client = await ConnectAsync();
            var handshake = McProtocol.BuildHandshake(767, "domain.com", 25565, 2);
            await client.WriteAsync(handshake);
            using var backendConn = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(handshake, await ReadAtLeast(backendConn.GetStream(), handshake.Length, TimeSpan.FromSeconds(5)));
            client.Dispose();
        }

        [Fact]
        public async Task ManyConcurrentPlayersAreRoutedIndependently()
        {
            UseRoutes();
            const int players = 25;
            var accepted = Task.Run(async () =>
            {
                var conns = new List<TcpClient>();
                for (int i = 0; i < players; i++) conns.Add(await _backend.AcceptTcpClientAsync());
                // Echo each connection's bytes back.
                await Task.WhenAll(conns.Select(async c =>
                {
                    var s = c.GetStream();
                    var buf = new byte[4096];
                    int n;
                    while ((n = await s.ReadAsync(buf)) > 0) await s.WriteAsync(buf.AsMemory(0, n));
                }));
            });

            await Task.WhenAll(Enumerable.Range(0, players).Select(async i =>
            {
                var client = await ConnectAsync();
                var hs = McProtocol.BuildHandshake(767, "oceanblock.domain.com", 25565, 2);
                await client.WriteAsync(hs);
                Assert.Equal(hs, await ReadAtLeast(client, hs.Length, TimeSpan.FromSeconds(10)));
                var msg = Encoding.ASCII.GetBytes($"player-{i}");
                await client.WriteAsync(msg);
                Assert.Equal(msg, await ReadAtLeast(client, msg.Length, TimeSpan.FromSeconds(10)));
                client.Dispose();
            }));
        }
    }
}
