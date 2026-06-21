using NtfyDesktop.Features.Rules;

namespace NtfyDesktop.Tests.Rules;

public class DurationTests
{
    [Theory]
    [InlineData("45s", 45)]
    [InlineData("90m", 90 * 60)]
    [InlineData("26h", 26 * 3600)]
    [InlineData("2d", 2 * 86400)]
    [InlineData("1H", 3600)]
    public void TryParse_Valid(string text, int expectedSeconds)
    {
        Assert.True(Duration.TryParse(text, out var value));
        Assert.Equal(expectedSeconds, (int)value.TotalSeconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("10")]
    [InlineData("10x")]
    [InlineData("-5h")]
    public void TryParse_Invalid(string? text)
    {
        Assert.False(Duration.TryParse(text, out _));
    }
}
