using CommunityToolkit.WinUI.Controls;
using FluentBrowser.Controls;
using FluentBrowser.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Windows.Globalization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;
using WinUI3Localizer;

namespace FluentBrowser.Pages;

public sealed partial class SettingsPage : Page, IDisposable
{
    // WebView2 exposes chrome.tabs.create and chrome.tabs.update through its
    // browser process, but it has no tab strip for chrome.tabs.remove to close.
    // The shim turns remove into a navigation that MainWindow closes locally.
    private const string ExtensionTabsCompatibilityShim = """
        (() => {
          const tabs = chrome.tabs;
          if (!tabs) return;

          tabs.remove = (tabIds, callback) => {
            const ids = Array.isArray(tabIds) ? tabIds : [tabIds];
            const closeTabs = Promise.all(ids.map((tabId) =>
              tabs.update(tabId, {
                url: `https://tabs.fluentbrowser.invalid/close-tab/${encodeURIComponent(tabId)}`
              })
            )).then(() => undefined);

            if (typeof callback === "function") {
              closeTabs.then(() => callback(), () => callback());
              return undefined;
            }

            return closeTabs;
          };
        })();
        """;

    private string? _faviconCacheDescriptionTemplate;

    private readonly ApplicationDataContainer _settings;
    private MainWindow? _mainWindow;
    private bool _isDisposed;
    private bool _loadingSettings;
    public event EventHandler? FullWebAddressChanged;
    public event EventHandler? SearchSuggestionsChanged;
    public event EventHandler? SearchEngineChanged;
    private readonly SemaphoreSlim _extensionLoadLock = new(1, 1);
    private int _extensionLoadVersion;

    public SettingsPage()
    {
        InitializeComponent();

        if (App.MainWindow is MainWindow window)
        {
            _mainWindow = window;
            _mainWindow.FaviconCacheChanged += MainWindow_FaviconCacheChanged;
        }

        _settings = ApplicationData.Current.LocalSettings;

        _loadingSettings = true;

        AddExtensionButton.Click += AddExtensionButton_Click;

        LoadTheme();
        LoadWebTheme();
        LoadBackdropMaterial();
        LoadLanguage();
        LoadTextDirection(ResolveLanguage(
            _settings.Values["Language"] as string ?? "System"));
        LoadTabWidth();
        LoadNewTabPosition();
        LoadFullWebAddress();
        LoadSearchSuggestions();
        LoadSearchEngine();
        LoadThemeColorTint();
        LoadDownloadLocation();
        LoadAskEveryDownload();
        LoadAllShortcuts();

        _loadingSettings = false;

        UpdateSystemLanguageInfoBar();
        LoadFaviconCacheDescription();

        var available = Localizer.Get().GetAvailableLanguages();
        System.Diagnostics.Debug.WriteLine("Available languages: " + string.Join(", ", available));
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        if (_mainWindow is not null)
        {
            _mainWindow.FaviconCacheChanged -= MainWindow_FaviconCacheChanged;
            _mainWindow = null;
        }
    }

    private void LoadTabWidth()
    {
        string width =
            _settings.Values["TabWidth"] as string
            ?? "Equal";

        TabWidthComboBox.SelectedIndex = width switch
        {
            "Equal" => 0,
            "Title" => 1,
            "Compact" => 2,
            _ => 0
        };
    }

    private void LoadBackdropMaterial()
    {
        string material =
            _settings.Values["BackdropMaterial"] as string
            ?? "Mica";

        BackdropMaterialComboBox.SelectedIndex = material switch
        {
            "Mica" => 0,
            "MicaAlt" => 1,
            "Acrylic" => 2,
            _ => 0
        };
    }

    private void LoadTheme()
    {
        string theme =
            _settings.Values["Theme"] as string
            ?? "System";

        ThemeComboBox.SelectedIndex = theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0
        };

        App.ApplyTheme(theme);
    }

    private void ThemeComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loadingSettings || ThemeComboBox.SelectedIndex < 0)
            return;

        string theme = ThemeComboBox.SelectedIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "System"
        };

        _settings.Values["Theme"] = theme;

        App.ApplyTheme(theme);

        if (App.MainWindow is MainWindow window)
            window.ApplyWebTheme();
    }

    private void BackdropMaterialComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loadingSettings || BackdropMaterialComboBox.SelectedIndex < 0)
            return;

        string material = BackdropMaterialComboBox.SelectedIndex switch
        {
            0 => "Mica",
            1 => "MicaAlt",
            2 => "Acrylic",
            _ => "Mica"
        };

        _settings.Values["BackdropMaterial"] = material;

        App.ApplyBackdropMaterial(material);
    }

    private void TextDirComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loadingSettings || TextDirComboBox.SelectedIndex < 0)
            return;

        string direction = TextDirComboBox.SelectedIndex switch
        {
            0 => "LeftToRight",
            1 => "RightToLeft",
            _ => "RightToLeft"
        };

        _settings.Values["TextDirection"] = direction;

        string language =
            _settings.Values["Language"] as string
            ?? "System";

        string resolvedLanguage = ResolveLanguage(language);

        App.ApplyFlowDirection(
            resolvedLanguage,
            direction == "RightToLeft"
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight);
    }

    private void LoadLanguage()
    {
        string language =
            _settings.Values["Language"] as string
            ?? "System";

        LanguageComboBox.SelectedIndex = language switch
        {
            "ar" => 1,
            "zh-CN" => 2,
            "zh-TW" => 3,
            "en-US" => 4,
            "he" => 5,
            "ja-JP" => 6,
            "ko-KR" => 7,
            "ro-RO" => 8,
            "th" => 9,
            "en-UWU" => 10,
            "en-LOL" => 11,
            _ => 0
        };
    }

    private void LoadTextDirection(string language)
    {
        bool isRtlLanguage = App.IsRtlLanguage(language);

        TextDirectionCard.Visibility = isRtlLanguage
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!isRtlLanguage)
        {
            TextDirComboBox.SelectedIndex = 0;
            return;
        }

        string direction =
            _settings.Values["TextDirection"] as string
            ?? "RightToLeft";

        TextDirComboBox.SelectedIndex = direction switch
        {
            "LeftToRight" => 0,
            "RightToLeft" => 1,
            _ => 1
        };
    }

    private async void LanguageComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loadingSettings || LanguageComboBox.SelectedIndex < 0)
            return;

        string language = LanguageComboBox.SelectedIndex switch
        {
            1 => "ar",
            2 => "zh-CN",
            3 => "zh-TW",
            4 => "en-US",
            5 => "he",
            6 => "ja-JP",
            7 => "ko-KR",
            8 => "ro-RO",
            9 => "th",
            10 => "en-UWU",
            11 => "en-LOL",
            _ => "System"
        };

        _settings.Values["Language"] = language;

        string resolvedLanguage = ResolveLanguage(language);

        await Localizer.Get().SetLanguage(resolvedLanguage);

        LoadFaviconCacheDescription();
        LoadTextDirection(resolvedLanguage);

        if (App.IsRtlLanguage(resolvedLanguage))
        {
            string direction =
                _settings.Values["TextDirection"] as string
                ?? "RightToLeft";

            App.ApplyFlowDirection(
                resolvedLanguage,
                direction == "RightToLeft"
                    ? FlowDirection.RightToLeft
                    : FlowDirection.LeftToRight);
        }
        else
        {
            App.ApplyFlowDirection(resolvedLanguage);
        }

        UpdateSystemLanguageInfoBar();
    }

    private void TabWidthComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loadingSettings || TabWidthComboBox.SelectedIndex < 0)
            return;

        string width = TabWidthComboBox.SelectedIndex switch
        {
            0 => "Equal",
            1 => "Title",
            2 => "Compact",
            _ => "Equal"
        };

        _settings.Values["TabWidth"] = width;

        if (App.MainWindow is MainWindow window)
            window.ApplyTabWidth(width);
    }

    private static string ResolveLanguage(string language)
    {
        if (language != "System")
            return language;

        var available = Localizer.Get().GetAvailableLanguages()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 1. Prefer an exact match from the system preferred languages
        foreach (string preferred in ApplicationLanguages.Languages)
        {
            if (available.Contains(preferred))
                return preferred;
        }

        // 2. Prefer a full match ignoring case (already covered above, but kept for clarity)
        // 3. Fallback: only accept the canonical English variant for "en"
        //    (or the first real language that matches the base code)
        foreach (string preferred in ApplicationLanguages.Languages)
        {
            string preferredBase = preferred.Split('-')[0];

            // Explicitly prefer the normal English locale over joke locales
            if (preferredBase.Equals("en", StringComparison.OrdinalIgnoreCase)
                && available.Contains("en-US"))
            {
                return "en-US";
            }

            // For other languages, take the first available match on the base code
            // that is NOT a joke language
            foreach (string avail in Localizer.Get().GetAvailableLanguages())
            {
                if (avail.StartsWith("en-UWU", StringComparison.OrdinalIgnoreCase) ||
                    avail.StartsWith("en-LOL", StringComparison.OrdinalIgnoreCase))
                    continue;

                string availBase = avail.Split('-')[0];
                if (preferredBase.Equals(availBase, StringComparison.OrdinalIgnoreCase))
                    return avail;
            }
        }

        // Final fallback
        return available.Contains("en-US") ? "en-US" : "en-US";
    }

    private bool IsSystemLanguageAvailable()
    {
        var available = Localizer.Get()
            .GetAvailableLanguages()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string preferred in ApplicationLanguages.Languages)
        {
            // Exact locale, e.g. en-US
            if (available.Contains(preferred))
                return true;

            // Base language, e.g. fr from fr-FR
            string preferredBase = preferred.Split('-')[0];

            foreach (string language in available)
            {
                // Don't consider the joke English locales when checking
                // whether normal system English is supported
                if (language.Equals("en-UWU", StringComparison.OrdinalIgnoreCase) ||
                    language.Equals("en-LOL", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string languageBase = language.Split('-')[0];

                if (preferredBase.Equals(languageBase, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private void UpdateSystemLanguageInfoBar()
    {
        string language =
            _settings.Values["Language"] as string
            ?? "System";

        SystemLanguageUnavailableInfoBar.IsOpen =
            language == "System" && !IsSystemLanguageAvailable();
    }

    private void LoadFullWebAddress()
    {
        FullWebAddressToggle.IsOn =
            _settings.Values["FullWebAddress"] as bool? ?? false;
    }

    private void FullWebAddressToggle_Toggled(
        object sender,
        RoutedEventArgs e)
    {
        if (_loadingSettings)
            return;

        _settings.Values["FullWebAddress"] =
            FullWebAddressToggle.IsOn;

        FullWebAddressChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LoadSearchSuggestions()
    {
        SearchSuggestionsToggle.IsOn =
            _settings.Values["SearchSuggestions"] as bool? ?? true;
    }

    private void SearchSuggestionsToggle_Toggled(
    object sender,
    RoutedEventArgs e)
    {
        if (_loadingSettings)
            return;

        bool enabled = SearchSuggestionsToggle.IsOn;

        _settings.Values["SearchSuggestions"] = enabled;

        SearchSuggestionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<string?> GetExtensionStringAsync(
        string extensionPath,
        JsonElement manifest,
        string propertyName)
    {
        if (!manifest.TryGetProperty(
                propertyName,
                out JsonElement value))
        {
            return null;
        }

        string? text = value.GetString();

        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (!text.StartsWith("__MSG_") ||
            !text.EndsWith("__"))
        {
            return text;
        }

        string messageName = text[6..^2];

        string? defaultLocale = null;

        if (manifest.TryGetProperty(
                "default_locale",
                out JsonElement localeElement))
        {
            defaultLocale = localeElement.GetString();
        }

        if (string.IsNullOrWhiteSpace(defaultLocale))
            return null;

        string? message = await ReadExtensionMessageAsync(
            extensionPath,
            messageName,
            defaultLocale);

        return message ?? text;
    }

    private async Task<string?> ReadExtensionMessageAsync(
        string extensionPath,
        string messageName,
        string defaultLocale)
    {
        var locales = new List<string>();

        foreach (string language in ApplicationLanguages.Languages)
        {
            string full = language;
            string baseLanguage = language.Split('-')[0];

            if (!locales.Contains(full, StringComparer.OrdinalIgnoreCase))
                locales.Add(full);

            if (!locales.Contains(baseLanguage, StringComparer.OrdinalIgnoreCase))
                locales.Add(baseLanguage);
        }

        if (!locales.Contains(
                defaultLocale,
                StringComparer.OrdinalIgnoreCase))
        {
            locales.Add(defaultLocale);
        }

        foreach (string locale in locales)
        {
            string messagesPath = Path.Combine(
                extensionPath,
                "_locales",
                locale,
                "messages.json");

            if (!File.Exists(messagesPath))
                continue;

            try
            {
                string json =
                    await File.ReadAllTextAsync(messagesPath);

                using JsonDocument document =
                    JsonDocument.Parse(json);

                if (!document.RootElement.TryGetProperty(
                        messageName,
                        out JsonElement message))
                {
                    continue;
                }

                if (message.TryGetProperty(
                        "message",
                        out JsonElement messageValue))
                {
                    return messageValue.GetString();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to read extension locale '{locale}': {ex}");
            }
        }

        return null;
    }

    private static string? GetBestExtensionIconPath(string extensionPath, JsonElement manifest)
    {
        // Preferred sizes for a SettingsCard header icon
        int[] preferredSizes = { 32, 48, 16, 64, 128, 24, 20 };

        // 1. Top-level "icons"
        if (TryGetIconFromObject(manifest, "icons", preferredSizes, extensionPath, out string? path))
            return path;

        // 2. action / browser_action / page_action .default_icon
        foreach (string key in new[] { "action", "browser_action", "page_action" })
        {
            if (!manifest.TryGetProperty(key, out JsonElement action))
                continue;

            // default_icon can be a string or an object
            if (action.TryGetProperty("default_icon", out JsonElement defaultIcon))
            {
                if (defaultIcon.ValueKind == JsonValueKind.String)
                {
                    string? rel = defaultIcon.GetString();
                    if (!string.IsNullOrWhiteSpace(rel))
                    {
                        string full = Path.Combine(extensionPath, rel);
                        if (File.Exists(full))
                            return full;
                    }
                }
                else if (TryGetIconFromObject(action, "default_icon", preferredSizes, extensionPath, out path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static bool TryGetIconFromObject(
        JsonElement parent,
        string propertyName,
        int[] preferredSizes,
        string extensionPath,
        out string? fullPath)
    {
        fullPath = null;

        if (!parent.TryGetProperty(propertyName, out JsonElement icons) ||
            icons.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // Try preferred sizes first
        foreach (int size in preferredSizes)
        {
            if (icons.TryGetProperty(size.ToString(), out JsonElement iconEl))
            {
                string? rel = iconEl.GetString();
                if (string.IsNullOrWhiteSpace(rel))
                    continue;

                string candidate = Path.Combine(extensionPath, rel);
                if (File.Exists(candidate))
                {
                    fullPath = candidate;
                    return true;
                }
            }
        }

        // Fallback: any icon that exists
        foreach (JsonProperty prop in icons.EnumerateObject())
        {
            string? rel = prop.Value.GetString();
            if (string.IsNullOrWhiteSpace(rel))
                continue;

            string candidate = Path.Combine(extensionPath, rel);
            if (File.Exists(candidate))
            {
                fullPath = candidate;
                return true;
            }
        }

        return false;
    }

    public async Task LoadExtensionsAsync(CoreWebView2Profile? profile)
    {
        if (profile is null)
            return;

        int loadVersion = Interlocked.Increment(ref _extensionLoadVersion);

        await _extensionLoadLock.WaitAsync();
        try
        {
            var extensions = await profile.GetBrowserExtensionsAsync();
            var cards = new List<SettingsCard>();

            foreach (var extension in extensions)
            {
                string? description = null;
                string key = $"ExtensionPath_{extension.Id}";
                string? iconPath = null;
                IconElement headerIcon;

                if (_settings.Values[key] is string extensionPath &&
                    Directory.Exists(extensionPath))
                {
                    string manifestPath = Path.Combine(extensionPath, "manifest.json");
                    if (File.Exists(manifestPath))
                    {
                        try
                        {
                            string json = await File.ReadAllTextAsync(manifestPath);
                            using JsonDocument document = JsonDocument.Parse(json);
                            description = await GetExtensionStringAsync(
                                extensionPath,
                                document.RootElement,
                                "description");
                            iconPath = GetBestExtensionIconPath(extensionPath, document.RootElement);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Failed to read extension manifest: {ex}");
                        }
                    }
                }

                if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
                {
                    headerIcon = new BitmapIcon
                    {
                        UriSource = new Uri(iconPath),
                        ShowAsMonochrome = false
                    };
                }
                else
                {
                    headerIcon = new FontIcon { Glyph = "\uF158" };
                }

                var toggle = new ToggleSwitch
                {
                    IsOn = extension.IsEnabled,
                    Tag = extension
                };
                toggle.Toggled += ExtensionToggle_Toggled;

                var card = new SettingsCard
                {
                    Header = extension.Name,
                    Description = description ?? extension.Id,
                    Content = toggle,
                    HeaderIcon = headerIcon
                };

                var removeItem = new MenuFlyoutItem
                {
                    Text = "Remove",
                    Tag = extension,
                    Icon = new FontIcon { Glyph = "\uE738" }
                };
                removeItem.Click += ExtensionRemove_Click;

                card.ContextFlyout = new MenuFlyout
                {
                    Items = { removeItem }
                };

                cards.Add(card);
            }

            // A newer refresh has already started.
            if (loadVersion != _extensionLoadVersion)
                return;

            // Assign a brand-new list so the Items DP change notification fires
            // and the internal ItemsRepeater actually refreshes.
            ExtensionsExpander.Items = cards.Cast<object>().ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load extensions: {ex}");
        }
        finally
        {
            _extensionLoadLock.Release();
        }
    }

    private async void ExtensionToggle_Toggled(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle ||
            toggle.Tag is not CoreWebView2BrowserExtension extension)
        {
            return;
        }

        await extension.EnableAsync(toggle.IsOn);
    }

    private async void ExtensionRemove_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item ||
            item.Tag is not CoreWebView2BrowserExtension extension ||
            App.MainWindow is not MainWindow window)
        {
            return;
        }

        CoreWebView2Profile? profile =
            window.GetBrowserProfile();

        if (profile is null)
            return;

        string extensionId = extension.Id;

        try
        {
            await extension.RemoveAsync();

            _settings.Values.Remove(
                $"ExtensionPath_{extensionId}");

            await LoadExtensionsAsync(profile);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Failed to remove extension '{extensionId}': {ex}");
        }
    }

    private async void AddExtensionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        // Prevent double clicks
        button.IsEnabled = false;

        try
        {
            if (App.MainWindow is not MainWindow window)
                return;

            CoreWebView2Profile? profile = window.GetBrowserProfile();
            if (profile is null)
                return;

            profile.AreWebViewScriptApisEnabledForServiceWorkers = true;

            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, window.GetWindowHandle());

            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder is null)
                return;

            try
            {
                string compatibleExtensionPath =
                    CreateCompatibleExtensionCopy(folder.Path);

                CoreWebView2BrowserExtension extension =
                    await profile.AddBrowserExtensionAsync(
                        compatibleExtensionPath);

                _settings.Values[$"ExtensionPath_{extension.Id}"] =
                    compatibleExtensionPath;
                await LoadExtensionsAsync(profile);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to install extension: {ex}");
            }
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private static string CreateCompatibleExtensionCopy(string sourcePath)
    {
        byte[] pathHash = SHA256.HashData(
            Encoding.UTF8.GetBytes(sourcePath));

        string cacheRoot = Path.Combine(
            ApplicationData.Current.LocalFolder.Path,
            "ExtensionCompatibility");

        string destinationPath = Path.Combine(
            cacheRoot,
            Convert.ToHexString(pathHash));

        CopyDirectory(sourcePath, destinationPath);
        PrependExtensionTabsCompatibilityShim(destinationPath);

        return destinationPath;
    }

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);

        foreach (string sourceFile in Directory.EnumerateFiles(sourcePath))
        {
            string destinationFile = Path.Combine(
                destinationPath,
                Path.GetFileName(sourceFile));

            File.Copy(sourceFile, destinationFile, overwrite: true);
        }

        foreach (string sourceDirectory in Directory.EnumerateDirectories(sourcePath))
        {
            string destinationDirectory = Path.Combine(
                destinationPath,
                Path.GetFileName(sourceDirectory));

            CopyDirectory(sourceDirectory, destinationDirectory);
        }
    }

    private static void PrependExtensionTabsCompatibilityShim(
        string extensionPath)
    {
        string manifestPath = Path.Combine(extensionPath, "manifest.json");

        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(manifestPath));

        if (!manifest.RootElement.TryGetProperty(
                "background",
                out JsonElement background))
        {
            return;
        }

        string? scriptPath = null;

        if (background.TryGetProperty(
                "service_worker",
                out JsonElement serviceWorker) &&
            serviceWorker.ValueKind == JsonValueKind.String)
        {
            scriptPath = serviceWorker.GetString();
        }
        else if (background.TryGetProperty(
                     "scripts",
                     out JsonElement scripts) &&
                 scripts.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement script in scripts.EnumerateArray())
            {
                if (script.ValueKind == JsonValueKind.String)
                {
                    scriptPath = script.GetString();
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(scriptPath))
            return;

        string extensionRoot = Path.GetFullPath(extensionPath) +
            Path.DirectorySeparatorChar;

        string fullScriptPath = Path.GetFullPath(Path.Combine(
            extensionPath,
            scriptPath.Replace('/', Path.DirectorySeparatorChar)));

        if (!fullScriptPath.StartsWith(
                extensionRoot,
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullScriptPath))
        {
            throw new InvalidOperationException(
                "The extension background script must be inside its folder.");
        }

        File.WriteAllText(
            fullScriptPath,
            ExtensionTabsCompatibilityShim + Environment.NewLine +
            File.ReadAllText(fullScriptPath));
    }

    private void NewTabPositionComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loadingSettings || NewTabPositionComboBox.SelectedIndex < 0)
            return;

        string position = NewTabPositionComboBox.SelectedIndex switch
        {
            0 => "AfterLast",
            1 => "AfterCurrent",
            _ => "AfterLast"
        };

        _settings.Values["NewTabPosition"] = position;
    }

    private void LoadNewTabPosition()
    {
        string position =
            _settings.Values["NewTabPosition"] as string
            ?? "AfterLast";

        NewTabPositionComboBox.SelectedIndex = position switch
        {
            "AfterLast" => 0,
            "AfterCurrent" => 1,
            _ => 0
        };
    }

    private async void ChooseDownloadFolderButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        // Prevent double clicks
        button.IsEnabled = false;

        try
        {
            if (App.MainWindow is not MainWindow window)
                return;

            var picker = new FolderPicker();

            picker.FileTypeFilter.Add("*");

            InitializeWithWindow.Initialize(
                picker,
                window.GetWindowHandle());

            StorageFolder? folder =
                await picker.PickSingleFolderAsync();

            if (folder is null)
                return;

            string path = folder.Path;

            _settings.Values["DownloadFolder"] = path;

            UpdateDownloadLocationDescription(path);

            if (window.GetBrowserProfile() is CoreWebView2Profile profile)
            {
                try
                {
                    profile.DefaultDownloadFolderPath = path;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"Failed to set download folder: {ex}");
                }
            }
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void AskEveryDownload_Toggled(
        object sender,
        RoutedEventArgs e)
    {
        if (_loadingSettings)
            return;

        _settings.Values["AskEveryDownload"] =
            AskEveryDownloadToggle.IsOn;
    }

    private void LoadDownloadLocation()
    {
        string? path =
            _settings.Values["DownloadFolder"] as string;

        if (string.IsNullOrWhiteSpace(path) &&
            App.MainWindow is MainWindow window &&
            window.GetBrowserProfile() is CoreWebView2Profile profile)
        {
            path = profile.DefaultDownloadFolderPath;
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            _settings.Values["DownloadFolder"] = path;
            UpdateDownloadLocationDescription(path);
        }
    }

    private void LoadAskEveryDownload()
    {
        AskEveryDownloadToggle.IsOn =
            _settings.Values["AskEveryDownload"] as bool? ?? false;
    }

    private void UpdateDownloadLocationDescription(string path)
    {
        DownloadLocationCard.Description = path;
    }

    private async void ClearFaviconCacheButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (App.MainWindow is not MainWindow window)
            return;

        ClearFaviconCacheText.Opacity = 0;

        await Task.Delay(200);

        ClearFaviconCacheProgressRing.IsActive = true;

        window.ClearFaviconCache();

        UpdateFaviconCacheDescription();

        await Task.Delay(400); // for visual feedback

        ClearFaviconCacheProgressRing.IsActive = false;
        ClearFaviconCacheText.Opacity = 1;
    }

    private void LoadThemeColorTint()
    {
        ThemeColorTintToggleSwitch.IsOn =
            _settings.Values["ThemeColorTint"] as bool? ?? true;
    }

    private async void ThemeColorTintToggleSwitch_Toggled(
        object sender,
        RoutedEventArgs e)
    {
        if (_loadingSettings)
            return;

        bool enabled = ThemeColorTintToggleSwitch.IsOn;

        _settings.Values["ThemeColorTint"] = enabled;

        if (App.MainWindow is MainWindow window)
            await window.ApplyThemeColorTintAsync(enabled);
    }

    private void WebThemeComboBox_SelectionChanged(
    object sender,
    SelectionChangedEventArgs e)
    {
        if (_loadingSettings || WebThemeComboBox.SelectedIndex < 0)
            return;

        string theme = WebThemeComboBox.SelectedIndex switch
        {
            1 => "FollowAppTheme",
            2 => "Light",
            3 => "Dark",
            _ => "System"
        };

        _settings.Values["WebTheme"] = theme;

        if (App.MainWindow is MainWindow window)
            window.ApplyWebTheme();
    }

    private void LoadWebTheme()
    {
        string theme =
            _settings.Values["WebTheme"] as string
            ?? "System";

        WebThemeComboBox.SelectedIndex = theme switch
        {
            "FollowAppTheme" => 1,
            "Light" => 2,
            "Dark" => 3,
            _ => 0
        };
    }

    private void LoadSearchEngine()
    {
        string engine =
            _settings.Values["SearchEngine"] as string
            ?? "Google";

        SearchEngineComboBox.SelectedIndex = engine switch
        {
            "Bing" => 0,
            "Google" => 1,
            "Yahoo" => 2,
            "DuckDuckGo" => 3,
            _ => 1
        };
    }

    private void SearchEngineComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loadingSettings || SearchEngineComboBox.SelectedIndex < 0)
            return;

        string engine = SearchEngineComboBox.SelectedIndex switch
        {
            0 => "Bing",
            1 => "Google",
            2 => "Yahoo",
            3 => "DuckDuckGo",
            _ => "Google"
        };

        _settings.Values["SearchEngine"] = engine;

        SearchEngineChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LoadFaviconCacheDescription()
    {
        _faviconCacheDescriptionTemplate =
            FaviconCacheCard.Description?.ToString();

        UpdateFaviconCacheDescription();
    }

    private void UpdateFaviconCacheDescription()
    {
        if (string.IsNullOrEmpty(_faviconCacheDescriptionTemplate))
            return;

        string faviconCachePath = Path.Combine(
            ApplicationData.Current.LocalFolder.Path,
            "Favicons");

        long totalBytes = 0;

        if (Directory.Exists(faviconCachePath))
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(
                    faviconCachePath,
                    "*",
                    SearchOption.AllDirectories))
                {
                    try
                    {
                        totalBytes += new FileInfo(file).Length;
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"Failed to calculate favicon cache size: {ex}");
            }
        }

        FaviconCacheCard.Description =
            _faviconCacheDescriptionTemplate.Replace(
                "{0}",
                Helpers.FormatFileSize(totalBytes));
    }

    private void MainWindow_FaviconCacheChanged(
        object? sender,
        EventArgs e)
    {
        UpdateFaviconCacheDescription();
    }

    private void LoadAllShortcuts()
    {
        NewTabShortcutControl.HotkeySettings =
            LoadHotkeySettings("NewTabShortcut", DefaultNewTabShortcut());
        CloseTabShortcutControl.HotkeySettings =
            LoadHotkeySettings("CloseTabShortcut", DefaultCloseTabShortcut());
        ReopenTabShortcutControl.HotkeySettings =
            LoadHotkeySettings("ReopenTabShortcut", DefaultReopenTabShortcut());
        FocusAddressBarShortcutControl.HotkeySettings =
            LoadHotkeySettings("FocusAddressBarShortcut", DefaultFocusAddressBarShortcut());
        ReloadShortcutControl.HotkeySettings =
            LoadHotkeySettings("ReloadShortcut", DefaultReloadShortcut());
        RightTabShortcutControl.HotkeySettings =
            LoadHotkeySettings("RightTabShortcut", DefaultNextTabShortcut());
        LeftTabShortcutControl.HotkeySettings =
            LoadHotkeySettings("LeftTabShortcut", DefaultPreviousTabShortcut());
        FindOnPageShortcutControl.HotkeySettings =
            LoadHotkeySettings("FindOnPageShortcut", DefaultFindOnPageShortcut());
    }

    private void NewTabShortcutControl_ShortcutChanged(object sender, EventArgs e) =>
        ApplyShortcutFromControl(sender, "NewTabShortcut", BrowserShortcut.NewTab);

    private void CloseTabShortcutControl_ShortcutChanged(object sender, EventArgs e) =>
        ApplyShortcutFromControl(sender, "CloseTabShortcut", BrowserShortcut.CloseTab);

    private void ReopenTabShortcutControl_ShortcutChanged(object sender, EventArgs e) =>
        ApplyShortcutFromControl(sender, "ReopenTabShortcut", BrowserShortcut.ReopenTab);

    private void FocusAddressBarShortcutControl_ShortcutChanged(object sender, EventArgs e) =>
        ApplyShortcutFromControl(sender, "FocusAddressBarShortcut", BrowserShortcut.FocusAddressBar);

    private void ReloadShortcutControl_ShortcutChanged(object sender, EventArgs e) =>
        ApplyShortcutFromControl(sender, "ReloadShortcut", BrowserShortcut.Reload);

    private void RightTabShortcutControl_ShortcutChanged(object sender, EventArgs e) =>
        ApplyShortcutFromControl(sender, "RightTabShortcut", BrowserShortcut.NextTab);

    private void LeftTabShortcutControl_ShortcutChanged(object sender, EventArgs e) =>
        ApplyShortcutFromControl(sender, "LeftTabShortcut", BrowserShortcut.PreviousTab);

    private void FindOnPageShortcutControl_ShortcutChanged(object sender, EventArgs e) =>
        ApplyShortcutFromControl(sender, "FindOnPageShortcut", BrowserShortcut.FindOnPage);

    private void ApplyShortcutFromControl(
        object sender,
        string settingsKey,
        BrowserShortcut action)
    {
        if (sender is not ShortcutControl control ||
            control.HotkeySettings is null)
        {
            return;
        }

        SaveHotkeySettings(settingsKey, control.HotkeySettings);

        if (App.MainWindow is MainWindow window)
            window.ApplyShortcut(action, control.HotkeySettings);
    }

    private static HotkeySettings DefaultNewTabShortcut() =>
        new() { Keys = [VirtualKey.Control, VirtualKey.T] };

    private static HotkeySettings DefaultCloseTabShortcut() =>
        new() { Keys = [VirtualKey.Control, VirtualKey.W] };

    private static HotkeySettings DefaultReopenTabShortcut() =>
        new() { Keys = [VirtualKey.Control, VirtualKey.Shift, VirtualKey.T] };

    private static HotkeySettings DefaultFocusAddressBarShortcut() =>
        new() { Keys = [VirtualKey.Control, VirtualKey.L] };

    private static HotkeySettings DefaultReloadShortcut() =>
        new() { Keys = [VirtualKey.Control, VirtualKey.R] };

    private static HotkeySettings DefaultNextTabShortcut() =>
        new() { Keys = [VirtualKey.Control, VirtualKey.Tab] };

    private static HotkeySettings DefaultPreviousTabShortcut() =>
        new() { Keys = [VirtualKey.Control, VirtualKey.Shift, VirtualKey.Tab] };

    private static HotkeySettings DefaultFindOnPageShortcut() =>
        new() { Keys = [VirtualKey.Control, VirtualKey.F] };

    private HotkeySettings LoadHotkeySettings(
        string key,
        HotkeySettings fallback)
    {
        if (!_settings.Values.ContainsKey(key))
            return CloneHotkeySettings(fallback);

        if (_settings.Values[key] is not string stored)
            return CloneHotkeySettings(fallback);

        if (string.IsNullOrWhiteSpace(stored))
            return new HotkeySettings();

        try
        {
            var keys = new List<VirtualKey>();

            foreach (string part in stored.Split(
                         ',',
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(part, out int value) &&
                    Enum.IsDefined(typeof(VirtualKey), value))
                {
                    keys.Add((VirtualKey)value);
                }
            }

            return new HotkeySettings { Keys = keys };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load hotkey '{key}': {ex}");
            return CloneHotkeySettings(fallback);
        }
    }

    private void SaveHotkeySettings(string key, HotkeySettings settings)
    {
        _settings.Values[key] = settings.Keys.Count == 0
            ? string.Empty
            : string.Join(',', settings.Keys.Select(k => (int)k));
    }

    private static HotkeySettings CloneHotkeySettings(
        HotkeySettings settings) =>
        new()
        {
            Keys = [.. settings.Keys]
        };
}
