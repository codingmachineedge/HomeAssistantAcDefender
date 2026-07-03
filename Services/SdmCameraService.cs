using System.Text.Json;

namespace HomeAssistantAcDefender.Services;

/// <summary>
/// Google Smart Device Management camera bridge: exchanges a browser WebRTC offer for a Nest
/// doorbell/camera answer via CameraLiveStream.GenerateWebRtcStream, so the front-door live view
/// can render inside the (auth-gated) defender site. Configured with an OAuth refresh token minted
/// during the one-time SDM consent; disabled entirely when the Sdm__* settings are absent.
/// </summary>
public class SdmCameraService
{
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly ILogger<SdmCameraService> logger;
    private readonly string? clientId;
    private readonly string? clientSecret;
    private readonly string? refreshToken;
    private readonly string? doorbellDevice;

    private readonly object tokenGate = new();
    private string? accessToken;
    private DateTimeOffset accessTokenExpiresAt = DateTimeOffset.MinValue;

    public SdmCameraService(IConfiguration configuration, ILogger<SdmCameraService> logger)
    {
        this.logger = logger;
        clientId = configuration["Sdm:ClientId"]?.Trim();
        clientSecret = configuration["Sdm:ClientSecret"]?.Trim();
        refreshToken = configuration["Sdm:RefreshToken"]?.Trim();
        doorbellDevice = configuration["Sdm:DoorbellDevice"]?.Trim();

        if (Enabled)
        {
            logger.LogInformation("SDM camera bridge enabled for {Device}", doorbellDevice);
        }
    }

    public bool Enabled => !string.IsNullOrEmpty(clientId)
        && !string.IsNullOrEmpty(clientSecret)
        && !string.IsNullOrEmpty(refreshToken)
        && !string.IsNullOrEmpty(doorbellDevice);

    /// <summary>Exchanges the browser's SDP offer for Google's SDP answer. Null on any failure.</summary>
    public async Task<string?> GenerateWebRtcAnswerAsync(string offerSdp)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(offerSdp))
        {
            return null;
        }

        var token = await GetAccessTokenAsync();
        if (token is null)
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://smartdevicemanagement.googleapis.com/v1/{doorbellDevice}:executeCommand")
            {
                Content = JsonContent.Create(new
                {
                    command = "sdm.devices.commands.CameraLiveStream.GenerateWebRtcStream",
                    @params = new { offerSdp }
                })
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await Http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("SDM GenerateWebRtcStream failed ({Status}): {Body}", (int)response.StatusCode, json);
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("results", out var results)
                && results.TryGetProperty("answerSdp", out var answer)
                    ? answer.GetString()
                    : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SDM GenerateWebRtcStream failed");
            return null;
        }
    }

    private async Task<string?> GetAccessTokenAsync()
    {
        lock (tokenGate)
        {
            if (accessToken is not null && DateTimeOffset.UtcNow < accessTokenExpiresAt - TimeSpan.FromMinutes(2))
            {
                return accessToken;
            }
        }

        try
        {
            using var response = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId!,
                ["client_secret"] = clientSecret!,
                ["refresh_token"] = refreshToken!,
                ["grant_type"] = "refresh_token"
            }));
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!response.IsSuccessStatusCode || !doc.RootElement.TryGetProperty("access_token", out var tokenProp))
            {
                logger.LogWarning("SDM token refresh failed: {Body}", json);
                return null;
            }

            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
            lock (tokenGate)
            {
                accessToken = tokenProp.GetString();
                accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
                return accessToken;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SDM token refresh failed");
            return null;
        }
    }
}
