using FluentBrowser.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.Web.WebView2.Core;
using System;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;
using Windows.System;
using WinUI3Localizer;

namespace FluentBrowser.Pages;

public sealed partial class CantOpenPage : Page
{
    private CoreWebView2WebErrorStatus _errorStatus;
    private string _url;
    private CoreWebView2Certificate? _certificate;
    private bool _waitingForInternet;

    public event EventHandler? ContinueRequested;

    public CantOpenPage(
        CoreWebView2WebErrorStatus errorStatus,
        string url,
        CoreWebView2Certificate? certificate = null)
    {
        InitializeComponent();

        _errorStatus = errorStatus;
        _url = url;
        _certificate = certificate;

        ContinueButton.Visibility = Visibility.Visible;

        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;

        UpdateTexts();

        Localizer.Get().LanguageChanged += OnLanguageChanged;
    }

    public void SetCertificate(
        CoreWebView2WebErrorStatus errorStatus,
        string url,
        CoreWebView2Certificate certificate)
    {
        _errorStatus = errorStatus;
        _url = url;
        _certificate = certificate;

        ContinueButton.Visibility = Visibility.Visible;

        UpdateTexts();
    }

    private void ContinueToPage_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!IsInternetAvailable())
        {
            _waitingForInternet = true;
            ContinueButton.IsEnabled = false;
            UpdateContinueButtonContent();

            if (IsInternetAvailable())
                ContinueToPage();

            return;
        }

        ContinueToPage();
    }

    private void ContinueToPage()
    {
        _waitingForInternet = false;
        ContinueButton.IsEnabled = false;
        UpdateContinueButtonContent();

        ContinueRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnNetworkStatusChanged(object sender)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            UpdateTexts();

            await Task.Delay(1000); // idk

            if (_waitingForInternet && IsInternetAvailable())
                ContinueToPage();
        });
    }

    private static bool IsInternetAvailable()
    {
        var profile = NetworkInformation.GetInternetConnectionProfile();

        return profile?.GetNetworkConnectivityLevel()
            == NetworkConnectivityLevel.InternetAccess;
    }

    private void OnLanguageChanged(
        object? sender,
        LanguageChangedEventArgs e)
    {
        UpdateTexts();
    }

    private void UpdateTexts()
    {
        bool internetAvailable = IsInternetAvailable();

        ErrorTitle.Text = internetAvailable
            ? LocalizerExtensions.GetLocalizedString("CantOpenPageTitle")
            : LocalizerExtensions.GetLocalizedString("InternetNotAvailableTitle");

        UpdateContinueButtonContent();

        if (!internetAvailable)
        {
            SetErrorMessageWithHyperlink(
                LocalizerExtensions.GetLocalizedString(
                    "InternetNotAvailableMessage"),
                LocalizerExtensions.GetLocalizedString(
                    "InternetHelpLink"));
        }
        else
        {
            string messageKey =
                GetErrorMessageKey(_errorStatus);

            string message =
                LocalizerExtensions.GetLocalizedString(messageKey);

            string expiredTime = string.Empty;

            if (_errorStatus == CoreWebView2WebErrorStatus.CertificateExpired &&
                _certificate is not null)
            {
                DateTime expirationDate =
                    DateTimeOffset.FromUnixTimeSeconds(
                        (long)_certificate.ValidTo)
                    .LocalDateTime;

                int daysExpired =
                    Math.Max(
                        0,
                        (DateTime.Now.Date - expirationDate.Date).Days);

                expiredTime =
                    Helpers.FormatDaysAgo(daysExpired);
            }

            string currentDate =
                DateTime.Now.ToString(
                    "D",
                    Helpers.GetCurrentCulture());

            ErrorMessageText.Text =
                string.Format(
                    message,
                    _url,
                    expiredTime,
                    currentDate);
        }

        ErrorStatusText.Text = $"{_errorStatus}";
    }

    private void UpdateContinueButtonContent()
    {
        ContinueButton.Content =
            LocalizerExtensions.GetLocalizedString(
                _waitingForInternet
                    ? "WaitingForInternet"
                    : "ContinueToPageButtonContent");
    }

    private void SetErrorMessageWithHyperlink(
        string message,
        string linkText)
    {
        ErrorMessageText.Inlines.Clear();

        int placeholderIndex =
            message.IndexOf("{0}", StringComparison.Ordinal);

        if (placeholderIndex < 0)
        {
            ErrorMessageText.Inlines.Add(
                new Run { Text = message });

            return;
        }

        ErrorMessageText.Inlines.Add(
            new Run
            {
                Text = message[..placeholderIndex]
            });

        var hyperlink = new Hyperlink
        {
            UnderlineStyle = UnderlineStyle.None
        };

        hyperlink.Inlines.Add(
            new Run
            {
                Text = linkText
            });

        hyperlink.Click += GetHelp_Click;

        ErrorMessageText.Inlines.Add(hyperlink);

        ErrorMessageText.Inlines.Add(
            new Run
            {
                Text = message[(placeholderIndex + 3)..]
            });
    }

    private async void GetHelp_Click(
        Hyperlink sender,
        HyperlinkClickEventArgs args)
    {
        await Launcher.LaunchUriAsync(
            new Uri("ms-contact-support:"));
    }

    private static string GetErrorMessageKey(
        CoreWebView2WebErrorStatus errorStatus)
    {
        return errorStatus switch
        {
            CoreWebView2WebErrorStatus.HostNameNotResolved =>
                "CantOpenPageHostNameNotResolved",

            CoreWebView2WebErrorStatus.CannotConnect =>
                "CantOpenPageCannotConnect",

            CoreWebView2WebErrorStatus.ServerUnreachable =>
                "CantOpenPageServerUnreachable",

            CoreWebView2WebErrorStatus.Timeout =>
                "CantOpenPageTimeout",

            CoreWebView2WebErrorStatus.Disconnected =>
                "CantOpenPageDisconnected",

            CoreWebView2WebErrorStatus.ConnectionReset =>
                "CantOpenPageConnectionReset",

            CoreWebView2WebErrorStatus.ConnectionAborted =>
                "CantOpenPageConnectionAborted",

            CoreWebView2WebErrorStatus.CertificateExpired =>
                "CantOpenPageCertificateExpired",

            CoreWebView2WebErrorStatus.CertificateIsInvalid =>
                "CantOpenPageCertificateInvalid",

            _ =>
                "CantOpenPageUnknownError"
        };
    }

    protected override void OnNavigatedFrom(
        Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        Localizer.Get().LanguageChanged -= OnLanguageChanged;
        NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;

        base.OnNavigatedFrom(e);
    }
}