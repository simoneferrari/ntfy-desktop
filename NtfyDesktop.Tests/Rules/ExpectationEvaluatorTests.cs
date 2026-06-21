using NtfyDesktop.Features.Rules;

namespace NtfyDesktop.Tests.Rules;

public class ExpectationEvaluatorTests
{
    private static readonly DateTimeOffset Base = DateTimeOffset.FromUnixTimeSeconds(1_000_000);

    [Fact]
    public void NotOverdue_WithinWindow()
    {
        // last seen at Base; every 1h, grace 10m; now = Base + 50m → not overdue
        Assert.False(ExpectationEvaluator.IsOverdue(
            Base.ToUnixTimeSeconds(), TimeSpan.FromHours(1), TimeSpan.FromMinutes(10),
            Base.AddMinutes(50)));
    }

    [Fact]
    public void Overdue_PastWindowPlusGrace()
    {
        Assert.True(ExpectationEvaluator.IsOverdue(
            Base.ToUnixTimeSeconds(), TimeSpan.FromHours(1), TimeSpan.FromMinutes(10),
            Base.AddMinutes(71)));
    }

    [Fact]
    public void NotOverdue_ExactlyAtBoundary()
    {
        Assert.False(ExpectationEvaluator.IsOverdue(
            Base.ToUnixTimeSeconds(), TimeSpan.FromHours(1), TimeSpan.Zero, Base.AddHours(1)));
    }
}
