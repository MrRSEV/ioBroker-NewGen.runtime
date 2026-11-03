using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ioBroker_NewGen.Core.Adapters.Configuration
{
    /// <summary>
    /// OAuth-2.0-Anbieter eines Adapters (Authorization-Code-Flow mit Loopback-Redirect, RFC 8252).
    /// Alle Schlüssel verweisen auf Felder im Schema des Adapters (<see cref="AdapterConfigSchema.Native"/>).
    /// </summary>
    public sealed record OAuthProviderSpec(string Id, string Label, string AuthorizeUrl, string TokenUrl)
    {
        public string Scope { get; init; } = string.Empty;

        /// <summary>Optionaler Schlüssel, dessen Wert den Scope überschreibt.</summary>
        public string? ScopeKey { get; init; }

        public string ClientIdKey { get; init; } = "clientId";

        public string? ClientSecretKey { get; init; }

        public string TokenKey { get; init; } = "token";

        public string? RefreshTokenKey { get; init; }

        /// <summary>Schlüssel für den Ablaufzeitpunkt (Unix-Sekunden).</summary>
        public string? ExpiresAtKey { get; init; }

        public bool UsePkce { get; init; } = true;

        /// <summary>Fester Port, damit die Redirect-URI beim Anbieter registriert werden kann.</summary>
        public int RedirectPort { get; init; } = 53682;

        public IReadOnlyDictionary<string, string> ExtraAuthParameters { get; init; } = new Dictionary<string, string>();

        public string RedirectUri => $"http://localhost:{RedirectPort}/callback";
    }

    public sealed record OAuthTokens(string AccessToken, string? RefreshToken, long? ExpiresAt);

    public sealed class OAuthSession
    {
        internal OAuthSession(string id, string instanceId, string adapterName, string providerId)
        {
            Id = id;
            InstanceId = instanceId;
            AdapterName = adapterName;
            ProviderId = providerId;
        }

        public string Id { get; }

        public string InstanceId { get; }

        public string AdapterName { get; }

        public string ProviderId { get; }

        /// <summary><c>pending</c>, <c>success</c>, <c>error</c> oder <c>cancelled</c>.</summary>
        public string State { get; internal set; } = "pending";

        public string? Message { get; internal set; }

        public string AuthorizeUrl { get; internal set; } = string.Empty;

        public bool BrowserLaunched { get; internal set; }

        /// <summary>Wird vom Aufrufer gesetzt, sobald das Ergebnis verarbeitet wurde (z. B. Adapter-Neustart).</summary>
        public bool Handled { get; set; }

        internal CancellationTokenSource Cts { get; } = new();
    }

    /// <summary>
    /// Führt OAuth-Anmeldungen in einer isolierten Browser-Sitzung (temporäres Profil, privater Modus) durch,
    /// fängt den Authorization-Code über einen lokalen Loopback-Listener ab und legt die Tokens in der Instanzkonfiguration ab.
    /// </summary>
    public sealed class OAuthFlowService
    {
        private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly TimeSpan FlowTimeout = TimeSpan.FromMinutes(5);

        private readonly AdapterConfigService _configs;
        private readonly HttpClient _http;
        private readonly ConcurrentDictionary<string, OAuthSession> _sessions = new();
        private readonly ConcurrentDictionary<int, OAuthSession> _activeByPort = new();

        public OAuthFlowService(AdapterConfigService configs, HttpClient? http = null)
        {
            _configs = configs;
            _http = http ?? SharedHttp;
        }

        public OAuthProviderSpec GetProvider(string adapterName, string providerId)
        {
            return _configs.GetSchema(adapterName)?.OAuthProviders.FirstOrDefault(p => p.Id == providerId)
                ?? throw new AdapterConfigException("Unbekannter Anmeldeanbieter.");
        }

        public OAuthSession? Get(string sessionId)
            => _sessions.TryGetValue(sessionId, out var session) ? session : null;

        public OAuthSession Start(string instanceId, string adapterName, string providerId)
        {
            var spec = GetProvider(adapterName, providerId);
            var values = _configs.GetValues(instanceId, adapterName);
            var clientId = values[spec.ClientIdKey]?.ToString();
            if (string.IsNullOrWhiteSpace(clientId))
            {
                throw new AdapterConfigException($"'{spec.ClientIdKey}' ist nicht konfiguriert.");
            }

            var session = new OAuthSession(Guid.NewGuid().ToString("N"), instanceId, adapterName, providerId);
            if (!_activeByPort.TryAdd(spec.RedirectPort, session))
            {
                throw new AdapterConfigException("Es läuft bereits eine Anmeldung. Bitte abbrechen oder warten.");
            }

            HttpListener? listener = null;
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{spec.RedirectPort}/");
                listener.Start();

                var state = RandomToken(32);
                var verifier = spec.UsePkce ? RandomToken(48) : null;
                var scope = spec.ScopeKey is not null && !string.IsNullOrWhiteSpace(values[spec.ScopeKey]?.ToString())
                    ? values[spec.ScopeKey]!.ToString()
                    : spec.Scope;
                session.AuthorizeUrl = BuildAuthorizeUrl(spec, clientId, scope, state, verifier);
                session.BrowserLaunched = false;
                _sessions[session.Id] = session;

                var browser = IsolatedBrowser.TryLaunch(session.AuthorizeUrl);
                session.BrowserLaunched = browser is not null;
                _ = Task.Run(() => RunAsync(session, spec, listener, browser, state, verifier));
                return session;
            }
            catch (HttpListenerException)
            {
                _activeByPort.TryRemove(spec.RedirectPort, out _);
                listener?.Close();
                throw new AdapterConfigException($"Port {spec.RedirectPort} für den Anmelde-Callback ist nicht verfügbar.");
            }
            catch
            {
                _activeByPort.TryRemove(spec.RedirectPort, out _);
                listener?.Close();
                throw;
            }
        }

        public bool Cancel(string sessionId)
        {
            if (!_sessions.TryGetValue(sessionId, out var session) || session.State != "pending")
            {
                return false;
            }

            session.Cts.Cancel();
            return true;
        }

        /// <summary>Erneuert das Access-Token mit dem gespeicherten Refresh-Token und speichert das Ergebnis.</summary>
        public async Task<OAuthTokens> RefreshAsync(
            string instanceId,
            string adapterName,
            string providerId,
            CancellationToken cancellationToken = default)
        {
            var spec = GetProvider(adapterName, providerId);
            var values = _configs.GetValues(instanceId, adapterName);
            var refresh = spec.RefreshTokenKey is null ? null : values[spec.RefreshTokenKey]?.ToString();
            if (string.IsNullOrWhiteSpace(refresh))
            {
                throw new AdapterConfigException("Kein Refresh-Token vorhanden. Bitte neu anmelden.");
            }

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["client_id"] = values[spec.ClientIdKey]?.ToString() ?? string.Empty
            };
            AddClientSecret(spec, values, form);
            return await RequestTokensAsync(instanceId, adapterName, spec, form, cancellationToken);
        }

        private async Task RunAsync(
            OAuthSession session,
            OAuthProviderSpec spec,
            HttpListener listener,
            IsolatedBrowser? browser,
            string state,
            string? verifier)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session.Cts.Token);
                timeout.CancelAfter(FlowTimeout);
                using var registration = timeout.Token.Register(() =>
                {
                    try
                    {
                        listener.Close();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                });

                while (true)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await listener.GetContextAsync();
                    }
                    catch (Exception) when (timeout.IsCancellationRequested)
                    {
                        Finish(session, session.Cts.IsCancellationRequested ? "cancelled" : "error",
                            session.Cts.IsCancellationRequested ? "Abgebrochen." : "Zeitüberschreitung bei der Anmeldung.");
                        return;
                    }

                    var request = context.Request;
                    if (request.Url?.AbsolutePath != "/callback")
                    {
                        Respond(context, 404, "Nicht gefunden.");
                        continue;
                    }

                    var returnedState = request.QueryString["state"] ?? string.Empty;
                    if (!FixedTimeEquals(returnedState, state))
                    {
                        Respond(context, 400, "Ungültiger State.");
                        continue;
                    }

                    var error = request.QueryString["error"];
                    var code = request.QueryString["code"];
                    if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
                    {
                        Respond(context, 400, "Anmeldung fehlgeschlagen. Das Fenster kann geschlossen werden.");
                        Finish(session, "error", $"Anbieter meldet einen Fehler: {Sanitize(error ?? "kein Code")}");
                        return;
                    }

                    Respond(context, 200, "Anmeldung empfangen. Das Fenster kann geschlossen werden.");
                    try
                    {
                        var values = _configs.GetValues(session.InstanceId, session.AdapterName);
                        var form = new Dictionary<string, string>
                        {
                            ["grant_type"] = "authorization_code",
                            ["code"] = code,
                            ["redirect_uri"] = spec.RedirectUri,
                            ["client_id"] = values[spec.ClientIdKey]?.ToString() ?? string.Empty
                        };
                        AddClientSecret(spec, values, form);
                        if (verifier is not null)
                        {
                            form["code_verifier"] = verifier;
                        }

                        await RequestTokensAsync(session.InstanceId, session.AdapterName, spec, form, timeout.Token);
                        Finish(session, "success", null);
                    }
                    catch (Exception ex) when (ex is AdapterConfigException or HttpRequestException or TaskCanceledException)
                    {
                        Finish(session, "error", ex.Message);
                    }

                    return;
                }
            }
            catch (Exception ex)
            {
                Finish(session, "error", ex.Message);
            }
            finally
            {
                try
                {
                    listener.Close();
                }
                catch (ObjectDisposedException)
                {
                }

                _activeByPort.TryRemove(new KeyValuePair<int, OAuthSession>(spec.RedirectPort, session));
                if (browser is not null)
                {
                    await Task.Delay(1500);
                    browser.Dispose();
                }
            }
        }

        private async Task<OAuthTokens> RequestTokensAsync(
            string instanceId,
            string adapterName,
            OAuthProviderSpec spec,
            Dictionary<string, string> form,
            CancellationToken cancellationToken)
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await _http.PostAsync(spec.TokenUrl, content, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonObject? body;
            try
            {
                body = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
                body = null;
            }

            if (!response.IsSuccessStatusCode || body is null)
            {
                var detail = body?["error_description"]?.ToString() ?? body?["error"]?.ToString() ?? "unerwartete Antwort";
                throw new AdapterConfigException($"Token-Anfrage fehlgeschlagen ({(int)response.StatusCode}): {Sanitize(detail)}");
            }

            var access = body["access_token"]?.ToString();
            if (string.IsNullOrEmpty(access))
            {
                throw new AdapterConfigException("Antwort enthält kein Access-Token.");
            }

            var refresh = body["refresh_token"]?.ToString();
            long? expiresAt = null;
            if (body["expires_in"] is JsonValue expires && expires.TryGetValue<long>(out var seconds))
            {
                expiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + seconds;
            }

            var update = new JsonObject { [spec.TokenKey] = access };
            if (spec.RefreshTokenKey is not null && !string.IsNullOrEmpty(refresh))
            {
                update[spec.RefreshTokenKey] = refresh;
            }

            if (spec.ExpiresAtKey is not null && expiresAt is not null)
            {
                update[spec.ExpiresAtKey] = expiresAt;
            }

            _configs.Save(instanceId, adapterName, update);
            return new OAuthTokens(access, refresh, expiresAt);
        }

        private static void AddClientSecret(OAuthProviderSpec spec, JsonObject values, Dictionary<string, string> form)
        {
            var secret = spec.ClientSecretKey is null ? null : values[spec.ClientSecretKey]?.ToString();
            if (!string.IsNullOrEmpty(secret))
            {
                form["client_secret"] = secret;
            }
        }

        private static string BuildAuthorizeUrl(OAuthProviderSpec spec, string clientId, string scope, string state, string? verifier)
        {
            var query = new List<KeyValuePair<string, string>>
            {
                new("response_type", "code"),
                new("client_id", clientId),
                new("redirect_uri", spec.RedirectUri),
                new("state", state)
            };
            if (!string.IsNullOrWhiteSpace(scope))
            {
                query.Add(new("scope", scope));
            }

            if (verifier is not null)
            {
                var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
                query.Add(new("code_challenge", challenge));
                query.Add(new("code_challenge_method", "S256"));
            }

            query.AddRange(spec.ExtraAuthParameters);
            var separator = spec.AuthorizeUrl.Contains('?') ? "&" : "?";
            return spec.AuthorizeUrl + separator + string.Join("&", query.Select(
                p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        }

        private static void Finish(OAuthSession session, string state, string? message)
        {
            session.Message = message;
            session.State = state;
        }

        private static void Respond(HttpListenerContext context, int status, string message)
        {
            var html = "<!doctype html><meta charset=\"utf-8\"><title>ioBroker-NewGen</title><body style=\"font-family:sans-serif\"><p>"
                + WebUtility.HtmlEncode(message) + "</p></body>";
            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.StatusCode = status;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.Close();
        }

        private static string Sanitize(string value)
            => new(value.Where(c => !char.IsControl(c)).Take(200).ToArray());

        private static bool FixedTimeEquals(string a, string b)
            => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

        private static string RandomToken(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

        private static string Base64Url(byte[] data)
            => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Startet Chrome/Edge mit einem frischen temporären Profil im privaten Modus und räumt Prozess und Profil danach auf.
    /// </summary>
    internal sealed class IsolatedBrowser : IDisposable
    {
        private readonly Process _process;
        private readonly string _profileDir;

        private IsolatedBrowser(Process process, string profileDir)
        {
            _process = process;
            _profileDir = profileDir;
        }

        public static IsolatedBrowser? TryLaunch(string url)
        {
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var exe = FindBrowser(out var isEdge);
            if (exe is null)
            {
                return null;
            }

            var profile = Path.Combine(Path.GetTempPath(), "ng-oauth-" + Guid.NewGuid().ToString("N"));
            try
            {
                var info = new ProcessStartInfo(exe) { UseShellExecute = false };
                info.ArgumentList.Add("--user-data-dir=" + profile);
                info.ArgumentList.Add("--no-first-run");
                info.ArgumentList.Add("--no-default-browser-check");
                info.ArgumentList.Add("--disable-extensions");
                info.ArgumentList.Add(isEdge ? "--inprivate" : "--incognito");
                info.ArgumentList.Add(url);
                var process = Process.Start(info);
                return process is null ? null : new IsolatedBrowser(process, profile);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(3000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                _process.Dispose();
            }

            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(_profileDir))
                    {
                        Directory.Delete(_profileDir, recursive: true);
                    }

                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(500);
                }
            }
        }

        private static string? FindBrowser(out bool isEdge)
        {
            isEdge = false;
            var candidates = new List<(string Path, bool Edge)>();
            if (OperatingSystem.IsWindows())
            {
                foreach (var root in new[]
                {
                    Environment.GetEnvironmentVariable("ProgramFiles"),
                    Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                    Environment.GetEnvironmentVariable("LOCALAPPDATA")
                })
                {
                    if (string.IsNullOrEmpty(root))
                    {
                        continue;
                    }

                    candidates.Add((Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"), true));
                    candidates.Add((Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"), false));
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                candidates.Add(("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge", true));
                candidates.Add(("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", false));
            }
            else
            {
                var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
                foreach (var (name, edge) in new[]
                {
                    ("microsoft-edge", true), ("google-chrome", false), ("chromium", false), ("chromium-browser", false)
                })
                {
                    foreach (var dir in dirs)
                    {
                        candidates.Add((Path.Combine(dir, name), edge));
                    }
                }
            }

            foreach (var (path, edge) in candidates)
            {
                if (File.Exists(path))
                {
                    isEdge = edge;
                    return path;
                }
            }

            return null;
        }
    }
}
