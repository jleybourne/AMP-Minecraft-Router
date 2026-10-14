using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MinecraftRouter.Core
{
    /// <summary>What the router needs to know about one AMP instance. Filled in by the plugin layer.</summary>
    public sealed class InstanceDescriptor
    {
        public Guid InstanceId;
        public string InstanceName;
        public string FriendlyName;
        public string Module;
        /// <summary>The game port AMP has configured for the instance (0 when unknown).</summary>
        public int GamePort;
        /// <summary>The IP the game server binds to ("0.0.0.0", "::", a specific IP or empty).</summary>
        public string BindAddress;
        public bool Suspended;
    }

    public sealed class Route
    {
        public Guid InstanceId;
        public string InstanceName;
        public string FriendlyName;
        public IReadOnlyList<string> Hostnames = Array.Empty<string>();
        public string BackendHost;
        public int BackendPort;
        public bool SendProxyProtocol;
        public bool IsDefault;
        /// <summary>True when the route came from an override line rather than automatic naming.</summary>
        public bool FromOverride;

        public string DisplayName => string.IsNullOrWhiteSpace(FriendlyName) ? InstanceName : FriendlyName;
        public string Backend => $"{BackendHost}:{BackendPort}";
    }

    public sealed class RouteTable
    {
        public static readonly RouteTable Empty = new RouteTable(new Dictionary<string, Route>(), null, "", new List<string>(), new List<Route>());

        private readonly Dictionary<string, Route> _byHost;
        public Route Default { get; }
        public string PrimaryDomain { get; }
        public IReadOnlyList<string> Warnings { get; }
        public IReadOnlyList<Route> Routes { get; }

        public RouteTable(Dictionary<string, Route> byHost, Route defaultRoute, string primaryDomain, List<string> warnings, List<Route> routes)
        {
            _byHost = byHost;
            Default = defaultRoute;
            PrimaryDomain = primaryDomain ?? "";
            Warnings = warnings;
            Routes = routes;
        }

        public IReadOnlyDictionary<string, Route> ByHost => _byHost;

        /// <summary>Finds the route for a normalized hostname, falling back to the default route.</summary>
        public Route Resolve(string hostname)
        {
            if (!string.IsNullOrEmpty(hostname) && _byHost.TryGetValue(hostname, out var r)) return r;
            return Default;
        }
    }

    public sealed class RoutingOptions
    {
        public string PrimaryDomain = "";
        public string Overrides = "";
        public string DefaultInstance = "";
        public bool ProxyProtocolForAll;
        /// <summary>Port the router itself listens on; instances using it are flagged.</summary>
        public int ListenPort = 25565;
        /// <summary>Modules routed automatically, without an override line.</summary>
        public ISet<string> AutoModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Minecraft" };
    }

    /// <summary>One parsed line of the overrides box: "Instance = names... [options]".</summary>
    public sealed class RouteOverride
    {
        public string Key;
        public bool Exclude;
        public List<string> Names = new List<string>();
        public int? Port;
        public string Host;
        public bool? ProxyProtocol;
        public bool Default;
        public int Line;
    }

    public static class RouteBuilder
    {
        private static readonly Regex Label = new Regex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.Compiled);

        /// <summary>"OceanBlock 2" -> "oceanblock-2". Returns "" when nothing usable is left.</summary>
        public static string Slugify(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var sb = new StringBuilder();
            bool dash = false;
            foreach (var ch in name.Trim().ToLowerInvariant())
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) { sb.Append(ch); dash = false; }
                else if (!dash && sb.Length > 0) { sb.Append('-'); dash = true; }
            }
            var s = sb.ToString().Trim('-');
            return s.Length > 63 ? s.Substring(0, 63).Trim('-') : s;
        }

        public static string NormalizeDomain(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain)) return "";
            var d = domain.Trim().ToLowerInvariant().TrimEnd('.');
            if (d.StartsWith("*.")) d = d.Substring(2);
            if (d.StartsWith(".")) d = d.Substring(1);
            return d;
        }

        /// <summary>A bare label becomes label.domain; anything with a dot is taken as a full hostname.</summary>
        public static string Qualify(string name, string domain)
        {
            var n = name.Trim().ToLowerInvariant().TrimEnd('.');
            if (n.Contains('.') || string.IsNullOrEmpty(domain)) return n;
            return n + "." + domain;
        }

        public static List<RouteOverride> ParseOverrides(string text, List<string> warnings)
        {
            var list = new List<RouteOverride>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            var lines = text.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash);
                line = line.Trim();
                if (line.Length == 0) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    warnings.Add($"Override line {i + 1} ignored: expected 'Instance = names', got '{line}'.");
                    continue;
                }
                var o = new RouteOverride { Key = line.Substring(0, eq).Trim(), Line = i + 1 };
                var tokens = line.Substring(eq + 1).Split(new[] { ' ', ',', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var raw in tokens)
                {
                    var t = raw.Trim();
                    var lower = t.ToLowerInvariant();
                    if (t == "-" || lower == "off" || lower == "none" || lower == "exclude") { o.Exclude = true; continue; }
                    if (lower == "proxy" || lower == "proxy-protocol") { o.ProxyProtocol = true; continue; }
                    if (lower == "noproxy" || lower == "no-proxy") { o.ProxyProtocol = false; continue; }
                    if (lower == "default") { o.Default = true; continue; }
                    int oeq = t.IndexOf('=');
                    if (oeq > 0)
                    {
                        var k = lower.Substring(0, oeq);
                        var v = t.Substring(oeq + 1);
                        if (k == "port" && int.TryParse(v, out var p) && p > 0 && p < 65536) o.Port = p;
                        else if (k == "host" && !string.IsNullOrWhiteSpace(v)) o.Host = v.Trim();
                        else warnings.Add($"Override line {i + 1}: unknown option '{t}'.");
                        continue;
                    }
                    o.Names.Add(lower.TrimEnd('.'));
                }
                list.Add(o);
            }
            return list;
        }

        private static string BackendHostFor(string bind)
        {
            if (string.IsNullOrWhiteSpace(bind) || bind == "0.0.0.0" || bind == "::" || bind == "*" || bind == "[::]") return "127.0.0.1";
            return bind.Trim();
        }

        public static RouteTable Build(IEnumerable<InstanceDescriptor> instances, RoutingOptions opts)
        {
            var warnings = new List<string>();
            var domain = NormalizeDomain(opts.PrimaryDomain);
            var overrides = ParseOverrides(opts.Overrides, warnings);
            var all = instances.Where(i => !string.Equals(i.Module, "ADS", StringComparison.OrdinalIgnoreCase)).ToList();

            RouteOverride FindOverride(InstanceDescriptor inst) =>
                overrides.FirstOrDefault(o => string.Equals(o.Key, inst.InstanceName, StringComparison.OrdinalIgnoreCase))
                ?? overrides.FirstOrDefault(o => string.Equals(o.Key, inst.FriendlyName, StringComparison.OrdinalIgnoreCase));

            foreach (var o in overrides)
            {
                bool matched = all.Any(i => string.Equals(o.Key, i.InstanceName, StringComparison.OrdinalIgnoreCase)
                                         || string.Equals(o.Key, i.FriendlyName, StringComparison.OrdinalIgnoreCase));
                if (!matched) warnings.Add($"Override line {o.Line}: no instance called '{o.Key}'.");
            }

            var byHost = new Dictionary<string, Route>(StringComparer.OrdinalIgnoreCase);
            var routes = new List<Route>();
            Route defaultRoute = null;

            // Explicit overrides claim their hostnames before automatic names do, so a
            // hand-picked name always wins a clash.
            var ordered = all.OrderBy(i => FindOverride(i) == null ? 1 : 0)
                             .ThenBy(i => i.InstanceName, StringComparer.OrdinalIgnoreCase);

            foreach (var inst in ordered)
            {
                var o = FindOverride(inst);
                if (o != null && o.Exclude) continue;
                if (inst.Suspended) continue;
                bool auto = opts.AutoModules.Contains(inst.Module ?? "");
                if (o == null && !auto) continue;

                int port = o?.Port ?? inst.GamePort;
                if (port <= 0)
                {
                    warnings.Add($"{inst.FriendlyName ?? inst.InstanceName}: no game port found. Add 'port=NNNNN' to its override line.");
                    continue;
                }

                var names = new List<string>();
                if (o != null && o.Names.Count > 0) names.AddRange(o.Names);
                else
                {
                    var slug = Slugify(inst.FriendlyName);
                    if (slug.Length == 0) slug = Slugify(inst.InstanceName);
                    if (slug.Length == 0)
                    {
                        warnings.Add($"{inst.InstanceName}: couldn't make a hostname from its name; add an override line.");
                        continue;
                    }
                    names.Add(slug);
                }

                var route = new Route
                {
                    InstanceId = inst.InstanceId,
                    InstanceName = inst.InstanceName,
                    FriendlyName = inst.FriendlyName,
                    BackendHost = o?.Host ?? BackendHostFor(inst.BindAddress),
                    BackendPort = port,
                    SendProxyProtocol = o?.ProxyProtocol ?? opts.ProxyProtocolForAll,
                    FromOverride = o != null
                };

                if (port == opts.ListenPort && o?.Host == null)
                    warnings.Add($"{route.DisplayName} uses port {port}, the same port the router listens on. Move the instance to another port, or it can't start while the router is running.");

                var claimed = new List<string>();
                foreach (var n in names)
                {
                    var host = Qualify(n, domain);
                    var firstLabel = host.Split('.')[0];
                    if (!Label.IsMatch(firstLabel))
                    {
                        warnings.Add($"{route.DisplayName}: '{n}' isn't a valid hostname label, skipped.");
                        continue;
                    }
                    if (byHost.TryGetValue(host, out var existing))
                    {
                        warnings.Add($"{host} is wanted by both {existing.DisplayName} and {route.DisplayName}; {existing.DisplayName} keeps it. Give one of them an override.");
                        continue;
                    }
                    byHost[host] = route;
                    claimed.Add(host);
                }
                if (claimed.Count == 0) continue;
                route.Hostnames = claimed;
                routes.Add(route);

                if (o != null && o.Default)
                {
                    if (defaultRoute == null) { defaultRoute = route; route.IsDefault = true; }
                    else warnings.Add($"Both {defaultRoute.DisplayName} and {route.DisplayName} are marked default; using {defaultRoute.DisplayName}.");
                }
            }

            if (defaultRoute == null && !string.IsNullOrWhiteSpace(opts.DefaultInstance))
            {
                var key = opts.DefaultInstance.Trim();
                defaultRoute = routes.FirstOrDefault(r => string.Equals(r.InstanceName, key, StringComparison.OrdinalIgnoreCase)
                                                       || string.Equals(r.FriendlyName, key, StringComparison.OrdinalIgnoreCase)
                                                       || r.Hostnames.Any(h => string.Equals(h, Qualify(key, domain), StringComparison.OrdinalIgnoreCase)));
                if (defaultRoute != null) defaultRoute.IsDefault = true;
                else warnings.Add($"Default server '{key}' isn't a routed instance.");
            }

            // The bare domain (domain.com with no subdomain) goes to the default server.
            if (defaultRoute != null && domain.Length > 0 && !byHost.ContainsKey(domain))
                byHost[domain] = defaultRoute;

            routes.Sort((a, b) => string.Compare(a.Hostnames[0], b.Hostnames[0], StringComparison.OrdinalIgnoreCase));
            return new RouteTable(byHost, defaultRoute, domain, warnings, routes);
        }
    }
}
