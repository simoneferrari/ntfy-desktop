namespace NtfyDesktop.Features.Rules.Model;

/// <summary>Mints short, stable, URL-safe rule ids for newly-created rules.</summary>
public static class RuleId
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567"; // base32

    public static string NewId()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        var chars = new char[8];
        for (var i = 0; i < 8; i++) chars[i] = Alphabet[bytes[i] & 31];
        return new string(chars);
    }
}
