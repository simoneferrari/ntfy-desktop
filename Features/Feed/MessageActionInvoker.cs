using System.Net.Http;
using System.Text;
using System.Windows;
using NtfyDesktop.Domain;

namespace NtfyDesktop.Features.Feed;

/// <summary>
/// Performs an ntfy message action button. The one safe entry point for executing
/// actions, so the security rules live in a single place:
/// <list type="bullet">
///   <item><c>view</c> — opens the URL via the scheme allow-list (same as a link click).</item>
///   <item><c>copy</c> — copies the value to the clipboard.</item>
///   <item><c>http</c> — a publisher-controlled request, so it is <b>confirmed</b> (method + URL
///     shown) before firing, and the server bearer token is never attached (the URL is
///     arbitrary; injecting the token would leak it).</item>
///   <item><c>broadcast</c> / unknown — not actionable on Windows; no-op.</item>
/// </list>
/// </summary>
public sealed class MessageActionInvoker
{
    // Bound the wait on a publisher-specified endpoint; firing is best-effort.
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task InvokeAsync(NtfyAction action)
    {
        if (!action.IsSupported) return;

        if (action.IsView)
        {
            SafeUrl.Open(action.Url);
        }
        else if (action.IsCopy)
        {
            TryCopy(action.Value!);
        }
        else if (action.IsHttp)
        {
            await InvokeHttpAsync(action);
        }
    }

    private static void TryCopy(string value)
    {
        try { Clipboard.SetText(value); }
        catch { /* clipboard can be transiently locked by another app — non-fatal */ }
    }

    private static async Task InvokeHttpAsync(NtfyAction action)
    {
        // Show the request before firing — an http action is publisher-controlled and could
        // do anything. Only proceed on explicit confirmation.
        var confirm = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Run action?",
            Content = $"This will send a request:\n\n{action.EffectiveMethod} {action.Url}\n\nProceed?",
            PrimaryButtonText = "Send",
            CloseButtonText = "Cancel",
        };

        if (await confirm.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
            return;

        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(action.EffectiveMethod), action.Url);

            if (action.Body is { Length: > 0 })
                request.Content = new StringContent(action.Body, Encoding.UTF8);

            if (action.Headers is { Count: > 0 })
                foreach (var (name, value) in action.Headers)
                    // Content-Type and other content headers can't go on the request; route
                    // them to the content. Falls back silently if there's no content.
                    if (!request.Headers.TryAddWithoutValidation(name, value))
                        request.Content?.Headers.TryAddWithoutValidation(name, value);

            using var response = await _http.SendAsync(request);
        }
        catch (Exception ex)
        {
            var error = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Action failed",
                Content = $"The request could not be sent:\n\n{ex.Message}",
                CloseButtonText = "OK",
            };
            await error.ShowDialogAsync();
        }
    }
}
