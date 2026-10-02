using FluentBrowser.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Threading.Tasks;
using Windows.Foundation;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private void RegisterCertificateHandling(
        WebView2 webView,
        CoreWebView2 core)
    {
        core.ServerCertificateErrorDetected += (sender, args) =>
        {
            bool isMainFrameNavigation =
                _navigationUris.TryGetValue(
                    webView,
                    out string? navigationUrl) &&
                string.Equals(
                    args.RequestUri,
                    navigationUrl,
                    StringComparison.OrdinalIgnoreCase);

            if (!isMainFrameNavigation)
            {
                args.Action =
                    CoreWebView2ServerCertificateErrorAction.Cancel;

                return;
            }

            if (webView.Tag is not BrowserTab tab)
            {
                args.Action =
                    CoreWebView2ServerCertificateErrorAction.Cancel;

                return;
            }

            if (tab.ErrorPage is not CantOpenPage errorPage)
            {
                errorPage = new CantOpenPage(
                    args.ErrorStatus,
                    args.RequestUri,
                    args.ServerCertificate);

                tab.ErrorPage = errorPage;

                errorPage.ContinueRequested += (_, _) =>
                {
                    if (_certificateErrorDecisions.TryGetValue(
                            webView,
                            out TaskCompletionSource<bool>? decision))
                    {
                        decision.TrySetResult(true);
                    }
                };

                webView.Visibility =
                    Visibility.Collapsed;

                if (ReferenceEquals(
                        SelectedWebView,
                        webView))
                {
                    CurrentTabContent.Content =
                        errorPage;

                    RestoreDefaultToolbarBackground();
                }
            }
            else
            {
                errorPage.SetCertificate(
                    args.ErrorStatus,
                    args.RequestUri,
                    args.ServerCertificate);
            }

            Deferral deferral =
                args.GetDeferral();

            TaskCompletionSource<bool> decision =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            _certificateErrorDecisions[webView] =
                decision;

            _ = WaitForCertificateDecisionAsync(
                webView,
                args,
                decision,
                deferral);
        };
    }

    private async Task WaitForCertificateDecisionAsync(
        WebView2 webView,
        CoreWebView2ServerCertificateErrorDetectedEventArgs args,
        TaskCompletionSource<bool> decision,
        Deferral deferral)
    {
        try
        {
            bool continueToPage =
                await decision.Task;

            args.Action =
                continueToPage
                    ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow
                    : CoreWebView2ServerCertificateErrorAction.Cancel;
        }
        finally
        {
            _certificateErrorDecisions.Remove(webView);

            deferral.Complete();
        }
    }

    private void ConfigureScriptDialogHandling(
        CoreWebView2 core,
        WebView2 webView)
    {
        core.ScriptDialogOpening += async (_, args) =>
        {
            if (args.Kind !=
                    CoreWebView2ScriptDialogKind.Prompt &&
                args.Kind !=
                    CoreWebView2ScriptDialogKind.Alert)
            {
                return;
            }

            var deferral = args.GetDeferral();

            try
            {
                TextBox? textBox = null;

                if (args.Kind ==
                    CoreWebView2ScriptDialogKind.Prompt)
                {
                    textBox = new TextBox
                    {
                        Text = args.DefaultText,
                        TextWrapping = TextWrapping.Wrap
                    };

                    textBox.Loaded += (_, _) =>
                    {
                        textBox.Focus(
                            FocusState.Programmatic);

                        textBox.SelectAll();
                    };
                }

                var dialog = new ContentDialog
                {
                    Title =
                        args.Kind ==
                        CoreWebView2ScriptDialogKind.Prompt
                            ? args.Message
                            : null,

                    Content =
                        args.Kind ==
                        CoreWebView2ScriptDialogKind.Alert
                            ? new TextBlock
                            {
                                Text = args.Message,
                                TextWrapping =
                                    TextWrapping.Wrap
                            }
                            : textBox,

                    PrimaryButtonText = "OK",

                    DefaultButton =
                        ContentDialogButton.Primary,

                    XamlRoot = webView.XamlRoot,

                    Style =
                        Application.Current.Resources[
                            "DefaultContentDialogStyle"]
                        as Style,

                    PrimaryButtonStyle =
                        Application.Current.Resources[
                            "AccentButtonStyle"]
                        as Style,

                    CloseButtonStyle =
                        Application.Current.Resources[
                            "DefaultButtonStyle"]
                        as Style
                };

                if (args.Kind ==
                    CoreWebView2ScriptDialogKind.Prompt)
                {
                    dialog.CloseButtonText = "Cancel";
                }

                var result =
                    await dialog.ShowAsync();

                if (result ==
                    ContentDialogResult.Primary)
                {
                    if (args.Kind ==
                        CoreWebView2ScriptDialogKind.Prompt)
                    {
                        args.ResultText =
                            textBox!.Text;
                    }

                    args.Accept();
                }
            }
            finally
            {
                deferral.Complete();
            }
        };
    }
}
