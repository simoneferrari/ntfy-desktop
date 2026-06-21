namespace NtfyDesktop.Features.Rules;

/// <summary>Pure timing check for expect rules — kept separate so it's unit-testable
/// with an injected "now" (no real waiting).</summary>
public static class ExpectationEvaluator
{
    public static bool IsOverdue(long lastSeenAtUnix, TimeSpan every, TimeSpan grace, DateTimeOffset now)
    {
        var deadline = DateTimeOffset.FromUnixTimeSeconds(lastSeenAtUnix) + every + grace;
        return now > deadline;
    }
}
