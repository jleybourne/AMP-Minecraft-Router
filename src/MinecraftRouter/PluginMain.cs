using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using InstanceManagerPlugin;
using MinecraftRouter.Core;
using ModuleShared;

namespace MinecraftRouter
{
    // AMP loads the class deriving from AMPPlugin whose namespace equals the plugin name, so
    // this must stay in namespace "MinecraftRouter". Constructor arguments are injected.
    public class PluginMain : AMPPlugin, IRouterHost
    {
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);

        private readonly PluginSettings _settings;
        private readonly ILogger _log;
        private readonly IPluginMessagePusher _messages;
        private readonly IFeatureManager _features;
        private readonly ITokenRequester _tokens;

        private LocalInstanceManager _im;
        private InstanceWaker _waker;
        private RouterServer _server;
        private Timer _timer;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        private volatile RouteTable _routes = RouteTable.Empty;
        private volatile RouterMessages _routerMessages = new RouterMessages();
        private string _listenKey;          // "addr:port" the server is bound to, null when stopped
        private string _lastBindError;      // so a failing bind is logged once, not every tick
        private string _lastWarnings = "";
        private bool _warnedNoInstanceManager;
        private bool _warnedNoDomain;

        public PluginMain(ILogger log, IConfigSerializer config, IPluginMessagePusher messages, IFeatureManager features, ITokenRequester tokens)
        {
            _log = log;
            _messages = messages;
            _features = features;
            _tokens = tokens;
            _settings = config.Load<PluginSettings>("", true, null);
        }

        public override bool HasFrontendContent => true;

        public override IEnumerable<SettingStore> SettingStores => new List<SettingStore> { _settings };

        public override Task<WebMethodsBase> InitAsync()
            => Task.FromResult<WebMethodsBase>(new WebMethods(this));

        public override Task PostInitAsync()
        {
            _server = new RouterServer(this);
            _waker = new InstanceWaker(() => _im, _tokens, _log);
            _timer = new Timer(_ => { _ = RefreshAsync(); }, null, TimeSpan.FromSeconds(2), RefreshInterval);
            return Task.CompletedTask;
        }

        // ---- IRouterHost ---------------------------------------------------------------

        public RouteTable Routes => _routes;
        public RouterMessages Messages => _routerMessages;
        public bool WakeOnJoin => _settings.Router.WakeOnJoin && _im != null;
        public bool IsWaking(Route route) => _waker != null && _waker.IsWaking(route.InstanceId);
        public void RequestWake(Route route, string playerAddress) => _waker?.Wake(route.InstanceId, route.DisplayName, playerAddress);

        public void Log(RouterLogLevel level, string message)
        {
            switch (level)
            {
                case RouterLogLevel.Debug: _log.Debug(message); break;
                case RouterLogLevel.Info: _log.Info(message); break;
                case RouterLogLevel.Warning: _log.Warning(message); break;
                default: _log.Error(message); break;
            }
        }

        // ---- Refresh loop --------------------------------------------------------------

        /// <summary>Rebuilds the route table from AMP's instances and (re)binds the listener to match the settings.</summary>
        internal async Task RefreshAsync()
        {
            if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                var s = _settings.Router;
                _routerMessages = new RouterMessages
                {
                    UnknownHost = s.MessageUnknownHost,
                    Offline = s.MessageOffline,
                    Sleeping = s.MessageSleeping,
                    Starting = s.MessageStarting
                };

                if (_im == null)
                {
                    _im = _features.RequestFeature<LocalInstanceManager>();
                    if (_im == null)
                    {
                        if (!_warnedNoInstanceManager)
                        {
                            _warnedNoInstanceManager = true;
                            _log.Error("Minecraft Router must be loaded on the ADS instance (the main AMP controller). It can't see any instances from here.");
                        }
                        return;
                    }
                    HookInstanceEvents(_im);
                }

                RebuildRoutes();
                ReconcileListener();
            }
            catch (Exception ex)
            {
                _log.Warning($"Minecraft Router refresh failed: {ex.Message}");
            }
            finally { _gate.Release(); }
        }

        private void HookInstanceEvents(LocalInstanceManager im)
        {
            EventHandler<InstanceEventArgs> soon = (_, __) => _ = Task.Delay(1500).ContinueWith(t => RefreshAsync());
            im.AfterCreateInstance += soon;
            im.AfterDestroyInstance += soon;
            im.AfterInstanceUpdate += soon;
            im.InstanceStarted += soon;
            im.InstanceStopped += soon;
        }

        private void RebuildRoutes()
        {
            var s = _settings.Router;
            var instances = AmpInstances.Describe(_im, w => _log.Debug(w));
            var table = RouteBuilder.Build(instances, new RoutingOptions
            {
                PrimaryDomain = s.PrimaryDomain,
                Overrides = s.Overrides,
                DefaultInstance = s.DefaultServer,
                ProxyProtocolForAll = s.ProxyProtocolForAll,
                ListenPort = s.ListenPort
            });

            var before = _routes;
            _routes = table;

            if (RouteSignature(before) != RouteSignature(table) && s.Enabled)
            {
                if (table.Routes.Count == 0) _log.Info("Minecraft Router: no servers to route yet.");
                foreach (var r in table.Routes)
                    _log.Info($"Minecraft Router: {string.Join(", ", r.Hostnames)} -> {r.DisplayName} ({r.Backend}{(r.SendProxyProtocol ? ", PROXY" : "")}{(r.IsDefault ? ", default" : "")})");
            }

            var warnings = string.Join("\n", table.Warnings);
            if (warnings != _lastWarnings)
            {
                _lastWarnings = warnings;
                foreach (var w in table.Warnings) _log.Warning($"Minecraft Router: {w}");
            }
        }

        private static string RouteSignature(RouteTable t)
            => string.Join("|", t.Routes.Select(r => $"{string.Join(",", r.Hostnames)}>{r.Backend}:{r.SendProxyProtocol}:{r.IsDefault}"));

        private void ReconcileListener()
        {
            var s = _settings.Router;
            string wanted = null;
            IPAddress addr = null;
            if (s.Enabled)
            {
                if (!IPAddress.TryParse(string.IsNullOrWhiteSpace(s.ListenAddress) ? "0.0.0.0" : s.ListenAddress.Trim(), out addr))
                {
                    LogBindErrorOnce($"Minecraft Router: listen address '{s.ListenAddress}' isn't a valid IP address.");
                    return;
                }
                if (s.ListenPort < 1 || s.ListenPort > 65535)
                {
                    LogBindErrorOnce($"Minecraft Router: listen port {s.ListenPort} is out of range.");
                    return;
                }
                bool noDomain = string.IsNullOrWhiteSpace(s.PrimaryDomain);
                if (noDomain && !_warnedNoDomain)
                    _log.Warning("Minecraft Router: no primary domain set, so only full hostnames from the overrides (and the default server) will match.");
                _warnedNoDomain = noDomain;
                wanted = $"{addr}:{s.ListenPort}";
            }

            if (wanted == _listenKey) return;

            if (_listenKey != null) { _server.Stop(); _listenKey = null; }
            if (wanted == null) { _lastBindError = null; return; }

            try
            {
                _server.Start(addr, s.ListenPort);
                _listenKey = wanted;
                _lastBindError = null;
            }
            catch (Exception ex)
            {
                LogBindErrorOnce($"Minecraft Router: can't listen on {wanted}: {ex.Message}. Is something else (an instance?) using that port?");
            }
        }

        private void LogBindErrorOnce(string message)
        {
            if (message == _lastBindError) return;
            _lastBindError = message;
            _log.Error(message);
        }

        // ---- Used by WebMethods ------------------------------------------------------

        internal bool IsListening => _listenKey != null;
        internal string ListenKey => _listenKey;
        internal PluginSettings.RouterSettings Settings => _settings.Router;
        internal RouterServer Server => _server;

        internal bool IsInstanceRunning(Guid id)
        {
            var inst = _im?.Instances.FirstOrDefault(i => i.InstanceID == id);
            if (inst == null) return false;
            try { return _im.InstanceIsRunning(inst); } catch { return false; }
        }

        // RequiredPermission must be null: a non-null value that matches nothing silently drops the message.
        internal void PushPopup(string kind, string text) => _messages.Push(kind, text, 30, null, "MinecraftRouter");
    }
}
