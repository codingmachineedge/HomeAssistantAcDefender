using System.Collections.Concurrent;
using System.Text.Json;

namespace HomeAssistantAcDefender.Services;

/// <summary>
/// "Sign in with Google" for a LAN-only site, built on the OAuth 2.0 device flow
/// (urn:ietf:params:oauth:grant-type:device_code). The device flow needs NO redirect URI, so it
/// works on http://192.168.x.x:8888 where Google's normal web flow is impossible (Google rejects
/// plain-HTTP and private-IP redirect URIs). The login page shows a short code, the user approves
/// it at google.com/device from any device, and the page polls here until Google confirms.
///
/// Only emails on the configured allow-list may sign in — Google proves WHO the user is; the
/// allow-list decides whether that identity belongs in the command tent. Disabled entirely until
/// Auth:GoogleClientId, Auth:GoogleClientSecret, and Auth:GoogleAllowedEmails are configured.
/// The OAuth client must be of type "TVs and Limited Input devices" in Google Cloud Console.
/// </summary>
public class GoogleDeviceLogin
{
    private const string DeviceCodeEndpoint = "https://oauth2.googleapis.com/device/code";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly ILogger<GoogleDeviceLogin> logger;
    private readonly string? clientId;
    private readonly string? clientSecret;
    private readonly HashSet<string> allowedEmails;

    // Pending sign-ins keyed by an opaque id stored in the visitor's cookie — never the Google
    // device_code itself, so the secret Google handle never leaves the server.
    private readonly ConcurrentDictionary<string, PendingLogin> pending = new();

    private sealed class PendingLogin
    {
        public required string DeviceCode { get; init; }
        public required string UserCode { get; init; }
        public required string VerificationUrl { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public required bool KeepSignedIn { get; init; }
        public int IntervalSeconds { get; set; }
        public DateTimeOffset LastPolledAt { get; set; } = DateTimeOffset.MinValue;
    }

    public GoogleDeviceLogin(IConfiguration configuration, ILogger<GoogleDeviceLogin> logger)
    {
        this.logger = logger;
        clientId = configuration["Auth:GoogleClientId"]?.Trim();
        clientSecret = configuration["Auth:GoogleClientSecret"]?.Trim();
        allowedEmails = (configuration["Auth:GoogleAllowedEmails"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (Enabled)
        {
            logger.LogInformation("Google sign-in enabled ({Count} allowed email(s))", allowedEmails.Count);
        }
    }

    public bool Enabled => !string.IsNullOrEmpty(clientId)
        && !string.IsNullOrEmpty(clientSecret)
        && allowedEmails.Count > 0;

    public sealed record StartResult(string PendingId, string UserCode, string VerificationUrl);

    /// <summary>Begins a device-flow sign-in. Returns null (with a logged reason) on failure.</summary>
    public async Task<StartResult?> StartAsync(bool keepSignedIn)
    {
        if (!Enabled)
        {
            return null;
        }

        try
        {
            using var response = await Http.PostAsync(DeviceCodeEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId!,
                ["scope"] = "email openid"
            }));
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Google device-code request failed: {Json}", json);
                return null;
            }

            var root = doc.RootElement;
            var entry = new PendingLogin
            {
                DeviceCode = root.GetProperty("device_code").GetString()!,
                UserCode = root.GetProperty("user_code").GetString()!,
                VerificationUrl = root.TryGetProperty("verification_url", out var vu)
                    ? vu.GetString()! : "https://www.google.com/device",
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()),
                KeepSignedIn = keepSignedIn,
                IntervalSeconds = root.TryGetProperty("interval", out var iv) ? Math.Max(5, iv.GetInt32()) : 5
            };

            CleanupExpired();
            var pendingId = Guid.NewGuid().ToString("N");
            pending[pendingId] = entry;
            return new StartResult(pendingId, entry.UserCode, entry.VerificationUrl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Google device-code request failed");
            return null;
        }
    }

    public enum PollStatus
    {
        /// <summary>User has not approved yet — keep showing the code.</summary>
        Pending,
        /// <summary>Approved and the email is allow-listed; sign the visitor in.</summary>
        SignedIn,
        /// <summary>Code expired, user declined, email not allow-listed, or unknown pending id.</summary>
        Failed
    }

    public sealed record PollResult(PollStatus Status, string? Email, bool KeepSignedIn, string? UserCode, string? VerificationUrl, string? Error);

    /// <summary>
    /// Polls Google once for the pending sign-in (rate-limited to Google's requested interval —
    /// calls arriving sooner return Pending without a network hop, so an eager page refresh can
    /// never trip Google's slow_down).
    /// </summary>
    public async Task<PollResult> PollAsync(string pendingId)
    {
        if (!Enabled || !pending.TryGetValue(pendingId, out var entry))
        {
            return new PollResult(PollStatus.Failed, null, false, null, null, "Sign-in session not found — start again.");
        }

        var now = DateTimeOffset.UtcNow;
        if (now > entry.ExpiresAt)
        {
            pending.TryRemove(pendingId, out _);
            return new PollResult(PollStatus.Failed, null, false, null, null, "The Google code expired — start again.");
        }

        if (now - entry.LastPolledAt < TimeSpan.FromSeconds(entry.IntervalSeconds))
        {
            return new PollResult(PollStatus.Pending, null, entry.KeepSignedIn, entry.UserCode, entry.VerificationUrl, null);
        }

        entry.LastPolledAt = now;

        try
        {
            using var response = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId!,
                ["client_secret"] = clientSecret!,
                ["device_code"] = entry.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            }));
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!response.IsSuccessStatusCode)
            {
                var error = root.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
                switch (error)
                {
                    case "authorization_pending":
                        return new PollResult(PollStatus.Pending, null, entry.KeepSignedIn, entry.UserCode, entry.VerificationUrl, null);
                    case "slow_down":
                        entry.IntervalSeconds += 5;
                        return new PollResult(PollStatus.Pending, null, entry.KeepSignedIn, entry.UserCode, entry.VerificationUrl, null);
                    case "access_denied":
                        pending.TryRemove(pendingId, out _);
                        return new PollResult(PollStatus.Failed, null, false, null, null, "Google sign-in was declined.");
                    default:
                        pending.TryRemove(pendingId, out _);
                        logger.LogWarning("Google token poll failed: {Json}", json);
                        return new PollResult(PollStatus.Failed, null, false, null, null, "Google sign-in failed — start again.");
                }
            }

            // Granted. The id_token arrives directly from Google's token endpoint over TLS, so its
            // payload is trusted the same way the access token is — no signature check needed here.
            var email = ExtractVerifiedEmail(root.GetProperty("id_token").GetString());
            pending.TryRemove(pendingId, out _);

            if (email is null || !allowedEmails.Contains(email))
            {
                logger.LogWarning("Google sign-in rejected: {Email} is not on the allow-list", email ?? "(no verified email)");
                return new PollResult(PollStatus.Failed, null, false, null, null,
                    "That Google account is not on this defender's allow-list.");
            }

            logger.LogInformation("Google sign-in confirmed for {Email}", email);
            return new PollResult(PollStatus.SignedIn, email, entry.KeepSignedIn, null, null, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Google token poll failed");
            return new PollResult(PollStatus.Pending, null, entry.KeepSignedIn, entry.UserCode, entry.VerificationUrl, null);
        }
    }

    public void Cancel(string pendingId) => pending.TryRemove(pendingId, out _);

    /// <summary>Reads email + email_verified from a Google id_token's payload (base64url JSON).</summary>
    private static string? ExtractVerifiedEmail(string? idToken)
    {
        if (string.IsNullOrEmpty(idToken))
        {
            return null;
        }

        var parts = idToken.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = doc.RootElement;
            var verified = root.TryGetProperty("email_verified", out var ev)
                && (ev.ValueKind == JsonValueKind.True || (ev.ValueKind == JsonValueKind.String && ev.GetString() == "true"));
            return verified && root.TryGetProperty("email", out var em) ? em.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private void CleanupExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, value) in pending)
        {
            if (now > value.ExpiresAt)
            {
                pending.TryRemove(key, out _);
            }
        }
    }
}
