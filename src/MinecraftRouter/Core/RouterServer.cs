using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftRouter.Core
{
    public enum RouterLogLevel { Debug, Info, Warning, Error }

    /// <summary>Text shown to players. {name} = server name, {host} = address they typed.</summary>
    public sealed class RouterMessages
    {
        public string UnknownHost = "§cThere's no server at §f{host}§c.";
        public string Offline = "§c{name} is offline.";
        public string Sleeping = "§e{name} is asleep. §7Join to wake it up.";
        public string Starting = "§e{name} is starting up. §7Try again in a minute.";
        public string VersionName = "Router";

        public static string Format(string template, Route route, string host)
            => (template ?? "").Replace("{name}", route?.DisplayName ?? "").Replace("{host}", host ?? "");
    }

    /// <summary>Everything the server needs from its owner (the AMP plugin, or a test).</summary>
    public interface IRouterHost
    {
        RouteTable Routes { get; }
        RouterMessages Messages { get; }
        bool WakeOnJoin { get; }
        /// <summary>True while a wake started by the router is still in progress for this route.</summary>
        bool IsWaking(Route route);
        /// <summary>Ask the owner to start the server behind this route. Must not block.</summary>
        void RequestWake(Route route, string playerAddress);
        void Log(RouterLogLevel level, string message);
    }

    public sealed class RouterServer : IDisposable
    {
        private readonly IRouterHost _host;
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private readonly ConcurrentDictionary<Guid, int> _active = new ConcurrentDictionary<Guid, int>();
        private long _totalConnections;

        public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(5);
        public TimeSpan BackendConnectTimeout { get; set; } = TimeSpan.FromSeconds(3);

        public IPEndPoint LocalEndpoint { get; private set; }
        public bool IsRunning => _listener != null;
        public long TotalConnections => Interlocked.Read(ref _totalConnections);
        public int ActiveFor(Guid instanceId) => _active.TryGetValue(instanceId, out var n) ? n : 0;

        public RouterServer(IRouterHost host) { _host = host; }

        public void Start(IPAddress address, int port)
        {
            Stop();
            var listener = new TcpListener(address, port);
            if (address.Equals(IPAddress.IPv6Any)) listener.Server.DualMode = true;
            listener.Start(512);
            _listener = listener;
            LocalEndpoint = (IPEndPoint)listener.LocalEndpoint;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _ = Task.Run(() => AcceptLoop(listener, ct));
            _host.Log(RouterLogLevel.Info, $"Minecraft Router listening on {LocalEndpoint}.");
        }

        public void Stop()
        {
            var l = _listener;
            _listener = null;
            if (l == null) return;
            try { _cts?.Cancel(); } catch { }
            try { l.Stop(); } catch { }
            _host.Log(RouterLogLevel.Info, "Minecraft Router stopped listening.");
        }

        public void Dispose() => Stop();

        private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException ex)
                {
                    if (ct.IsCancellationRequested) return;
                    _host.Log(RouterLogLevel.Warning, $"Accept failed: {ex.Message}");
                    await Task.Delay(100).ConfigureAwait(false);
                    continue;
                }
                Interlocked.Increment(ref _totalConnections);
                _ = Task.Run(() => HandleClientAsync(client, ct));
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken serverCt)
        {
            var remote = client.Client.RemoteEndPoint as IPEndPoint;
            var local = client.Client.LocalEndPoint as IPEndPoint;
            string who = remote == null ? "?" : $"{(remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address)}:{remote.Port}";
            TcpClient backend = null;
            Route route = null;
            bool counted = false;
            try
            {
                client.NoDelay = true;
                var cs = client.GetStream();

                Handshake hs;
                using (var hsCts = CancellationTokenSource.CreateLinkedTokenSource(serverCt))
                {
                    hsCts.CancelAfter(HandshakeTimeout);
                    try { hs = await McProtocol.ReadHandshakeAsync(cs, hsCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    catch (EndOfStreamException) { return; }
                    catch (InvalidDataException ex)
                    {
                        _host.Log(RouterLogLevel.Debug, $"{who}: not a Minecraft handshake ({ex.Message}).");
                        return;
                    }
                }
                if (hs == null) return; // legacy ping, nothing to route on

                var table = _host.Routes ?? RouteTable.Empty;
                route = table.Resolve(hs.Hostname);
                var msgs = _host.Messages ?? new RouterMessages();

                if (route == null)
                {
                    if (!hs.IsStatus)
                        _host.Log(RouterLogLevel.Info, $"{who} tried to join unknown address '{hs.Hostname}'.");
                    await ReplyAsync(cs, hs, msgs.UnknownHost, null, serverCt).ConfigureAwait(false);
                    return;
                }

                backend = await ConnectBackendAsync(route, serverCt).ConfigureAwait(false);
                if (backend == null)
                {
                    await HandleUnavailableAsync(cs, hs, route, who, msgs, serverCt).ConfigureAwait(false);
                    return;
                }

                if (!hs.IsStatus)
                    _host.Log(RouterLogLevel.Info, $"{who} -> {hs.Hostname} -> {route.DisplayName} ({route.Backend})");

                backend.NoDelay = true;
                var bs = backend.GetStream();
                if (route.SendProxyProtocol)
                {
                    var header = ProxyProtocol.BuildV2(remote, local);
                    await bs.WriteAsync(header, serverCt).ConfigureAwait(false);
                }
                await bs.WriteAsync(hs.RawBytes, serverCt).ConfigureAwait(false);

                _active.AddOrUpdate(route.InstanceId, 1, (_, n) => n + 1);
                counted = true;
                await PumpAsync(cs, bs, serverCt).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is OperationCanceledException || ex is ObjectDisposedException)
            {
                // Connection dropped by either side; normal when a player quits.
                _host.Log(RouterLogLevel.Debug, $"{who}: connection closed ({ex.GetType().Name}: {ex.Message})");
            }
            catch (Exception ex)
            {
                _host.Log(RouterLogLevel.Warning, $"{who}: connection error: {ex.Message}");
            }
            finally
            {
                if (counted) _active.AddOrUpdate(route.InstanceId, 0, (_, n) => Math.Max(0, n - 1));
                try { backend?.Dispose(); } catch { }
                try { client.Dispose(); } catch { }
            }
        }

        private async Task<TcpClient> ConnectBackendAsync(Route route, CancellationToken ct)
        {
            // The parameterless TcpClient picks the address family from the resolved address,
            // so it works on hosts without IPv6 as well as for IPv6 backends.
            var c = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(BackendConnectTimeout);
            try
            {
                await c.ConnectAsync(route.BackendHost, route.BackendPort, cts.Token).ConfigureAwait(false);
                return c;
            }
            catch (Exception)
            {
                c.Dispose();
                return null;
            }
        }

        private async Task HandleUnavailableAsync(NetworkStream cs, Handshake hs, Route route, string who, RouterMessages msgs, CancellationToken ct)
        {
            bool waking = _host.IsWaking(route);
            if (hs.IsStatus)
            {
                string motd = waking ? msgs.Starting : (_host.WakeOnJoin ? msgs.Sleeping : msgs.Offline);
                await ReplyAsync(cs, hs, motd, route, ct).ConfigureAwait(false);
                return;
            }

            if (_host.WakeOnJoin)
            {
                if (!waking)
                {
                    _host.Log(RouterLogLevel.Info, $"{who} wants {route.DisplayName}, which isn't running. Waking it up.");
                    _host.RequestWake(route, who);
                }
                await ReplyAsync(cs, hs, msgs.Starting, route, ct).ConfigureAwait(false);
            }
            else
            {
                _host.Log(RouterLogLevel.Info, $"{who} wants {route.DisplayName}, which isn't running.");
                await ReplyAsync(cs, hs, msgs.Offline, route, ct).ConfigureAwait(false);
            }
        }

        private async Task ReplyAsync(NetworkStream cs, Handshake hs, string template, Route route, CancellationToken ct)
        {
            var text = RouterMessages.Format(template, route, hs.Hostname);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                if (hs.IsStatus)
                {
                    var version = (_host.Messages ?? new RouterMessages()).VersionName;
                    await McProtocol.ServeStatusAsync(cs, hs.ProtocolVersion, version, text, cts.Token).ConfigureAwait(false);
                }
                else
                {
                    await cs.WriteAsync(McProtocol.BuildLoginDisconnect(text), cts.Token).ConfigureAwait(false);
                    await cs.FlushAsync(cts.Token).ConfigureAwait(false);
                    // Give the client a moment to read the reason before we close.
                    await Task.Delay(200, cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is OperationCanceledException || ex is EndOfStreamException || ex is InvalidDataException) { }
        }

        private static async Task PumpAsync(NetworkStream a, NetworkStream b, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var t1 = CopyAsync(a, b, cts.Token);
            var t2 = CopyAsync(b, a, cts.Token);
            await Task.WhenAny(t1, t2).ConfigureAwait(false);
            cts.Cancel();
            try { await Task.WhenAll(t1, t2).ConfigureAwait(false); } catch { }
        }

        private static async Task CopyAsync(Stream from, Stream to, CancellationToken ct)
        {
            var buf = new byte[32 * 1024];
            try
            {
                while (true)
                {
                    int n = await from.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
                    if (n == 0) return;
                    await to.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is OperationCanceledException || ex is ObjectDisposedException) { }
        }
    }
}
