using System.ComponentModel;
using ModuleShared;

namespace MinecraftRouter
{
    // Shown in ADS under Configuration > Instance Deployment, tab "Minecraft Router".
    // The WebSetting argument order is: Name, Description, ReadOnly, Tag, CustomFieldType,
    // Keywords, AlwaysAllowRead, Placeholder, Suffix, Meta, Subcategory, Order.
    internal class PluginSettings : SettingStore
    {
        private const string Tab = "Minecraft Router";

        [Description("Instance Deployment")]
        public class RouterSettings : SettingSectionStore
        {
            [WebSetting("Enable Minecraft Router",
                "Listen for Minecraft players and send each one to the server matching the address they typed (e.g. survival.example.com). Changes apply within a few seconds.",
                false, "", "", "minecraft,router,proxy,domain", false, "", "", "", Tab, 10)]
            [InlineAction("MinecraftRouter", "ShowRoutes", "Show routes", "", false)]
            public bool Enabled = false;

            [WebSetting("Primary domain",
                "Your domain. Each server gets <name>.<this domain>. Create a wildcard DNS record (*.example.com) pointing at this machine. In Cloudflare, set it to DNS only (grey cloud).",
                false, "", "", "minecraft,router,domain,dns", false, "example.com", "", "", Tab, 20)]
            public string PrimaryDomain = "";

            [WebSetting("Listen port",
                "Port players connect to. 25565 lets them leave the port off. No Minecraft instance may use this port itself; move any that do.",
                false, "", "", "minecraft,router,port", false, "25565", "", "", Tab, 30)]
            public int ListenPort = 25565;

            [WebSetting("Listen address",
                "Address to listen on. 0.0.0.0 for all IPv4 addresses, :: for IPv4 and IPv6.",
                false, "", "", "minecraft,router,ip", false, "0.0.0.0", "", "", Tab, 40)]
            public string ListenAddress = "0.0.0.0";

            [WebSetting("Default server",
                "Instance (or subdomain) that players get when they connect with the bare domain or an address that matches nothing. Leave blank to refuse them.",
                false, "", "", "minecraft,router,default", false, "", "", "", Tab, 50)]
            public string DefaultServer = "";

            [WebSetting("Wake on join",
                "When someone joins a server that isn't running, start it for them. They're told to reconnect in a minute. Server list pings never wake a server.",
                false, "", "", "minecraft,router,wake,start", false, "", "", "", Tab, 60)]
            public bool WakeOnJoin = true;

            [WebSetting("Send PROXY protocol to every server",
                "Pass the player's real IP to the servers using PROXY protocol v2. Only turn on if ALL your servers accept it (e.g. Paper with proxies.proxy-protocol: true). Otherwise use the 'proxy' option per server below.",
                false, "", "", "minecraft,router,proxy,ip", false, "", "", "", Tab, 70)]
            public bool ProxyProtocolForAll = false;

            [WebSetting("Route overrides",
                "Optional, one line per instance: Instance = names [options]. Examples:  OceanBlock01 = ob oceanblock   |   Lobby = lobby default   |   TestServer = -   (not routed)   |   Modded01 = modded port=25570 proxy   |   Other = play.otherdomain.com. A name without a dot gets the primary domain added. Minecraft instances without a line are routed automatically by their friendly name.",
                false, "", "Textarea", "minecraft,router,override,subdomain,hostname", false, "OceanBlock01 = ob oceanblock", "", "", Tab, 80)]
            public string Overrides = "";

            [WebSetting("Message: unknown address",
                "Shown when the address matches no server. {host} = the address typed. Supports § colour codes.",
                false, "", "", "minecraft,router,message", false, "", "", "", Tab, 90)]
            public string MessageUnknownHost = "§cThere's no server at §f{host}§c.";

            [WebSetting("Message: server offline",
                "Shown when a server isn't running and wake on join is off. {name} = server name.",
                false, "", "", "minecraft,router,message", false, "", "", "", Tab, 100)]
            public string MessageOffline = "§c{name} is offline.";

            [WebSetting("Message: server asleep",
                "Server list text for a stopped server when wake on join is on.",
                false, "", "", "minecraft,router,message", false, "", "", "", Tab, 110)]
            public string MessageSleeping = "§e{name} is asleep. §7Join to wake it up.";

            [WebSetting("Message: server starting",
                "Shown while a server is being woken up.",
                false, "", "", "minecraft,router,message", false, "", "", "", Tab, 120)]
            public string MessageStarting = "§e{name} is starting up. §7Try again in a minute.";
        }

        public RouterSettings Router = new RouterSettings();
    }
}
