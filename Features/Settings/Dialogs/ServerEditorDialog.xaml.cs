using System.Windows;
using Wpf.Ui.Controls;

namespace NtfyDesktop.Features.Settings.Dialogs;

public partial class ServerEditorDialog : FluentWindow
{
    private readonly ServerEditorViewModel _vm;
    private bool _suspendSecretSync;

    public ServerConfig? Result { get; private set; }

    public ServerEditorDialog(ServerConfig? existing)
    {
        InitializeComponent();
        _vm = ServerEditorViewModel.FromServer(existing);
        DataContext = _vm;
        Title = existing is null ? "Add server" : "Edit server";

        // PasswordBox.Password isn't bindable; pump both secret fields in manually.
        _suspendSecretSync = true;
        TokenBox.Password = _vm.AccessToken;
        PasswordInput.Password = _vm.Password;
        _suspendSecretSync = false;
    }

    private void OnTokenChanged(object sender, RoutedEventArgs e)
    {
        if (_suspendSecretSync) return;
        _vm.AccessToken = TokenBox.Password;
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_suspendSecretSync) return;
        _vm.Password = PasswordInput.Password;
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (_vm.HasUrlError)
        {
            System.Windows.MessageBox.Show(this, _vm.UrlError, "Validation",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        Result = _vm.ToServer();
        DialogResult = true;
        Close();
    }
}
