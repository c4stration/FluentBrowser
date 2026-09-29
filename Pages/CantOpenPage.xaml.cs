using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Globalization;
using WinUI3Localizer;

namespace FluentBrowser.Pages;

public sealed partial class CantOpenPage : Page
{
    private CoreWebView2WebErrorStatus _errorStatus;
    private string _url;
    private CoreWebView2Certificate? _certificate;

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

        if (_certificate is not null)
            ContinueButton.Visibility = Visibility.Visible;

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

        ContinueButton.Visibility =
            Visibility.Visible;

        UpdateTexts();
    }

    private void ContinueToPage_Click(
        object sender,
        RoutedEventArgs e)
    {
        ContinueRequested?.Invoke(this, EventArgs.Empty);

        // Prevents multiple clicks
        ContinueButton.IsEnabled = false;
    }

    private void OnLanguageChanged(
        object? sender,
        LanguageChangedEventArgs e)
    {
        UpdateTexts();
    }

    private void UpdateTexts()
    {
        string messageKey =
            GetErrorMessageKey(_errorStatus);

        ILocalizer localizer = Localizer.Get();

        string message =
            localizer.GetLocalizedString(messageKey);

        string expiredTime = string.Empty;

        if (_errorStatus == CoreWebView2WebErrorStatus.CertificateExpired &&
            _certificate is not null)
        {
            DateTime expirationDate =
                DateTimeOffset.FromUnixTimeSeconds(
                    (long)_certificate.ValidTo)
                .LocalDateTime;

            int daysExpired =
                Math.Max(0, (DateTime.Now.Date - expirationDate.Date).Days);

            expiredTime =
                $"{daysExpired:N0} {(daysExpired == 1 ? "day" : "days")}";
        }

        string currentDate =
            DateTime.Now.ToString("D", CultureInfo.CurrentCulture);

        ErrorMessageText.Text =
            string.Format(
                message,
                _url,
                expiredTime,
                currentDate);

        ErrorStatusText.Text =
            $"{_errorStatus}";
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
        Localizer.Get().LanguageChanged -=
            OnLanguageChanged;

        base.OnNavigatedFrom(e);
    }
}