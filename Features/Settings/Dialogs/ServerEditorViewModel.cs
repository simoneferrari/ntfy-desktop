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
    [NotifyPropertyChangedFor(nameof(HasInsecureCredentialsWarning))]
    private string _url = "https://ntfy.sh";

    // Auth method as two bound bool flags so the dialog can use RadioButtons and toggle
    // which credential fields show. Exactly one is true; setting one clears the other.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInsecureCredentialsWarning))]
    private bool _useToken = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInsecureCredentialsWarning))]
    private bool _usePassword;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInsecureCredentialsWarning))]
    private string _accessToken = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInsecureCredentialsWarning))]
    private string _username = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInsecureCredentialsWarning))]
    private string _password = string.Empty;

    partial void OnUseTokenChanged(bool value)
    {
        if (value) UsePassword = false;
    }

    partial void OnUsePasswordChanged(bool value)
    {
        if (value) UseToken = false;
    }

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

    /// <summary>True when the user has entered credentials but the server is plain http://, so
    /// TopicConnection won't send the Authorization header (token or Basic) over cleartext.</summary>
    public bool HasInsecureCredentialsWarning
    {
        get
        {
            var hasCredentials = UsePassword
                ? !string.IsNullOrEmpty(Username)
                : !string.IsNullOrEmpty(AccessToken);
            return hasCredentials &&
                   (Url?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ?? false);
        }
    }

    public static ServerEditorViewModel FromServer(ServerConfig? source)
    {
        if (source is null) return new ServerEditorViewModel();

        var usePassword = source.AuthMethod == ServerAuthMethod.Password;
        return new ServerEditorViewModel
        {
            Id = source.Id,
            Name = source.Name,
            Url = source.Url,
            UseToken = !usePassword,
            UsePassword = usePassword,
            AccessToken = source.GetAccessToken(),
            Username = source.Username,
            Password = source.GetPassword(),
        };
    }

    public ServerConfig ToServer()
    {
        var server = new ServerConfig
        {
            Id = Id,
            Name = Name.Trim(),
            Url = Url.Trim(),
            AuthMethod = UsePassword ? ServerAuthMethod.Password : ServerAuthMethod.Token,
            Username = Username.Trim(),
        };
        server.SetAccessToken(AccessToken);
        server.SetPassword(Password);
        return server;
    }
}
