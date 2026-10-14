using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using InstanceManagerPlugin;
using MinecraftRouter.Core;
using ModuleShared;

namespace MinecraftRouter
{
    /// <summary>Reads ADS's local instances and turns them into router descriptors.</summary>
    internal static class AmpInstances
    {
        public static List<InstanceDescriptor> Describe(LocalInstanceManager im, Action<string> warn)
        {
            var list = new List<InstanceDescriptor>();
            foreach (var inst in im.Instances.ToList())
            {
                if (string.Equals(inst.Module, "ADS", StringComparison.OrdinalIgnoreCase)) continue;
                var d = new InstanceDescriptor
                {
                    InstanceId = inst.InstanceID,
                    InstanceName = inst.InstanceName,
                    FriendlyName = inst.FriendlyName,
                    Module = inst.Module,
                    Suspended = inst.Suspended
                };
                try
                {
                    var port = FindGamePort(inst);
                    if (port != null)
                    {
                        d.GamePort = port.PortNumber;
                        d.BindAddress = AddressText(port.AssociatedAddress) ?? AddressText(inst.ApplicationIP);
                    }
                }
                catch (Exception ex)
                {
                    warn?.Invoke($"Couldn't read ports for {inst.InstanceName}: {ex.Message}");
                }
                list.Add(d);
            }
            return list;
        }

        // The Minecraft module stores its port as MinecraftModule.Minecraft.PortNumber. For
        // anything else (Generic templates used for modpacks), take the most game-like TCP port.
        private static PortUsage FindGamePort(AMPInstanceBase inst)
        {
            var ports = inst.GetPortUsage().Where(p => p.PortNumber > 0).ToList();
            var mc = ports.FirstOrDefault(p => (p.ProvisionNodeName ?? "").EndsWith("Minecraft.PortNumber", StringComparison.OrdinalIgnoreCase));
            if (mc != null) return mc;

            var tcp = ports.Where(p => p.Protocol != PortProtocol.UDP && !IsAdmin(p)).ToList();
            return tcp.FirstOrDefault(p => Mentions(p, "game") || Mentions(p, "server"))
                ?? tcp.FirstOrDefault();
        }

        private static bool Mentions(PortUsage p, string word)
            => (p.ProvisionNodeName ?? "").IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0
            || (p.Description ?? "").IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsAdmin(PortUsage p)
            => Mentions(p, "rcon") || Mentions(p, "query") || Mentions(p, "admin") || Mentions(p, "web") || Mentions(p, "sftp");

        private static string AddressText(IPAddress a) => a?.ToString();

        private static string AddressText(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
