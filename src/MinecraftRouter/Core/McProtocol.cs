using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftRouter.Core
{
    /// <summary>
    /// The small slice of the Minecraft Java protocol the router needs: VarInts, strings,
    /// the handshake packet, and the status / login-disconnect replies used when a server
    /// is offline or unknown.
    /// </summary>
    public static class McProtocol
    {
        // A real handshake is well under 300 bytes (address is capped at 255 chars).
        // Forge appends markers to the address, so leave generous headroom.
        public const int MaxHandshakeLength = 2048;

        public const int StateStatus = 1;
        public const int StateLogin = 2;
        public const int StateTransfer = 3;

        // ---- VarInt ------------------------------------------------------------------

        public static int ReadVarInt(byte[] buf, ref int pos, int limit)
        {
            int value = 0, shift = 0;
            while (true)
            {
                if (pos >= limit) throw new InvalidDataException("Truncated VarInt");
                byte b = buf[pos++];
                value |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) return value;
                shift += 7;
                if (shift >= 35) throw new InvalidDataException("VarInt too long");
            }
        }

        public static void WriteVarInt(Stream s, int value)
        {
            uint v = (uint)value;
            while (true)
            {
                if ((v & ~0x7Fu) == 0) { s.WriteByte((byte)v); return; }
                s.WriteByte((byte)((v & 0x7F) | 0x80));
                v >>= 7;
            }
        }

        public static string ReadString(byte[] buf, ref int pos, int limit, int maxBytes)
        {
            int len = ReadVarInt(buf, ref pos, limit);
            if (len < 0 || len > maxBytes) throw new InvalidDataException("String length out of range");
            if (pos + len > limit) throw new InvalidDataException("Truncated string");
            var s = Encoding.UTF8.GetString(buf, pos, len);
            pos += len;
            return s;
        }

        public static void WriteString(Stream s, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? "");
            WriteVarInt(s, bytes.Length);
            s.Write(bytes, 0, bytes.Length);
        }

        // ---- Packet framing ----------------------------------------------------------

        /// <summary>Reads one length-prefixed packet. Returns the raw frame (prefix included) and the body.</summary>
        public static async Task<(byte[] Frame, byte[] Body)> ReadPacketAsync(Stream s, int maxLength, CancellationToken ct, byte? firstByte = null)
        {
            var prefix = new MemoryStream(5);
            int length = 0, shift = 0;
            var one = new byte[1];
            bool useFirst = firstByte.HasValue;
            while (true)
            {
                byte b;
                if (useFirst) { b = firstByte.Value; useFirst = false; }
                else
                {
                    int n = await s.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
                    if (n == 0) throw new EndOfStreamException();
                    b = one[0];
                }
                prefix.WriteByte(b);
                length |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift >= 35) throw new InvalidDataException("Packet length VarInt too long");
            }
            if (length <= 0 || length > maxLength) throw new InvalidDataException($"Packet length {length} out of range");

            var body = new byte[length];
            await s.ReadExactlyAsync(body.AsMemory(), ct).ConfigureAwait(false);

            var frame = new byte[prefix.Length + length];
            prefix.ToArray().CopyTo(frame, 0);
            body.CopyTo(frame, (int)prefix.Length);
            return (frame, body);
        }

        public static byte[] BuildPacket(int packetId, Action<Stream> writeBody)
        {
            var body = new MemoryStream();
            WriteVarInt(body, packetId);
            writeBody?.Invoke(body);
            var frame = new MemoryStream();
            WriteVarInt(frame, (int)body.Length);
            body.Position = 0;
            body.CopyTo(frame);
            return frame.ToArray();
        }

        // ---- Handshake ---------------------------------------------------------------

        /// <summary>
        /// Reads the client's first packet. Returns null for a legacy (pre-1.7) server list ping,
        /// which carries no hostname and can't be routed.
        /// </summary>
        public static async Task<Handshake> ReadHandshakeAsync(Stream s, CancellationToken ct)
        {
            var one = new byte[1];
            int n = await s.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException();
            if (one[0] == 0xFE) return null; // legacy ping

            var (frame, body) = await ReadPacketAsync(s, MaxHandshakeLength, ct, one[0]).ConfigureAwait(false);
            int pos = 0, limit = body.Length;
            int packetId = ReadVarInt(body, ref pos, limit);
            if (packetId != 0x00) throw new InvalidDataException($"Expected handshake (0x00), got 0x{packetId:X2}");
            int protocol = ReadVarInt(body, ref pos, limit);
            string address = ReadString(body, ref pos, limit, 255 * 4 + 64);
            if (pos + 2 > limit) throw new InvalidDataException("Truncated handshake port");
            ushort port = (ushort)((body[pos] << 8) | body[pos + 1]);
            pos += 2;
            int next = ReadVarInt(body, ref pos, limit);

            return new Handshake
            {
                ProtocolVersion = protocol,
                RawAddress = address,
                Hostname = NormalizeHostname(address),
                Port = port,
                NextState = next,
                RawBytes = frame
            };
        }

        public static byte[] BuildHandshake(int protocol, string address, ushort port, int nextState)
            => BuildPacket(0x00, b =>
            {
                WriteVarInt(b, protocol);
                WriteString(b, address);
                b.WriteByte((byte)(port >> 8));
                b.WriteByte((byte)(port & 0xFF));
                WriteVarInt(b, nextState);
            });

        /// <summary>
        /// Turns the address the client typed into a routable hostname: lowercase, without
        /// Forge/FML markers (after a NUL), proxy forwarding data (after "///"), a trailing
        /// dot, or a port suffix.
        /// </summary>
        public static string NormalizeHostname(string address)
        {
            if (string.IsNullOrEmpty(address)) return "";
            var h = address;
            int nul = h.IndexOf('\0');
            if (nul >= 0) h = h.Substring(0, nul);
            int fwd = h.IndexOf("///", StringComparison.Ordinal);
            if (fwd >= 0) h = h.Substring(0, fwd);
            h = h.Trim();
            // host:port (IPv6 literals in brackets are left alone)
            if (!h.StartsWith("[") && h.Count(':') == 1) h = h.Substring(0, h.IndexOf(':'));
            h = h.TrimEnd('.');
            return h.ToLowerInvariant();
        }

        private static int Count(this string s, char c)
        {
            int n = 0;
            foreach (var ch in s) if (ch == c) n++;
            return n;
        }

        // ---- Replies for offline / unknown servers -------------------------------------

        public static byte[] BuildStatusResponse(int protocol, string versionName, string motd, int online = 0, int max = 0)
        {
            var json = JsonSerializer.Serialize(new
            {
                version = new { name = versionName, protocol },
                players = new { max, online },
                description = new { text = motd ?? "" }
            });
            return BuildPacket(0x00, b => WriteString(b, json));
        }

        public static byte[] BuildPong(byte[] payload)
            => BuildPacket(0x01, b => b.Write(payload, 0, payload.Length));

        public static byte[] BuildLoginDisconnect(string message)
        {
            var json = JsonSerializer.Serialize(new { text = message ?? "" });
            return BuildPacket(0x00, b => WriteString(b, json));
        }

        /// <summary>
        /// Answers a server-list ping ourselves: waits for the status request, replies with
        /// the given MOTD, then echoes the ping so the client shows a latency.
        /// </summary>
        public static async Task ServeStatusAsync(Stream s, int protocol, string versionName, string motd, CancellationToken ct)
        {
            var (_, req) = await ReadPacketAsync(s, 16, ct).ConfigureAwait(false);
            int pos = 0;
            if (ReadVarInt(req, ref pos, req.Length) != 0x00) return;
            var resp = BuildStatusResponse(protocol, versionName, motd);
            await s.WriteAsync(resp, ct).ConfigureAwait(false);

            try
            {
                var (_, ping) = await ReadPacketAsync(s, 16, ct).ConfigureAwait(false);
                pos = 0;
                if (ReadVarInt(ping, ref pos, ping.Length) == 0x01 && ping.Length - pos == 8)
                {
                    var payload = new byte[8];
                    Array.Copy(ping, pos, payload, 0, 8);
                    await s.WriteAsync(BuildPong(payload), ct).ConfigureAwait(false);
                }
            }
            catch (EndOfStreamException) { /* client didn't ping, that's fine */ }
        }
    }

    public sealed class Handshake
    {
        public int ProtocolVersion;
        public string RawAddress;
        public string Hostname;
        public ushort Port;
        public int NextState;
        /// <summary>The exact bytes received, replayed to the backend unchanged.</summary>
        public byte[] RawBytes;

        public bool IsStatus => NextState == McProtocol.StateStatus;
    }
}
