using System.Text;

namespace NtfyDesktop.Features.Settings;

/// <summary>How a server authenticates. ntfy accepts either an access token or HTTP Basic
/// (username + password); a server uses exactly one. Token is the default so older
/// settings.json (which has no AuthMethod field) deserialise as token servers.</summary>
public enum ServerAuthMethod
{
    Token = 0,
    Password = 1,
}

/// <summary>
/// A configured ntfy server. Each server carries its own credentials (DPAPI-encrypted
/// at rest) — an access token or a username/password pair — so topics on different
/// servers authenticate independently.
/// </summary>
public sealed class ServerConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Friendly label shown in the UI (e.g. "Home", "ntfy.sh").</summary>
    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = "https://ntfy.sh";

    /// <summary>Which credential the server authenticates with. Defaults to token.</summary>
    public ServerAuthMethod AuthMethod { get; set; } = ServerAuthMethod.Token;

    public string EncryptedAccessToken { get; set; } = string.Empty;

    /// <summary>Plaintext username for HTTP Basic auth (not secret, so not encrypted).</summary>
    public string Username { get; set; } = string.Empty;

    public string EncryptedPassword { get; set; } = string.Empty;

    public string GetAccessToken() => TokenProtector.Decrypt(EncryptedAccessToken);

    public void SetAccessToken(string token) => EncryptedAccessToken = TokenProtector.Encrypt(token);

    public string GetPassword() => TokenProtector.Decrypt(EncryptedPassword);

    public void SetPassword(string password) => EncryptedPassword = TokenProtector.Encrypt(password);

    /// <summary>
    /// The full HTTP <c>Authorization</c> header value for this server — <c>"Bearer &lt;token&gt;"</c>
    /// or <c>"Basic &lt;base64(user:pass)&gt;"</c> — or null when no usable credentials are configured.
    /// ntfy accepts both; password auth maps to standard HTTP Basic. The single source of truth for
    /// authenticating any request to this server (subscribe socket, attachment download).
    /// </summary>
    public string? GetAuthorizationHeader()
    {
        if (AuthMethod == ServerAuthMethod.Password)
        {
            if (string.IsNullOrEmpty(Username)) return null;
            var raw = Encoding.UTF8.GetBytes($"{Username}:{GetPassword()}");
            return $"Basic {Convert.ToBase64String(raw)}";
        }

        var token = GetAccessToken();
        return string.IsNullOrEmpty(token) ? null : $"Bearer {token}";
    }

    /// <summary>Label for display: the friendly name if set, otherwise the host.</summary>
    public string DisplayLabel =>
        !string.IsNullOrWhiteSpace(Name)
            ? Name
            : (Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host : Url);
}
