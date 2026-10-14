using MinecraftRouter.Core;
using Xunit;

namespace MinecraftRouter.Tests
{
    public class RoutingTests
    {
        private static InstanceDescriptor Mc(string name, string friendly, int port, string module = "Minecraft")
            => new InstanceDescriptor { InstanceId = Guid.NewGuid(), InstanceName = name, FriendlyName = friendly, Module = module, GamePort = port, BindAddress = "0.0.0.0" };

        private static RoutingOptions Opts(string overrides = "", string def = "", bool proxyAll = false)
            => new RoutingOptions { PrimaryDomain = "domain.com", Overrides = overrides, DefaultInstance = def, ProxyProtocolForAll = proxyAll };

        [Theory]
        [InlineData("OceanBlock", "oceanblock")]
        [InlineData("Survival Server", "survival-server")]
        [InlineData("  ATM9 -- Sky! ", "atm9-sky")]
        [InlineData("###", "")]
        public void Slugify(string input, string expected) => Assert.Equal(expected, RouteBuilder.Slugify(input));

        [Theory]
        [InlineData("*.domain.com", "domain.com")]
        [InlineData("Domain.COM.", "domain.com")]
        [InlineData(".domain.com", "domain.com")]
        public void NormalizeDomain(string input, string expected) => Assert.Equal(expected, RouteBuilder.NormalizeDomain(input));

        [Fact]
        public void MinecraftInstancesAreRoutedByFriendlyName()
        {
            var t = RouteBuilder.Build(new[]
            {
                Mc("Survival01", "Survival", 25566),
                Mc("Creative01", "Creative", 25567),
                Mc("OceanBlock01", "OceanBlock", 25568),
            }, Opts());

            Assert.Equal(3, t.Routes.Count);
            Assert.Equal(25566, t.Resolve("survival.domain.com").BackendPort);
            Assert.Equal(25567, t.Resolve("creative.domain.com").BackendPort);
            Assert.Equal("127.0.0.1", t.Resolve("oceanblock.domain.com").BackendHost);
            Assert.Null(t.Resolve("nope.domain.com"));
            Assert.Empty(t.Warnings);
        }

        [Fact]
        public void NonMinecraftModulesNeedAnOverride()
        {
            var generic = Mc("Modded01", "Modded", 25570, "GenericModule");
            var t = RouteBuilder.Build(new[] { generic }, Opts());
            Assert.Empty(t.Routes);

            t = RouteBuilder.Build(new[] { generic }, Opts("Modded01 = modded"));
            Assert.Equal(25570, t.Resolve("modded.domain.com").BackendPort);
        }

        [Fact]
        public void OverridesSetNamesOptionsAndExclusions()
        {
            var t = RouteBuilder.Build(new[]
            {
                Mc("OceanBlock01", "OceanBlock", 25568),
                Mc("Test01", "Test", 25569),
                Mc("Lobby01", "Lobby", 25570),
            }, Opts("# comment\nOceanBlock01 = ob, oceanblock proxy port=25600\nTest = -\nlobby01 = lobby play.example.com default"));

            var ob = t.Resolve("ob.domain.com");
            Assert.Same(ob, t.Resolve("oceanblock.domain.com"));
            Assert.Equal(25600, ob.BackendPort);
            Assert.True(ob.SendProxyProtocol);

            Assert.Null(t.ByHost.GetValueOrDefault("test.domain.com"));

            var lobby = t.Resolve("play.example.com");
            Assert.Equal("Lobby01", lobby.InstanceName);
            Assert.True(lobby.IsDefault);
            // Bare domain and unknown names fall back to the default server.
            Assert.Same(lobby, t.Resolve("domain.com"));
            Assert.Same(lobby, t.Resolve("whatever.domain.com"));
        }

        [Fact]
        public void DefaultServerSettingAcceptsSubdomain()
        {
            var t = RouteBuilder.Build(new[] { Mc("Survival01", "Survival", 25566) }, Opts(def: "survival"));
            Assert.Equal("Survival01", t.Default.InstanceName);
        }

        [Fact]
        public void OverrideWinsANameClash()
        {
            var t = RouteBuilder.Build(new[]
            {
                Mc("Survival01", "Survival", 25566),
                Mc("Survival02", "Survival 2", 25567),
            }, Opts("Survival02 = survival"));

            Assert.Equal("Survival02", t.Resolve("survival.domain.com").InstanceName);
            Assert.Contains(t.Warnings, w => w.Contains("survival.domain.com"));
            Assert.Single(t.Routes);
        }

        [Fact]
        public void WarnsAboutListenPortClashAndUnknownOverride()
        {
            var t = RouteBuilder.Build(new[] { Mc("Survival01", "Survival", 25565) }, Opts("Ghost = boo"));
            Assert.Contains(t.Warnings, w => w.Contains("same port the router listens on"));
            Assert.Contains(t.Warnings, w => w.Contains("Ghost"));
        }

        [Fact]
        public void SuspendedAndAdsInstancesAreSkipped()
        {
            var s = Mc("Survival01", "Survival", 25566);
            s.Suspended = true;
            var ads = Mc("ADS01", "ADS", 8080, "ADS");
            var t = RouteBuilder.Build(new[] { s, ads }, Opts());
            Assert.Empty(t.Routes);
        }

        [Fact]
        public void ProxyProtocolForAllCanBeTurnedOffPerServer()
        {
            var t = RouteBuilder.Build(new[] { Mc("A", "Alpha", 25566), Mc("B", "Beta", 25567) }, Opts("B = beta noproxy", proxyAll: true));
            Assert.True(t.Resolve("alpha.domain.com").SendProxyProtocol);
            Assert.False(t.Resolve("beta.domain.com").SendProxyProtocol);
        }

        [Fact]
        public void SpecificBindAddressIsUsedAsBackend()
        {
            var i = Mc("A", "Alpha", 25566);
            i.BindAddress = "10.0.0.5";
            var t = RouteBuilder.Build(new[] { i }, Opts());
            Assert.Equal("10.0.0.5:25566", t.Resolve("alpha.domain.com").Backend);
        }
    }
}
