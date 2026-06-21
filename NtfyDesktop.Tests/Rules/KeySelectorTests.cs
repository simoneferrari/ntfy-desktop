using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Tests.Rules;

public class KeySelectorTests
{
    private static NtfyMessage Msg(string? title = null, string? body = null) =>
        new() { Id = "m1", Topic = "t", Title = title, Message = body };

    [Fact]
    public void Extract_UsesNamedGroup_FromBody()
    {
        var sel = new KeySelector { From = KeyField.Body, Regex = @"Event ID: (?<key>\d+)" };
        Assert.Equal("4242", sel.Extract(Msg(body: "Disk full. Event ID: 4242 on host db1")));
    }

    [Fact]
    public void Extract_FallsBackToGroupOne_WhenNoNamedGroup()
    {
        var sel = new KeySelector { From = KeyField.Title, Regex = @"#(\d+)" };
        Assert.Equal("7", sel.Extract(Msg(title: "PROBLEM #7")));
    }

    [Fact]
    public void Extract_ReturnsNull_WhenNoMatch()
    {
        var sel = new KeySelector { From = KeyField.Body, Regex = @"Event ID: (?<key>\d+)" };
        Assert.Null(sel.Extract(Msg(body: "nothing here")));
    }

    [Fact]
    public void Extract_ReturnsNull_WhenSourceFieldNull()
    {
        var sel = new KeySelector { From = KeyField.Body, Regex = @"(?<key>\d+)" };
        Assert.Null(sel.Extract(Msg(body: null)));
    }
}
