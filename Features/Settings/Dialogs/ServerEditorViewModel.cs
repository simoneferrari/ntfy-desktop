using CommunityToolkit.Mvvm.ComponentModel;

namespace NtfyDesktop.Features.Settings.Dialogs;

// Backs the add/edit-server dialog. Operates on a copy so the user can cancel cleanly.
public sealed partial class ServerEditorViewModel : ObservableObject
{
    // Preserved across edit so the server keeps its identity (topics reference it).
    public Guid Id { get; private set; } = Guid.NewGuid();

    [ObservableProperty] private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UrlError))]
    [NotifyPropertyChangedFor(nameof(HasUrlError))]
    [NotifyPropertyChangedFor(nameof(HasInsecureTokenWarning))]
    private string _url = "https://ntfy.sh";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInsecureTokenWarning))]
    private string _accessToken = string.Empty;

    public string? UrlError
    {
        get
        {
            var s = Url?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(s))                     return "Server URL is required.";
            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return "Not a valid URL.";
            if (uri.Scheme != "http" && uri.Scheme != "https")    return "URL must use http or https.";
            return null;
        }
    }

    public bool HasUrlError => UrlError is not null;

    /// <summary>http:// + a token set: TopicConnection won't send the bearer header over cleartext.</summary>
    public bool HasInsecureTokenWarning =>
        !string.IsNullOrEmpty(AccessToken) &&
        (Url?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ?? false);

    public static ServerEditorViewModel FromServer(ServerConfig? source)
    {
        if (source is null) return new ServerEditorViewModel();

        return new ServerEditorViewModel
        {
            Id = source.Id,
            Name = source.Name,
            Url = source.Url,
            AccessToken = source.GetAccessToken(),
        };
    }

    public ServerConfig ToServer()
    {
        var server = new ServerConfig
        {
            Id = Id,
            Name = Name.Trim(),
            Url = Url.Trim(),
        };
        server.SetAccessToken(AccessToken);
        return server;
    }
}
