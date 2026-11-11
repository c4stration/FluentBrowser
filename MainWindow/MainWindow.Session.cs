using MessagePack;
using MessagePack.Resolvers;
using FluentBrowser.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private readonly string _browserSessionFile =
        AppDataPaths.DataFile("BrowserSession.msgpack");

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
            CreatePageTab(typeof(StartPage)),
            true);
    }

    private bool OpenCustomStartupSites()
    {
        try
        {
            var urls = CustomSitesStorage.Load();

            if (urls.Count == 0)
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
                var entries = new List<(string Url, string? Title)>();
                int selectedEntry = -1;

                for (int i = 0; i < session.TabUrls.Count; i++)
                {
                    if (!Uri.TryCreate(
                            session.TabUrls[i],
                            UriKind.Absolute,
                            out Uri? uri) ||
                        (uri.Scheme != Uri.UriSchemeHttp &&
                         uri.Scheme != Uri.UriSchemeHttps))
                    {
                        continue;
                    }

                    if (i == session.SelectedTabIndex)
                        selectedEntry = entries.Count;

                    entries.Add((
                        uri.AbsoluteUri,
                        session.TabTitles?.ElementAtOrDefault(i)));
                }

                if (entries.Count == 0)
                    return false;

                if (selectedEntry < 0)
                {
                    selectedEntry = Math.Clamp(
                        session.SelectedTabIndex,
                        0,
                        entries.Count - 1);
                }

                TabViewItem selectedTab =
                    CreateNewTab(entries[selectedEntry].Url);

                MainTabView.TabItems.Add(selectedTab);
                MainTabView.SelectedItem = selectedTab;

                for (int i = 0; i < entries.Count; i++)
                {
                    if (i == selectedEntry)
                        continue;

                    TabViewItem tab = CreateDiscardedTab(
                        entries[i].Url,
                        entries[i].Title);

                    if (i < selectedEntry)
                        MainTabView.TabItems.Insert(i, tab);
                    else
                        MainTabView.TabItems.Add(tab);
                }

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
            var tabTitles = new List<string?>();
            int selectedTabIndex = -1;

            foreach (object item in MainTabView.TabItems)
            {
                if (item is not TabViewItem tab ||
                    tab.Tag is not BrowserTab browserTab)
                {
                    continue;
                }

                string? url = browserTab.LastKnownUrl;

                if (browserTab.WebView is WebView2 webView)
                {
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

                tabTitles.Add(
                    string.IsNullOrWhiteSpace(browserTab.Title.Text)
                        ? null
                        : browserTab.Title.Text);
            }

            if (tabUrls.Count == 0)
            {
                File.Delete(_browserSessionFile);
                return;
            }

            var session = new BrowserSession
            {
                TabUrls = tabUrls,
                TabTitles = tabTitles,
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
        ShutdownTabMemoryManagement();
        SaveBrowserSession();
    }
}