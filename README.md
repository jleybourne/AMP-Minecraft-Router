# Minecraft Router for AMP

[![Build](https://github.com/jleybourne/AMP-Minecraft-Router/actions/workflows/build.yml/badge.svg)](https://github.com/jleybourne/AMP-Minecraft-Router/actions/workflows/build.yml)
[![Latest release](https://img.shields.io/github/v/release/jleybourne/AMP-Minecraft-Router)](https://github.com/jleybourne/AMP-Minecraft-Router/releases/latest)

An [AMP](https://cubecoders.com/AMP) plugin that sends Minecraft players to the right server based on the address they type. It works like DiscoPanel's proxy:

- Set a primary domain once, e.g. `domain.com`.
- Point a wildcard DNS record (`*.domain.com`) at your AMP machine.
- Create Minecraft instances in AMP as normal. Each one is reachable at `<name>.domain.com` on the default port. No port numbers to hand out, and only one port to forward.

```
survival.domain.com   ─┐                       ┌─► Survival    (127.0.0.1:25566)
creative.domain.com   ─┼─► :25565  Minecraft  ─┼─► Creative    (127.0.0.1:25567)
oceanblock.domain.com ─┘           Router      └─► OceanBlock  (127.0.0.1:25568)
```

## Features

- **Automatic routes.** Every Minecraft instance gets a subdomain from its friendly name (`OceanBlock` → `oceanblock.domain.com`). New, renamed and re-ported instances are picked up within seconds.
- **Overrides.** Choose your own subdomains, give a server several names or a full hostname on another domain, route a Generic-module modpack, or leave a server out.
- **Wake on join.** When someone joins a stopped server, the router starts the AMP instance and the game server inside it, then tells the player to reconnect in a minute. Server-list pings never wake anything.
- **Friendly server-list entries.** Stopped servers show "asleep, join to wake it up". Unknown addresses get a clear message instead of a connection error.
- **Default server.** The bare domain (`domain.com`), and any address that matches nothing, can go to a server of your choice.
- **Real player IPs (optional).** Sends PROXY protocol v2 to servers that accept it, so bans and logs show the player's IP instead of the router's.
- **Settings in the AMP web panel**, with a **Show routes** button that lists what goes where and flags problems such as name clashes or an instance using the router's port.

## How it works

When a Minecraft Java client connects, its first packet (the handshake) includes the address the player typed. The router reads that packet, looks the hostname up in a table built from your AMP instances, opens a connection to that instance's game port, replays the handshake and then passes bytes both ways untouched. Encryption, compression and mods all work as normal because the router never looks past the first packet.

The plugin runs inside ADS (AMP's main controller). It reads the instance list directly from ADS, so there's nothing extra to run or keep in sync.

## Setup

### 1. DNS (Cloudflare)

Add one record:

| Type | Name | Content | Proxy status |
|------|------|---------|--------------|
| A | `*` | your public IP | **DNS only** (grey cloud) |

Cloudflare's orange-cloud proxy only carries web traffic and will break Minecraft. If you also want the bare domain to work, add an `A` record for `@` too and set a default server.

### 2. Firewall / port forward

Forward **TCP 25565** to the AMP machine. The individual instance ports no longer need to be reachable from the internet. You can close them once everything works.

### 3. Free up port 25565

The router needs port 25565, so no instance can use it. If one of your Minecraft instances is on 25565, move it to another port first, in the instance's Configuration → Server and Ports. The **Show routes** dialog warns you if one is still on it.

### 4. Install the plugin

AMP only loads plugins signed by CubeCoders unless the instance also has an **AMP Developer licence**. This applies to every third-party AMP plugin (see CubeCoders' [3rd-party plugins list](https://discourse.cubecoders.com/t/3rd-party-amp-plugins-and-bots-list/2231)). The developer licence is a companion to your normal licence and doesn't use an instance slot.

1. Get a developer licence key from your CubeCoders licence manager.
2. Download `MinecraftRouter-x.y.z.zip` from [Releases](https://github.com/jleybourne/AMP-Minecraft-Router/releases/latest). It contains a `MinecraftRouter` folder.
3. Stop ADS and add both licences, developer key **first**, then your normal key:

   ```bash
   ampinstmgr stop ADS01
   ampinstmgr --ReactivateInstance ADS01 <developer-key>
   ampinstmgr --ReactivateInstance ADS01 <your-normal-key>
   ```

4. Copy the `MinecraftRouter` folder into the ADS instance's `Plugins` folder:

   - Linux: `~/.ampdata/instances/ADS01/Plugins/MinecraftRouter/`
   - Windows: `<AMP datastore>\Instances\ADS01\Plugins\MinecraftRouter\`

   You should end up with `.../Plugins/MinecraftRouter/MinecraftRouter.dll` and `.../Plugins/MinecraftRouter/WebRoot/Plugin.js`.

5. Edit `AMPConfig.conf` in the ADS instance folder and add the plugin to the load list. If the line already lists other plugins, add it to them:

   ```
   AMP.LoadPlugins=["MinecraftRouter"]
   ```

6. Start ADS (`ampinstmgr start ADS01`). The log should show `Loaded MinecraftRouter by ...`.

> Run `ampinstmgr --help` if your version's command names differ. If ADS runs in Docker, also publish port 25565 from the container.

### 5. Configure

In the AMP web panel, open **Configuration → Instance Deployment → Minecraft Router**:

| Setting | What it does |
|---|---|
| Enable Minecraft Router | Master switch. The **Show routes** button is next to it. |
| Primary domain | e.g. `domain.com`. Servers become `<name>.domain.com`. |
| Listen port / address | Where players connect. Defaults: `25565` on `0.0.0.0`. Use `::` to accept IPv6 as well. |
| Default server | Instance (or subdomain) for the bare domain and unmatched addresses. Blank refuses them. |
| Wake on join | Start stopped servers when someone tries to join. |
| Send PROXY protocol to every server | Only if **all** your servers accept it (see below). |
| Route overrides | Optional, see below. |
| Messages | Text shown to players for unknown, offline, asleep and starting servers. `§` colour codes work. |

Changes apply within about 10 seconds. You don't need to restart ADS.

## Route overrides

One line per instance: `Instance = names [options]`. The instance can be its instance name (`OceanBlock01`) or friendly name. Lines starting with `#` are comments.

```
# Custom subdomains (several names are fine)
OceanBlock01 = ob oceanblock

# A full hostname on a different domain
Lobby01 = lobby play.example.com default

# Don't route this one
TestServer01 = -

# Generic-module modpack: name it, and set the port if it isn't detected
ATM9 = atm9 port=25570

# Send the real player IP to this server only
Survival01 = survival proxy
```

| Option | Meaning |
|---|---|
| `-` | Leave this instance out. |
| `default` | Use this server for the bare domain and unmatched addresses. |
| `proxy` / `noproxy` | Send (or don't send) PROXY protocol to this server, overriding the global setting. |
| `port=N` | Backend port, when it isn't the instance's detected game port. |
| `host=IP` | Backend address, when it isn't on this machine. |

A name without a dot gets the primary domain added; a name with a dot is used as-is. Minecraft-module instances with no override line are routed automatically by friendly name. Instances from other modules (such as Generic-module modpacks) are only routed when they have a line.

## Wake on join

When a player joins a server that isn't reachable:

1. If the AMP instance is stopped, the router starts it (the same as pressing Start in ADS).
2. It then logs in to the instance the same way ADS does and starts the game server if it isn't already starting.
3. The player is disconnected with "starting up, try again in a minute". The server list shows "starting" until it's up.

This pairs well with AMP's own idle shutdown or sleep settings, which stop empty servers. The router brings them back when someone wants to play.

## Real player IPs (PROXY protocol)

By default every server sees players coming from `127.0.0.1`, the router. To pass through the real IP, the server must accept PROXY protocol v2, and you turn it on for that server with `proxy`, or globally:

- **Paper / Purpur:** in `config/paper-global.yml` set `proxies: proxy-protocol: true`, then restart the server.
- **Velocity / BungeeCord:** enable `haproxy-protocol` / `proxy_protocol` in their configs.
- **Forge / Fabric / NeoForge modpacks:** need a server-side mod that adds PROXY protocol support.

A server that doesn't support it will reject every connection that has PROXY protocol turned on. Enable it per server once you've set the server up.

## Limitations

- **Java Edition only.** Bedrock clients don't send the hostname, so they can't be routed this way. Give Bedrock servers their own port as usual.
- **Restarting ADS drops connected players briefly.** The game servers keep running. Players just reconnect. This usually only happens during AMP updates.
- **Local instances only.** Instances on remote ADS targets aren't routed yet. You can add them with an override line and `host=`/`port=`, but wake on join won't work for them.
- Old clients that only send the pre-1.7 legacy ping see the server as offline in their server list. Joining works for all versions from 1.7 on.

## Troubleshooting

- **"Enabled, but NOT listening"**: something else is on the listen port. That's usually a Minecraft instance still on 25565. Check the AMP log.
- **"There's no server at …"**: the hostname doesn't match a route. Use **Show routes** to see the exact hostnames, and check the primary domain setting.
- **Server list shows it, but joining times out**: check the Cloudflare record is grey-clouded and TCP 25565 is forwarded.
- **"Plugin has no suitable class" / plugin not loading**: the folder and DLL must both be called `MinecraftRouter`. The developer licence must also be active alongside your normal licence.
- **Settings tab missing after an AMP update**: AMP sometimes changes its plugin interfaces. Grab the latest release.

## Building from source

Requires the .NET 8 SDK.

```bash
git clone https://github.com/jleybourne/AMP-Minecraft-Router.git
cd AMP-Minecraft-Router
./scripts/fetch-amp-refs.sh          # Windows: .\scripts\fetch-amp-refs.ps1
dotnet test
./scripts/package.sh                 # -> dist/MinecraftRouter-<version>.zip
```

`fetch-amp-refs` downloads the current AMP release and extracts `ModuleShared.dll` and `InstanceManagerPlugin.dll` into `lib/` to compile against. They're reference-only: AMP provides them at runtime, and they're not committed or shipped. The router core (`src/MinecraftRouter/Core`) has no AMP dependency and is covered by the tests in `tests/`.

### Testing status

- The router core is unit- and integration-tested. It has also been tested end to end with real Minecraft 1.21.4 clients (mineflayer) against two Paper servers. Routing by hostname, PROXY protocol, the asleep/unknown server-list entries and the wake-on-join kick all worked.
- The AMP integration (instance discovery, settings tab, wake on join starting real instances) builds against AMP 2.8.0.8 but hasn't been tested on a live AMP install yet. Please [open an issue](https://github.com/jleybourne/AMP-Minecraft-Router/issues) with your AMP log if anything misbehaves.

## Contributing

Issues and pull requests are welcome. Please run `dotnet test` before opening a PR. Anything touching the protocol code should come with a test in `tests/`.

## Licence

[MIT](LICENSE). Not affiliated with or endorsed by CubeCoders.
