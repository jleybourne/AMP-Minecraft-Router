using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ModuleShared;

namespace MinecraftRouter
{
    // Every [JSONMethod] here is callable as API/MinecraftRouter/<Method>.
    internal class WebMethods : WebMethodsBase
    {
        private readonly PluginMain _plugin;

        public WebMethods(PluginMain plugin) { _plugin = plugin; }

        [JSONMethod("Current Minecraft routes: which hostname goes to which instance.", "Object with listener state, routes and warnings.")]
        public async Task<object> GetRoutes()
        {
            await _plugin.RefreshAsync();
            var t = _plugin.Routes;
            var s = _plugin.Settings;
            return new Dictionary<string, object>
            {
                ["enabled"] = s.Enabled,
                ["listening"] = _plugin.IsListening,
                ["listen"] = _plugin.ListenKey,
                ["domain"] = t.PrimaryDomain,
                ["wakeOnJoin"] = s.WakeOnJoin,
                ["totalConnections"] = _plugin.Server?.TotalConnections ?? 0,
                ["routes"] = t.Routes.Select(r => new Dictionary<string, object>
                {
                    ["hostnames"] = r.Hostnames,
                    ["instance"] = r.InstanceName,
                    ["name"] = r.DisplayName,
                    ["backend"] = r.Backend,
                    ["proxyProtocol"] = r.SendProxyProtocol,
                    ["default"] = r.IsDefault,
                    ["instanceRunning"] = _plugin.IsInstanceRunning(r.InstanceId),
                    ["players"] = _plugin.Server?.ActiveFor(r.InstanceId) ?? 0
                }).ToList(),
                ["warnings"] = t.Warnings
            };
        }

        // Backs the "Show routes" button next to the Enable setting. The panel tends to
        // swallow ActionResult text, so the table is pushed as a dialog as well.
        [JSONMethod("Show the current routes in a dialog.", "Success with a text summary.")]
        public async Task<ActionResult> ShowRoutes()
        {
            await _plugin.RefreshAsync();
            var t = _plugin.Routes;
            var s = _plugin.Settings;
            var sb = new StringBuilder();

            if (!s.Enabled) sb.AppendLine("The router is OFF. Tick 'Enable Minecraft Router' to start it.\n");
            else if (_plugin.IsListening) sb.AppendLine($"Listening on {_plugin.ListenKey}.\n");
            else sb.AppendLine("Enabled, but NOT listening. Check the AMP log for the reason (usually the port is in use).\n");

            if (t.Routes.Count == 0) sb.AppendLine("No servers are routed yet. Minecraft instances appear here automatically.");
            foreach (var r in t.Routes)
            {
                var flags = new List<string>();
                if (r.IsDefault) flags.Add("default");
                if (r.SendProxyProtocol) flags.Add("PROXY");
                flags.Add(_plugin.IsInstanceRunning(r.InstanceId) ? "running" : "stopped");
                int players = _plugin.Server?.ActiveFor(r.InstanceId) ?? 0;
                if (players > 0) flags.Add($"{players} connected");
                sb.AppendLine($"{string.Join(", ", r.Hostnames)}  →  {r.DisplayName} ({r.Backend})  [{string.Join(", ", flags)}]");
            }

            if (t.Warnings.Count > 0)
            {
                sb.AppendLine("\nWarnings:");
                foreach (var w in t.Warnings) sb.AppendLine("• " + w);
            }

            var text = sb.ToString().TrimEnd();
            _plugin.PushPopup("routes", text);
            return new ActionResult(true, (RawString)text);
        }

        [JSONMethod("Rebuild the routes now instead of waiting for the next refresh.", "Success.")]
        public async Task<ActionResult> RefreshRoutes()
        {
            await _plugin.RefreshAsync();
            return ActionResult.Success;
        }
    }
}
