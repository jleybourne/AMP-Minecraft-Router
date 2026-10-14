using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using InstanceManagerPlugin;
using ModuleShared;

namespace MinecraftRouter
{
    /// <summary>
    /// Starts a stopped server when a player tries to join it: starts the AMP instance if it
    /// isn't running, then logs in to it the same way ADS does (a one-time system token) and
    /// starts the game server inside it.
    /// </summary>
    internal sealed class InstanceWaker
    {
        private static readonly TimeSpan WakeWindow = TimeSpan.FromMinutes(3);
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        private readonly Func<LocalInstanceManager> _im;
        private readonly ITokenRequester _tokens;
        private readonly ILogger _log;
        private readonly ConcurrentDictionary<Guid, DateTime> _waking = new ConcurrentDictionary<Guid, DateTime>();

        public InstanceWaker(Func<LocalInstanceManager> im, ITokenRequester tokens, ILogger log)
        {
            _im = im;
            _tokens = tokens;
            _log = log;
        }

        public bool IsWaking(Guid id)
            => _waking.TryGetValue(id, out var until) && until > DateTime.UtcNow;

        public void Wake(Guid id, string displayName, string requestedBy)
        {
            var until = DateTime.UtcNow.Add(WakeWindow);
            bool claimed = false;
            _waking.AddOrUpdate(id, _ => { claimed = true; return until; }, (_, old) =>
            {
                if (old > DateTime.UtcNow) return old;
                claimed = true;
                return until;
            });
            if (!claimed) return;
            _ = Task.Run(() => WakeAsync(id, displayName, requestedBy));
        }

        private async Task WakeAsync(Guid id, string name, string requestedBy)
        {
            try
            {
                var im = _im();
                var inst = im?.Instances.FirstOrDefault(i => i.InstanceID == id);
                if (inst == null) { _log.Warning($"Minecraft Router: can't wake {name}, the instance no longer exists."); return; }

                _log.Activity($"Minecraft Router: starting {name} for {requestedBy}.");
                if (!im.InstanceIsRunning(inst))
                {
                    var r = await im.StartInstance(id).ConfigureAwait(false);
                    if (!r.Status)
                    {
                        _log.Warning($"Minecraft Router: couldn't start instance {name}: {r.Reason}");
                        _waking.TryRemove(id, out _);
                        return;
                    }
                }

                // The instance process is up; make sure the game server inside it is too.
                // The instance's web API takes a few seconds to come up after a cold start.
                var deadline = DateTime.UtcNow.AddSeconds(90);
                string lastError = null;
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        var state = await EnsureAppStartedAsync(inst, name).ConfigureAwait(false);
                        if (state != null) { _log.Info($"Minecraft Router: {name} {state}."); return; }
                    }
                    catch (Exception ex) { lastError = ex.Message; }
                    await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
                _log.Warning($"Minecraft Router: started instance {name} but couldn't confirm its server started{(lastError == null ? "" : $" ({lastError})")}.");
            }
            catch (Exception ex)
            {
                _log.Warning($"Minecraft Router: waking {name} failed: {ex.Message}");
                _waking.TryRemove(id, out _);
            }
        }

        // Returns a short description of what happened, or null to retry.
        private async Task<string> EnsureAppStartedAsync(AMPInstanceBase inst, string name)
        {
            var token = await _tokens.RequestTokenAsync($"Minecraft Router wake ({name})", true, null, "MinecraftRouterWake", true, inst.InstanceID).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token)) throw new Exception("no login token");

            var baseUri = new Uri(inst.URL);
            using var login = await CallAsync(baseUri, "Core/Login", null, new { username = "AMP_SYSTEM", password = "", token, rememberMe = false }).ConfigureAwait(false);
            var root = login.RootElement;
            if (!root.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True)
                throw new Exception("instance login refused");
            var session = root.TryGetProperty("sessionID", out var sid) ? sid.GetString() : null;
            if (string.IsNullOrEmpty(session)) throw new Exception("no session");

            try
            {
                using var status = await CallAsync(baseUri, "Core/GetStatus", session, new { }).ConfigureAwait(false);
                int state = status.RootElement.TryGetProperty("State", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : -1;
                var appState = (ApplicationState)state;
                switch (appState)
                {
                    case ApplicationState.Ready:
                        return "is already running";
                    case ApplicationState.PreStart:
                    case ApplicationState.Configuring:
                    case ApplicationState.Starting:
                    case ApplicationState.Restarting:
                    case ApplicationState.Installing:
                    case ApplicationState.Updating:
                    case ApplicationState.Sleeping: // AMP's own sleep mode wakes on connect
                        return "is starting";
                    case ApplicationState.Stopped:
                    case ApplicationState.Failed:
                    case ApplicationState.Undefined:
                        using (await CallAsync(baseUri, "Core/Start", session, new { }).ConfigureAwait(false)) { }
                        return "was told to start";
                    default:
                        return null; // stopping, waiting etc. - try again shortly
                }
            }
            finally
            {
                try { using (await CallAsync(baseUri, "Core/Logout", session, new { }).ConfigureAwait(false)) { } } catch { }
            }
        }

        private static async Task<JsonDocument> CallAsync(Uri baseUri, string method, string session, object body)
        {
            var json = JsonSerializer.Serialize(body);
            if (session != null)
            {
                // AMP accepts the session either as a bearer token or as SESSIONID in the body.
                json = json.Length > 2 ? json.Insert(1, $"\"SESSIONID\":{JsonSerializer.Serialize(session)},") : $"{{\"SESSIONID\":{JsonSerializer.Serialize(session)}}}";
            }
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "API/" + method))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (session != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        }
    }
}
