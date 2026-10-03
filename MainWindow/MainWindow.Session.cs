using MessagePack;
using MessagePack.Resolvers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Windows.Storage;
using Path = System.IO.Path;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private readonly string _browserSessionFile = Path.Combine(
        ApplicationData.Current.LocalFolder.Path,
        "BrowserSession.msgpack");

    private static readonly MessagePackSerializerOptions _browserSessionOptions =
        StandardResolver.Options.WithSecurity(
            MessagePackSecurity.UntrustedData);

    private void InitializeStartup()
    {
        string startupBehavior =
            _settings.Values["StartupBehavior"] as string
            ?? "ContinueSession";

        if (startupBehavior == "ContinueSession" &&
            RestoreBrowserSession())
        {
            return;
        }

        if (startupBehavior == "CustomSites" &&
            OpenCustomStartupSites())
        {
            return;
        }

        AddNewTab(
            CreateNewTab(
                CreateSearchEngineHomeUri().AbsoluteUri),
            true);
    }

    private bool OpenCustomStartupSites()
    {
        if (_settings.Values["CustomSites"] is not string json ||
            string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            var urls = System.Text.Json.JsonSerializer
                .Deserialize<List<string>>(json);

            if (urls is null || urls.Count == 0)
                return false;

            bool openedAny = false;

            foreach (string url in urls)
            {
                if (string.IsNullOrWhiteSpace(url) ||
                    !Uri.TryCreate(
                        url,
                        UriKind.Absolute,
                        out Uri? uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp &&
                     uri.Scheme != Uri.UriSchemeHttps))
                {
                    continue;
                }

                AddNewTab(
                    CreateNewTab(uri.AbsoluteUri),
                    !openedAny);

                openedAny = true;
            }

            return openedAny;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Failed to open custom startup sites: {ex}");
            return false;
        }
    }


    private bool RestoreBrowserSession()
    {
        if (!File.Exists(_browserSessionFile))
            return false;

        try
        {
            byte[] data =
                File.ReadAllBytes(_browserSessionFile);

            BrowserSession? session =
                MessagePackSerializer.Deserialize<BrowserSession>(
                    data,
                    _browserSessionOptions);

            if (session is null || session.TabUrls.Count == 0)
                return false;

            try
            {
                foreach (string url in session.TabUrls)
                {
                    if (!Uri.TryCreate(
                            url,
                            UriKind.Absolute,
                            out Uri? uri) ||
                        (uri.Scheme != Uri.UriSchemeHttp &&
                         uri.Scheme != Uri.UriSchemeHttps))
                    {
                        continue;
                    }

                    AddNewTab(
                        CreateNewTab(uri.AbsoluteUri),
                        false);
                }

                if (MainTabView.TabItems.Count == 0)
                    return false;

                int selectedIndex = Math.Clamp(
                    session.SelectedTabIndex,
                    0,
                    MainTabView.TabItems.Count - 1);

                MainTabView.SelectedIndex = selectedIndex;

                return true;
            }
            finally
            {
                // sussy baka
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Failed to restore browser session: {ex}");

            return false;
        }
    }

    private void SaveBrowserSession()
    {
        try
        {
            var tabUrls = new List<string>();
            int selectedTabIndex = -1;

            foreach (object item in MainTabView.TabItems)
            {
                if (item is not TabViewItem tab ||
                    tab.Tag is not BrowserTab browserTab ||
                    browserTab.WebView is not WebView2 webView)
                {
                    continue;
                }

                string? url = null;

                if (_navigationUris.TryGetValue(
                        webView,
                        out string? navigationUrl))
                {
                    url = navigationUrl;
                }
                else if (webView.Source is Uri source)
                {
                    url = source.AbsoluteUri;
                }

                if (string.IsNullOrWhiteSpace(url) ||
                    !Uri.TryCreate(
                        url,
                        UriKind.Absolute,
                        out Uri? uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp &&
                     uri.Scheme != Uri.UriSchemeHttps))
                {
                    continue;
                }

                if (ReferenceEquals(
                        tab,
                        MainTabView.SelectedItem))
                {
                    selectedTabIndex = tabUrls.Count;
                }

                tabUrls.Add(uri.AbsoluteUri);
            }

            if (tabUrls.Count == 0)
            {
                File.Delete(_browserSessionFile);
                return;
            }

            var session = new BrowserSession
            {
                TabUrls = tabUrls,
                SelectedTabIndex =
                    selectedTabIndex >= 0
                        ? selectedTabIndex
                        : 0
            };

            byte[] data =
                MessagePackSerializer.Serialize(
                    session,
                    _browserSessionOptions);

            string temporaryFile =
                _browserSessionFile + ".tmp";

            File.WriteAllBytes(
                temporaryFile,
                data);

            File.Move(
                temporaryFile,
                _browserSessionFile,
                true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Failed to save browser session: {ex}");
        }
    }

    private void MainWindow_Closed(
        object sender,
        WindowEventArgs args)
    {
        SaveBrowserSession();
    }
}