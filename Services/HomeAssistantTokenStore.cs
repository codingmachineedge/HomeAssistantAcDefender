using System.Text.Json;

namespace HomeAssistantAcDefender.Services;

/// <summary>
/// Optional Home Assistant access token entered on the website. The environment/config token
/// always wins when present; this store only fills the gap when no token was configured, so the
/// site can be brought online without editing .env on the docker host. The token lives in
/// App_Data (a persistent volume in production) and is never included in defender snapshots.
/// </summary>
public sealed class HomeAssistantTokenStore
{
    private readonly object gate = new();
    private readonly string filePath;
    private readonly ILogger<HomeAssistantTokenStore> logger;
    private string? token;

    public HomeAssistantTokenStore(IWebHostEnvironment environment, ILogger<HomeAssistantTokenStore> logger)
    {
        this.logger = logger;
        var directory = Path.Combine(environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(directory);
        filePath = Path.Combine(directory, "ha-token.json");
        token = Load();
    }

    public string? Token
    {
        get
        {
            lock (gate)
            {
                return token;
            }
        }
    }

    public bool HasToken => !string.IsNullOrWhiteSpace(Token);

    public void SetToken(string? value)
    {
        lock (gate)
        {
            token = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            try
            {
                if (token is null)
                {
                    File.Delete(filePath);
                }
                else
                {
                    File.WriteAllText(filePath, JsonSerializer.Serialize(new TokenFile(token)));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not persist the Home Assistant token override.");
            }
        }
    }

    private string? Load()
    {
        try
        {
            if (File.Exists(filePath))
            {
                var saved = JsonSerializer.Deserialize<TokenFile>(File.ReadAllText(filePath));
                return string.IsNullOrWhiteSpace(saved?.AccessToken) ? null : saved.AccessToken;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load the Home Assistant token override.");
        }

        return null;
    }

    private sealed record TokenFile(string AccessToken);
}
