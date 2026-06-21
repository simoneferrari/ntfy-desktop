namespace NtfyDesktop.Features.Rules;

/// <summary>Parses compact durations like "26h", "90m", "2d", "45s" into a TimeSpan.</summary>
public static class Duration
{
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2) return false;

        var unit = char.ToLowerInvariant(text[^1]);
        if (!int.TryParse(text[..^1], out var n) || n < 0) return false;

        value = unit switch
        {
            's' => TimeSpan.FromSeconds(n),
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            _ => TimeSpan.Zero,
        };
        return value > TimeSpan.Zero || (n == 0 && "smhd".Contains(unit));
    }
}
