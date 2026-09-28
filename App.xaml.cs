using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using Microsoft.Windows.Globalization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using WinUI3Localizer;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FluentBrowser
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private static Window? _window;
        private static FrameworkElement? ThemeRoot;

        public static CoreWebView2Environment? WebViewEnvironment { get; private set; }

        public static Window? MainWindow => _window;

        public static FlowDirection CurrentFlowDirection { get; private set; }
            = FlowDirection.LeftToRight;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override async void OnLaunched(
            Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            await InitializeLocalizerAsync();

            var options = new CoreWebView2EnvironmentOptions
            {
                AreBrowserExtensionsEnabled = true,
                AdditionalBrowserArguments = "--disable-lcd-text",
                ScrollBarStyle = CoreWebView2ScrollbarStyle.FluentOverlay
            };

            WebViewEnvironment =
                await CoreWebView2Environment.CreateWithOptionsAsync(
                    null,
                    null,
                    options);

            _window = new MainWindow();
            _window.Activate();
        }

        public static void ApplyBackdropMaterial(string material)
        {
            if (MainWindow is MainWindow window)
                window.ApplyBackdropMaterial(material);
        }

        private static async Task InitializeLocalizerAsync()
        {
            string stringsFolderPath =
                Path.Combine(AppContext.BaseDirectory, "Strings");

            ILocalizer localizer = await new LocalizerBuilder()
                .AddStringResourcesFolderForLanguageDictionaries(
                    stringsFolderPath)
                .SetOptions(options =>
                {
                    options.DefaultLanguage = "en-US";
                })
                .Build();

            string savedLanguage =
                ApplicationData.Current.LocalSettings.Values["Language"] as string
                ?? "System";

            string language = ResolveLanguage(savedLanguage);

            await localizer.SetLanguage(language);

            string savedDirection =
                ApplicationData.Current.LocalSettings.Values["TextDirection"] as string
                ?? "RightToLeft";

            if (IsRtlLanguage(language))
            {
                ApplyFlowDirection(
                    language,
                    savedDirection == "LeftToRight"
                        ? FlowDirection.LeftToRight
                        : FlowDirection.RightToLeft);
            }
            else
            {
                ApplyFlowDirection(language);
            }
        }

        private static string ResolveLanguage(string language)
        {
            if (language != "System")
                return language;

            string[] preferredLanguages = ApplicationLanguages.Languages.ToArray();

            foreach (string preferred in preferredLanguages)
            {
                foreach (string available in Localizer.Get().GetAvailableLanguages())
                {
                    if (string.Equals(
                        preferred,
                        available,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return available;
                    }

                    string preferredBase =
                        preferred.Split('-')[0];

                    string availableBase =
                        available.Split('-')[0];

                    if (string.Equals(
                        preferredBase,
                        availableBase,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return available;
                    }
                }
            }

            return "en-US";
        }

        public static bool IsRtlLanguage(string language)
        {
            string languageCode = language.Split('-')[0];

            return languageCode switch
            {
                "ar" => true,
                "he" => true,
                "fa" => true,
                "ur" => true,
                _ => false
            };
        }

        public static void ApplyFlowDirection(
            string language,
            FlowDirection? overrideDirection = null)
        {
            CurrentFlowDirection =
                overrideDirection
                ?? (IsRtlLanguage(language)
                    ? FlowDirection.RightToLeft
                    : FlowDirection.LeftToRight);

            if (ThemeRoot is not null)
                ThemeRoot.FlowDirection = CurrentFlowDirection;

            if (MainWindow is MainWindow window)
            {
                window.MainTabView.Margin =
                    CurrentFlowDirection == FlowDirection.RightToLeft
                        ? new Thickness(152, 0, 0, 0)
                        : new Thickness(0, 0, 64, 0);
            }
        }

        public static void RegisterThemeRoot(
            FrameworkElement root,
            MainWindow window)
        {
            ThemeRoot = root;

            root.FlowDirection = CurrentFlowDirection;

            window.MainTabView.Margin = CurrentFlowDirection == FlowDirection.RightToLeft
                ? new Thickness(152, 0, 0, 0)
                : new Thickness(0, 0, 64, 0);

            string theme =
                ApplicationData.Current.LocalSettings.Values["Theme"] as string
                ?? "System";

            ApplyTheme(theme);
        }

        public static void ApplySavedTheme()
        {
            string theme =
                Windows.Storage.ApplicationData.Current.LocalSettings.Values["Theme"] as string
                ?? "System";

            ApplyTheme(theme);
        }

        public static void ApplyTheme(string theme)
        {
            if (ThemeRoot is null)
                return;

            ThemeRoot.RequestedTheme = theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }
    }
}
