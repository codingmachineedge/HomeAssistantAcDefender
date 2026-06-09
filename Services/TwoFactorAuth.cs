using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HomeAssistantAcDefender.Services;

public class TwoFactorAuth
{
    private const int PasswordSaltBytes = 16;
    private const int PasswordHashBytes = 32;
    private const int PasswordIterations = 100_000;

    private string? _secret;
    private readonly string _secretFilePath;
    private readonly string _authConfigFilePath;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TwoFactorAuth> _logger;

    private bool _passwordEnabled;
    private byte[]? _passwordHash;
    private byte[]? _passwordSalt;
    private readonly string? _configPassword;

    // Multi-user accounts (username + password). The first account created is the owner; later sign-ups
    // require the owner-set registration code.
    private readonly List<Account> _accounts = new();
    private string? _registrationCode;

    private sealed class Account
    {
        public string Username { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string PasswordSalt { get; set; } = "";
        public bool IsOwner { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    public bool HasAnyAccount => _accounts.Count > 0;
    public bool HasRegistrationCode => !string.IsNullOrEmpty(_registrationCode);
    public int AccountCount => _accounts.Count;
    public IReadOnlyList<string> AccountUsernames => _accounts.Select(a => a.IsOwner ? $"{a.Username} (owner)" : a.Username).ToList();

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_secret);
    public string? ManualKey => _secret;

    /// <summary>A password value is configured (via file or app config).</summary>
    public bool HasPassword => _passwordHash is not null || !string.IsNullOrEmpty(_configPassword);

    /// <summary>Password login is switched on and a password value is actually set.</summary>
    public bool IsPasswordEnabled => (_passwordEnabled && _passwordHash is not null)
                                     || !string.IsNullOrEmpty(_configPassword);

    public TwoFactorAuth(IConfiguration configuration, IWebHostEnvironment env, ILogger<TwoFactorAuth> logger)
    {
        _configuration = configuration;
        _logger = logger;

        _secret = configuration["TwoFactor:Secret"];

        if (!string.IsNullOrWhiteSpace(_secret))
        {
            _secret = _secret.Trim();
            logger.LogInformation("TOTP secret loaded from configuration");
        }

        _secretFilePath = Path.Combine(env.ContentRootPath, "App_Data", "totp-secret.json");
        _authConfigFilePath = Path.Combine(env.ContentRootPath, "App_Data", "auth-config.json");

        if (string.IsNullOrWhiteSpace(_secret) && File.Exists(_secretFilePath))
        {
            try
            {
                var json = File.ReadAllText(_secretFilePath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("secret", out var secretProp))
                {
                    _secret = secretProp.GetString()?.Trim();
                    logger.LogInformation("TOTP secret loaded from file");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read TOTP secret file");
            }
        }

        // Optional config override (mirrors TwoFactor:Secret). When set, it always wins.
        var configPassword = configuration["Auth:Password"];
        if (!string.IsNullOrEmpty(configPassword))
        {
            _configPassword = configPassword;
            _passwordEnabled = true;
            logger.LogInformation("Login password loaded from configuration");
        }

        LoadAuthConfig();
    }

    private void LoadAuthConfig()
    {
        if (!File.Exists(_authConfigFilePath))
            return;

        try
        {
            var json = File.ReadAllText(_authConfigFilePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("passwordEnabled", out var enabledProp))
                _passwordEnabled = enabledProp.GetBoolean();

            if (root.TryGetProperty("passwordHash", out var hashProp) &&
                root.TryGetProperty("passwordSalt", out var saltProp))
            {
                var hash = hashProp.GetString();
                var salt = saltProp.GetString();
                if (!string.IsNullOrWhiteSpace(hash) && !string.IsNullOrWhiteSpace(salt))
                {
                    _passwordHash = Convert.FromBase64String(hash);
                    _passwordSalt = Convert.FromBase64String(salt);
                }
            }

            if (root.TryGetProperty("registrationCode", out var codeProp) && codeProp.ValueKind == JsonValueKind.String)
            {
                _registrationCode = codeProp.GetString();
            }

            if (root.TryGetProperty("accounts", out var accountsProp) && accountsProp.ValueKind == JsonValueKind.Array)
            {
                _accounts.Clear();
                foreach (var element in accountsProp.EnumerateArray())
                {
                    var username = element.TryGetProperty("username", out var up) ? up.GetString() : null;
                    var hash = element.TryGetProperty("passwordHash", out var hp) ? hp.GetString() : null;
                    var salt = element.TryGetProperty("passwordSalt", out var sp) ? sp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(salt))
                    {
                        continue;
                    }

                    _accounts.Add(new Account
                    {
                        Username = username,
                        PasswordHash = hash,
                        PasswordSalt = salt,
                        IsOwner = element.TryGetProperty("isOwner", out var io) && io.ValueKind == JsonValueKind.True,
                        CreatedAt = element.TryGetProperty("createdAt", out var ca) && ca.TryGetDateTimeOffset(out var dt) ? dt : DateTimeOffset.UtcNow
                    });
                }
            }

            _logger.LogInformation("Auth config loaded from file ({Accounts} account(s))", _accounts.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read auth config file");
        }
    }

    private void SaveAuthConfig()
    {
        var dir = Path.GetDirectoryName(_authConfigFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var content = JsonSerializer.Serialize(new
        {
            passwordEnabled = _passwordEnabled,
            passwordHash = _passwordHash is null ? null : Convert.ToBase64String(_passwordHash),
            passwordSalt = _passwordSalt is null ? null : Convert.ToBase64String(_passwordSalt),
            registrationCode = _registrationCode,
            accounts = _accounts.Select(a => new
            {
                username = a.Username,
                passwordHash = a.PasswordHash,
                passwordSalt = a.PasswordSalt,
                isOwner = a.IsOwner,
                createdAt = a.CreatedAt
            }).ToArray(),
            updatedAt = DateTimeOffset.UtcNow
        });
        File.WriteAllText(_authConfigFilePath, content);
        _logger.LogInformation("Auth config saved to file");
    }

    /// <summary>Sets (or replaces) the login password and turns password login on.</summary>
    public void SetPassword(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password must not be empty.", nameof(password));

        _passwordSalt = RandomNumberGenerator.GetBytes(PasswordSaltBytes);
        _passwordHash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            _passwordSalt,
            PasswordIterations,
            HashAlgorithmName.SHA256,
            PasswordHashBytes);
        _passwordEnabled = true;
        SaveAuthConfig();
    }

    /// <summary>Removes the stored password and turns password login off.</summary>
    public void ClearPassword()
    {
        _passwordHash = null;
        _passwordSalt = null;
        _passwordEnabled = false;
        SaveAuthConfig();
    }

    /// <summary>Turns password login on or off without changing the stored value.</summary>
    public void SetPasswordEnabled(bool enabled)
    {
        _passwordEnabled = enabled;
        SaveAuthConfig();
    }

    public bool ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return false;

        if (!string.IsNullOrEmpty(_configPassword))
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(password),
                Encoding.UTF8.GetBytes(_configPassword));

        if (_passwordHash is null || _passwordSalt is null)
            return false;

        var candidate = Rfc2898DeriveBytes.Pbkdf2(
            password,
            _passwordSalt,
            PasswordIterations,
            HashAlgorithmName.SHA256,
            PasswordHashBytes);
        return CryptographicOperations.FixedTimeEquals(candidate, _passwordHash);
    }

    // ───────────────────────── Accounts ─────────────────────────

    /// <summary>Creates an account. The first account is the owner (no code needed); later sign-ups
    /// require the owner-set registration code. Returns false with a user-facing <paramref name="error"/>.</summary>
    public bool TryCreateAccount(string? username, string? password, string? registrationCode, out string error)
    {
        error = string.Empty;
        username = (username ?? string.Empty).Trim();
        password ??= string.Empty;

        if (string.IsNullOrWhiteSpace(username) || username.Length > 64)
        {
            error = "Enter a username.";
            return false;
        }

        if (password.Length < 6)
        {
            error = "Password must be at least 6 characters.";
            return false;
        }

        if (_accounts.Any(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase)))
        {
            error = "That username is already taken.";
            return false;
        }

        var isOwner = _accounts.Count == 0;
        if (!isOwner)
        {
            if (!HasRegistrationCode)
            {
                error = "Sign-ups are closed until the owner sets a registration code.";
                return false;
            }

            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes((registrationCode ?? string.Empty).Trim()),
                    Encoding.UTF8.GetBytes(_registrationCode!)))
            {
                error = "Incorrect registration code.";
                return false;
            }
        }

        var salt = RandomNumberGenerator.GetBytes(PasswordSaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, PasswordHashBytes);
        _accounts.Add(new Account
        {
            Username = username,
            PasswordHash = Convert.ToBase64String(hash),
            PasswordSalt = Convert.ToBase64String(salt),
            IsOwner = isOwner,
            CreatedAt = DateTimeOffset.UtcNow
        });
        SaveAuthConfig();
        _logger.LogInformation("Account created '{Username}' (owner={Owner})", username, isOwner);
        return true;
    }

    /// <summary>Validates a username + password against the stored accounts.</summary>
    public bool ValidateAccount(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        var account = _accounts.FirstOrDefault(a => string.Equals(a.Username, username.Trim(), StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(account.PasswordSalt);
            var expected = Convert.FromBase64String(account.PasswordHash);
            var candidate = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, PasswordHashBytes);
            return CryptographicOperations.FixedTimeEquals(candidate, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Owner-set code required for additional sign-ups. Empty/null closes sign-ups.</summary>
    public void SetRegistrationCode(string? code)
    {
        _registrationCode = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        SaveAuthConfig();
    }

    public string GenerateNewSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        return Base32Encode(bytes);
    }

    public void SaveSecret(string secret)
    {
        _secret = secret.Trim();
        var dir = Path.GetDirectoryName(_secretFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var content = JsonSerializer.Serialize(new { secret = _secret, configuredAt = DateTimeOffset.UtcNow });
        File.WriteAllText(_secretFilePath, content);
        _logger.LogInformation("TOTP secret saved to file");
    }

    public bool ValidateCode(string secret, string code)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code))
            return false;

        code = code.Trim();
        if (code.Length != 6 || !code.All(char.IsDigit))
            return false;

        var secretBytes = Base32Decode(secret);
        var now = DateTimeOffset.UtcNow;
        var counter = (long)Math.Floor(now.ToUnixTimeSeconds() / 30.0);

        for (long offset = -1; offset <= 1; offset++)
        {
            if (ComputeTotpCode(secretBytes, counter + offset) == code)
                return true;
        }

        return false;
    }

    public bool ValidateCurrentCode(string code)
    {
        if (_secret == null)
            throw new InvalidOperationException("No TOTP secret configured");
        return ValidateCode(_secret, code);
    }

    public string GetOtpAuthUrl(string secret, string label = "AC Defender")
    {
        var encodedLabel = Uri.EscapeDataString(label);
        var encodedIssuer = Uri.EscapeDataString("AC Defender");
        return $"otpauth://totp/{encodedLabel}?secret={secret}&issuer={encodedIssuer}&algorithm=SHA1&digits=6&period=30";
    }

    public string GenerateQrSvg(string url)
    {
        return QrSvgGenerator.Generate(url, 8);
    }

    private static string ComputeTotpCode(byte[] key, long counter)
    {
        var counterBytes = new byte[8];
        for (int i = 7; i >= 0; i--)
        {
            counterBytes[i] = (byte)(counter & 0xFF);
            counter >>= 8;
        }

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(counterBytes);

        int offset = hash[^1] & 0x0F;
        int binary =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        return (binary % 1_000_000).ToString("D6");
    }

    private static readonly char[] Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".ToCharArray();

    public static string Base32Encode(byte[] data)
    {
        var result = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0;
        int bitsLeft = 0;

        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                result.Append(Base32Alphabet[(buffer >> bitsLeft) & 0x1F]);
            }
        }

        if (bitsLeft > 0)
        {
            buffer <<= (5 - bitsLeft);
            result.Append(Base32Alphabet[buffer & 0x1F]);
        }

        int padding = (8 - (result.Length % 8)) % 8;
        result.Append('=', padding);

        return result.ToString();
    }

    public static byte[] Base32Decode(string base32)
    {
        base32 = base32.TrimEnd('=').ToUpperInvariant().Replace(" ", "").Replace("-", "");

        var result = new List<byte>();
        int buffer = 0;
        int bitsLeft = 0;

        foreach (char c in base32)
        {
            int value = Array.IndexOf(Base32Alphabet, c);
            if (value < 0)
                throw new FormatException($"Invalid Base32 character: '{c}'");

            buffer = (buffer << 5) | value;
            bitsLeft += 5;

            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                result.Add((byte)(buffer >> bitsLeft));
            }
        }

        return result.ToArray();
    }
}

internal static class QrSvgGenerator
{
    private const int Size = 37;
    private const int EccCodewords = 67;
    private const int DataCodewords = 67;
    private const int MaxDataBits = DataCodewords * 8;

    public static string Generate(string text, int pixelSize)
    {
        var modules = Encode(text);
        return RenderSvg(modules, pixelSize);
    }

    private static bool[,] Encode(string text)
    {
        var dataBits = EncodeData(text);
        var dataBytes = BitsToBytes(dataBits, MaxDataBits);
        var eccBytes = GenerateEcc(dataBytes, EccCodewords);
        var allBytes = dataBytes.Concat(eccBytes).ToArray();

        var modules = new bool[Size, Size];
        PlaceFunctionPatterns(modules);
        PlaceDataBits(modules, allBytes);
        ApplyMask(modules, 2);

        return modules;
    }

    private static List<int> EncodeData(string text)
    {
        var bits = new List<int>();

        bits.AddRange(new[] { 0, 1, 0, 0 });

        int count = text.Length;
        for (int i = 7; i >= 0; i--)
            bits.Add((count >> i) & 1);

        foreach (char c in text)
        {
            int b = c;
            for (int i = 7; i >= 0; i--)
                bits.Add((b >> i) & 1);
        }

        bits.AddRange(new[] { 0, 0, 0, 0 });

        while (bits.Count % 8 != 0)
            bits.Add(0);

        int padByte = 0;
        while (bits.Count < MaxDataBits)
        {
            bits.AddRange(ByteToBits(padByte == 0 ? 0xEC : 0x11));
            padByte ^= 1;
        }

        while (bits.Count > MaxDataBits)
            bits.RemoveAt(bits.Count - 1);

        return bits;
    }

    private static List<int> ByteToBits(int b)
    {
        var bits = new List<int>();
        for (int i = 7; i >= 0; i--)
            bits.Add((b >> i) & 1);
        return bits;
    }

    private static byte[] BitsToBytes(List<int> bits, int maxBits)
    {
        while (bits.Count < maxBits)
            bits.Add(0);
        if (bits.Count > maxBits)
            bits = bits.Take(maxBits).ToList();

        var bytes = new byte[bits.Count / 8];
        for (int i = 0; i < bytes.Length; i++)
        {
            int val = 0;
            for (int j = 0; j < 8; j++)
                val = (val << 1) | bits[i * 8 + j];
            bytes[i] = (byte)val;
        }
        return bytes;
    }

    private static void PlaceFunctionPatterns(bool[,] m)
    {
        for (int r = 0; r < Size; r++)
            for (int c = 0; c < Size; c++)
                m[r, c] = false;

        PlaceFinder(m, 0, 0);
        PlaceFinder(m, 0, Size - 7);
        PlaceFinder(m, Size - 7, 0);

        for (int i = 0; i < Size; i++)
        {
            m[i, 6] = i % 2 == 0;
            m[6, i] = i % 2 == 0;
        }

        int alignRow = Size - 9;
        int alignCol = Size - 9;
        if (alignRow > 0 && alignCol > 0)
            PlaceAlignment(m, alignRow, alignCol);

        for (int r = 0; r < 9; r++)
        {
            if (r < 8) m[r, 8] = (FormatInfo[15 - (r + 1)] & 1) == 1;
            if (r < 8) m[8, Size - 1 - r] = (FormatInfo[15 - (r + 1)] & 1) == 1;
        }

        m[Size - 8, 8] = true;
    }

    private static readonly int[] FormatInfo =
    {
        1, 0, 1, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 1, 0
    };

    private static void PlaceFinder(bool[,] m, int startRow, int startCol)
    {
        for (int r = 0; r < 7; r++)
        {
            for (int c = 0; c < 7; c++)
            {
                bool isBlack = (r == 0 || r == 6 || c == 0 || c == 6) ||
                               (r >= 2 && r <= 4 && c >= 2 && c <= 4);
                m[startRow + r, startCol + c] = isBlack;
            }
        }

        for (int r = -1; r <= 7; r++)
        {
            if (startRow + r >= 0 && startRow + r < Size)
            {
                if (startCol - 1 >= 0) m[startRow + r, startCol - 1] = false;
                if (startCol + 7 < Size) m[startRow + r, startCol + 7] = false;
            }
        }
        for (int c = -1; c <= 7; c++)
        {
            if (startCol + c >= 0 && startCol + c < Size)
            {
                if (startRow - 1 >= 0) m[startRow - 1, startCol + c] = false;
                if (startRow + 7 < Size) m[startRow + 7, startCol + c] = false;
            }
        }
    }

    private static void PlaceAlignment(bool[,] m, int row, int col)
    {
        for (int r = -2; r <= 2; r++)
        {
            for (int c = -2; c <= 2; c++)
            {
                bool isBlack = r == -2 || r == 2 || c == -2 || c == 2 || (r == 0 && c == 0);
                m[row + r, col + c] = isBlack;
            }
        }
    }

    private static void PlaceDataBits(bool[,] modules, byte[] data)
    {
        var bits = new List<int>();
        foreach (byte b in data)
        {
            for (int i = 7; i >= 0; i--)
                bits.Add((b >> i) & 1);
        }

        int bitIndex = 0;
        bool upward = true;

        for (int col = Size - 1; col > 0; col -= 2)
        {
            if (col == 6) col--;

            if (upward)
            {
                for (int row = Size - 1; row >= 0; row--)
                {
                    PlaceTwoBits(modules, row, col, col - 1, bits, ref bitIndex);
                }
            }
            else
            {
                for (int row = 0; row < Size; row++)
                {
                    PlaceTwoBits(modules, row, col, col - 1, bits, ref bitIndex);
                }
            }

            upward = !upward;
        }
    }

    private static void PlaceTwoBits(bool[,] m, int row, int c1, int c2, List<int> bits, ref int idx)
    {
        if (IsDataArea(row, c1) && idx < bits.Count)
            m[row, c1] = bits[idx++] == 1;
        if (IsDataArea(row, c2) && idx < bits.Count)
            m[row, c2] = bits[idx++] == 1;
    }

    private static bool IsDataArea(int r, int c)
    {
        if (r < 0 || r >= Size || c < 0 || c >= Size) return false;

        if (r <= 8 && c <= 8) return false;
        if (r <= 8 && c >= Size - 8) return false;
        if (r >= Size - 8 && c <= 8) return false;

        if (r == 6 || c == 6) return false;

        int align = Size - 9;
        if (Math.Abs(r - align) <= 2 && Math.Abs(c - align) <= 2) return false;

        return true;
    }

    private static void ApplyMask(bool[,] m, int maskPattern)
    {
        for (int r = 0; r < Size; r++)
        {
            for (int c = 0; c < Size; c++)
            {
                if (!IsDataArea(r, c)) continue;

                bool condition = maskPattern switch
                {
                    0 => (r + c) % 2 == 0,
                    1 => r % 2 == 0,
                    2 => c % 3 == 0,
                    3 => (r + c) % 3 == 0,
                    4 => ((r / 2) + (c / 3)) % 2 == 0,
                    5 => (r * c) % 2 + (r * c) % 3 == 0,
                    6 => ((r * c) % 2 + (r * c) % 3) % 2 == 0,
                    7 => ((r + c) % 2 + (r * c) % 3) % 2 == 0,
                    _ => false
                };

                if (condition)
                    m[r, c] = !m[r, c];
            }
        }
    }

    private static byte[] GenerateEcc(byte[] data, int eccCount)
    {
        var generator = GetGeneratorPolynomial(eccCount);
        var result = new byte[data.Length + eccCount];
        Array.Copy(data, result, data.Length);

        for (int i = 0; i < data.Length; i++)
        {
            byte factor = result[i];
            if (factor == 0) continue;

            for (int j = 0; j < generator.Length; j++)
            {
                int pos = i + j;
                result[pos] ^= GfMul(generator[j], factor);
            }
        }

        return result.Skip(data.Length).Take(eccCount).ToArray();
    }

    private static byte[] GetGeneratorPolynomial(int degree)
    {
        if (!GeneratorCache.TryGetValue(degree, out var poly))
        {
            poly = new byte[] { 1 };
            for (int i = 0; i < degree; i++)
            {
                byte[] term = { 1, GfExp(i) };
                poly = PolyMul(poly, term);
            }
            GeneratorCache[degree] = poly;
        }
        return poly;
    }

    private static readonly Dictionary<int, byte[]> GeneratorCache = new();

    private static byte[] PolyMul(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length - 1];
        for (int i = 0; i < a.Length; i++)
            for (int j = 0; j < b.Length; j++)
                result[i + j] ^= GfMul(a[i], b[j]);
        return result;
    }

    private static byte GfMul(byte a, byte b)
    {
        if (a == 0 || b == 0) return 0;
        int sum = GfLog[a] + GfLog[b];
        if (sum >= 255) sum -= 255;
        return ExpTable[sum];
    }

    private static byte GfExp(int exp)
    {
        while (exp < 0) exp += 255;
        while (exp >= 255) exp -= 255;
        return ExpTable[exp];
    }

    private static readonly byte[] ExpTable = BuildExpTable();
    private static readonly byte[] GfLog = BuildLogTable();

    private static byte[] BuildExpTable()
    {
        var table = new byte[255];
        int val = 1;
        for (int i = 0; i < 255; i++)
        {
            table[i] = (byte)val;
            val <<= 1;
            if (val >= 256)
                val ^= 0x11D;
        }
        return table;
    }

    private static byte[] BuildLogTable()
    {
        var table = new byte[256];
        for (int i = 0; i < 255; i++)
            table[ExpTable[i]] = (byte)i;
        table[0] = 0;
        return table;
    }

    private static string RenderSvg(bool[,] modules, int pixelSize)
    {
        var sb = new StringBuilder();
        int totalSize = Size * pixelSize;
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {totalSize} {totalSize}\" width=\"{totalSize}\" height=\"{totalSize}\">");
        sb.Append($"<rect width=\"{totalSize}\" height=\"{totalSize}\" fill=\"#ffffff\"/>");

        for (int r = 0; r < Size; r++)
        {
            for (int c = 0; c < Size; c++)
            {
                if (modules[r, c])
                {
                    int x = c * pixelSize;
                    int y = r * pixelSize;
                    sb.Append($"<rect x=\"{x}\" y=\"{y}\" width=\"{pixelSize}\" height=\"{pixelSize}\" fill=\"#131b18\"/>");
                }
            }
        }

        sb.Append("</svg>");
        return sb.ToString();
    }
}
