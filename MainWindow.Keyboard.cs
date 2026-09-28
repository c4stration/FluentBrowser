using FluentBrowser.Controls;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Windows.System;

namespace FluentBrowser;

public enum BrowserShortcut
{
    NewTab,
    CloseTab,
    ReopenTab,
    FocusAddressBar,
    Reload
}

public sealed partial class MainWindow
{
    private readonly Dictionary<BrowserShortcut, HotkeySettings> _shortcuts = new()
    {
        [BrowserShortcut.NewTab] = new() { Keys = [VirtualKey.Control, VirtualKey.T] },
        [BrowserShortcut.CloseTab] = new() { Keys = [VirtualKey.Control, VirtualKey.W] },
        [BrowserShortcut.ReopenTab] = new() { Keys = [VirtualKey.Control, VirtualKey.Shift, VirtualKey.T] },
        [BrowserShortcut.FocusAddressBar] = new() { Keys = [VirtualKey.Control, VirtualKey.L] },
        [BrowserShortcut.Reload] = new() { Keys = [VirtualKey.Control, VirtualKey.R] }
    };

    private readonly Dictionary<BrowserShortcut, KeyboardAccelerator?> _shortcutAccelerators = new()
    {
        [BrowserShortcut.NewTab] = null,
        [BrowserShortcut.CloseTab] = null,
        [BrowserShortcut.ReopenTab] = null,
        [BrowserShortcut.FocusAddressBar] = null,
        [BrowserShortcut.Reload] = null
    };

    private static string SettingsKeyFor(BrowserShortcut action) => action switch
    {
        BrowserShortcut.NewTab => "NewTabShortcut",
        BrowserShortcut.CloseTab => "CloseTabShortcut",
        BrowserShortcut.ReopenTab => "ReopenTabShortcut",
        BrowserShortcut.FocusAddressBar => "FocusAddressBarShortcut",
        BrowserShortcut.Reload => "ReloadShortcut",
        _ => action.ToString()
    };

    private void RegisterKeyboardAccelerators()
    {
        // These accelerators are app-wide commands, not hints for the element
        // under the pointer. Showing them on RootGrid creates a stray tooltip.
        RootGrid.KeyboardAcceleratorPlacementMode =
            KeyboardAcceleratorPlacementMode.Hidden;

        LoadAllShortcutsFromSettings();
        RegisterAllShortcutAccelerators();

        // Hard-reload stays fixed for now
        AddXamlAccelerator(
            VirtualKey.R,
            VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            () =>
            {
                _ = SelectedWebView?.CoreWebView2?
                    .CallDevToolsProtocolMethodAsync(
                        "Page.reload",
                        "{\"ignoreCache\":true}");
            });

        AddXamlAccelerator(VirtualKey.F5, VirtualKeyModifiers.None, () =>
            SelectedWebView?.Reload());

        AddXamlAccelerator(VirtualKey.F11, VirtualKeyModifiers.None, () =>
            ToggleBrowserFullscreen());

        AddXamlAccelerator(VirtualKey.Tab, VirtualKeyModifiers.Control, () =>
            CycleTab(1));

        AddXamlAccelerator(
            VirtualKey.Tab,
            VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            () => CycleTab(-1));

        AddXamlAccelerator(VirtualKey.Add, VirtualKeyModifiers.Control, () =>
        {
            if (SelectedWebView is WebView2 webView)
                _ = SetZoomFactorAsync(
                    webView,
                    Math.Min(GetZoomFactor(webView) + 0.25, 3.0));
        });

        AddXamlAccelerator((VirtualKey)187, VirtualKeyModifiers.Control, () =>
        {
            if (SelectedWebView is WebView2 webView)
                _ = SetZoomFactorAsync(
                    webView,
                    Math.Min(GetZoomFactor(webView) + 0.25, 3.0));
        });

        AddXamlAccelerator(VirtualKey.Subtract, VirtualKeyModifiers.Control, () =>
        {
            if (SelectedWebView is WebView2 webView)
                _ = SetZoomFactorAsync(
                    webView,
                    Math.Max(GetZoomFactor(webView) - 0.25, 0.25));
        });

        AddXamlAccelerator((VirtualKey)189, VirtualKeyModifiers.Control, () =>
        {
            if (SelectedWebView is WebView2 webView)
                _ = SetZoomFactorAsync(
                    webView,
                    Math.Max(GetZoomFactor(webView) - 0.25, 0.25));
        });

        AddXamlAccelerator(VirtualKey.Number0, VirtualKeyModifiers.Control, () =>
        {
            if (SelectedWebView is WebView2 webView)
                _ = SetZoomFactorAsync(webView, 1.0);
        });

        AddXamlAccelerator(VirtualKey.Left, VirtualKeyModifiers.Menu, () =>
        {
            if (SelectedWebView is { CanGoBack: true } webView)
            {
                webView.GoBack();
                webView.Focus(FocusState.Programmatic);
            }
        });

        AddXamlAccelerator(VirtualKey.Right, VirtualKeyModifiers.Menu, () =>
        {
            if (SelectedWebView is { CanGoForward: true } webView)
            {
                webView.GoForward();
                webView.Focus(FocusState.Programmatic);
            }
        });

        AddXamlAccelerator(
            VirtualKey.D,
            VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            () =>
            {
                if (SelectedWebView?.Source is Uri uri)
                    OpenNewTab(uri.AbsoluteUri);
            });

        AddXamlAccelerator(VirtualKey.F12, VirtualKeyModifiers.None, () =>
            SelectedWebView?.CoreWebView2?.OpenDevToolsWindow());

        AddXamlAccelerator(
            VirtualKey.I,
            VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            () => SelectedWebView?.CoreWebView2?.OpenDevToolsWindow());

        AddXamlAccelerator(VirtualKey.U, VirtualKeyModifiers.Control, () =>
        {
            if (SelectedWebView?.Source is Uri uri)
                OpenNewTab($"view-source:{uri}");
        });

        AddXamlAccelerator(VirtualKey.P, VirtualKeyModifiers.Control, () =>
            SelectedWebView?.CoreWebView2?.ShowPrintUI(
                CoreWebView2PrintDialogKind.Browser));

        // Secondary focus-address binding (Ctrl+E), not user-configurable for now
        AddXamlAccelerator(VirtualKey.E, VirtualKeyModifiers.Control, () =>
            AddressBar.Focus(FocusState.Programmatic));
    }

    private void AddXamlAccelerator(
        VirtualKey key,
        VirtualKeyModifiers modifiers,
        Action action)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = modifiers
        };

        accelerator.Invoked += (_, args) =>
        {
            action();
            args.Handled = true;
        };

        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    public void ApplyShortcut(BrowserShortcut action, HotkeySettings settings)
    {
        _shortcuts[action] = new HotkeySettings
        {
            Keys = [.. settings.Keys]
        };

        SaveShortcutToSettings(action, _shortcuts[action]);
        RegisterShortcutAccelerator(action);
        RefreshWebViewShortcuts();
    }

    private void LoadAllShortcutsFromSettings()
    {
        foreach (BrowserShortcut action in Enum.GetValues<BrowserShortcut>())
        {
            _shortcuts[action] = LoadShortcutFromSettings(
                action,
                DefaultShortcut(action));
        }
    }

    private static HotkeySettings DefaultShortcut(BrowserShortcut action) =>
        action switch
        {
            BrowserShortcut.NewTab =>
                new() { Keys = [VirtualKey.Control, VirtualKey.T] },
            BrowserShortcut.CloseTab =>
                new() { Keys = [VirtualKey.Control, VirtualKey.W] },
            BrowserShortcut.ReopenTab =>
                new() { Keys = [VirtualKey.Control, VirtualKey.Shift, VirtualKey.T] },
            BrowserShortcut.FocusAddressBar =>
                new() { Keys = [VirtualKey.Control, VirtualKey.L] },
            BrowserShortcut.Reload =>
                new() { Keys = [VirtualKey.Control, VirtualKey.R] },
            _ => new()
        };

    private HotkeySettings LoadShortcutFromSettings(
        BrowserShortcut action,
        HotkeySettings fallback)
    {
        string key = SettingsKeyFor(action);

        if (!_settings.Values.ContainsKey(key))
            return CloneHotkey(fallback);

        if (_settings.Values[key] is not string stored)
            return CloneHotkey(fallback);

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
            Debug.WriteLine($"Failed to load {key}: {ex}");
            return CloneHotkey(fallback);
        }
    }

    private void SaveShortcutToSettings(
        BrowserShortcut action,
        HotkeySettings settings)
    {
        _settings.Values[SettingsKeyFor(action)] = settings.Keys.Count == 0
            ? string.Empty
            : string.Join(',', settings.Keys.Select(k => (int)k));
    }

    private static HotkeySettings CloneHotkey(HotkeySettings settings) =>
        new() { Keys = [.. settings.Keys] };

    private void RegisterAllShortcutAccelerators()
    {
        foreach (BrowserShortcut action in Enum.GetValues<BrowserShortcut>())
            RegisterShortcutAccelerator(action);
    }

    private void RegisterShortcutAccelerator(BrowserShortcut action)
    {
        if (_shortcutAccelerators.TryGetValue(action, out KeyboardAccelerator? existing) &&
            existing is not null)
        {
            RootGrid.KeyboardAccelerators.Remove(existing);
            _shortcutAccelerators[action] = null;
        }

        HotkeySettings settings = _shortcuts[action];
        if (settings.Keys.Count == 0)
            return;

        if (!TryGetAcceleratorParts(
                settings,
                out VirtualKey key,
                out VirtualKeyModifiers modifiers))
        {
            return;
        }

        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = modifiers
        };

        accelerator.Invoked += (_, args) =>
        {
            InvokeShortcut(action);
            args.Handled = true;
        };

        RootGrid.KeyboardAccelerators.Add(accelerator);
        _shortcutAccelerators[action] = accelerator;
    }

    private void InvokeShortcut(BrowserShortcut action)
    {
        switch (action)
        {
            case BrowserShortcut.NewTab:
                TabView_AddButtonClick(MainTabView, new object());
                break;
            case BrowserShortcut.CloseTab:
                if (MainTabView.SelectedItem is TabViewItem tab)
                    CloseTab(tab);
                break;
            case BrowserShortcut.ReopenTab:
                ReopenClosedTab();
                break;
            case BrowserShortcut.FocusAddressBar:
                AddressBar.Focus(FocusState.Programmatic);
                break;
            case BrowserShortcut.Reload:
                SelectedWebView?.Reload();
                break;
        }
    }

    private static bool TryGetAcceleratorParts(
        HotkeySettings settings,
        out VirtualKey key,
        out VirtualKeyModifiers modifiers)
    {
        key = default;
        modifiers = VirtualKeyModifiers.None;

        VirtualKey? primary = null;

        foreach (VirtualKey k in settings.Keys)
        {
            if (IsControlKey(k))
                modifiers |= VirtualKeyModifiers.Control;
            else if (IsAltKey(k))
                modifiers |= VirtualKeyModifiers.Menu;
            else if (IsShiftKey(k))
                modifiers |= VirtualKeyModifiers.Shift;
            else if (IsWindowsKey(k))
                modifiers |= VirtualKeyModifiers.Windows;
            else
                primary = k;
        }

        if (primary is null)
            return false;

        key = primary.Value;
        return true;
    }

    private bool MatchesHotkey(
        HotkeySettings settings,
        VirtualKey key,
        bool ctrl,
        bool shift,
        bool alt,
        bool win = false)
    {
        if (settings.Keys.Count == 0)
            return false;

        bool wantCtrl = settings.Keys.Any(IsControlKey);
        bool wantShift = settings.Keys.Any(IsShiftKey);
        bool wantAlt = settings.Keys.Any(IsAltKey);
        bool wantWin = settings.Keys.Any(IsWindowsKey);

        if (ctrl != wantCtrl ||
            shift != wantShift ||
            alt != wantAlt ||
            win != wantWin)
        {
            return false;
        }

        VirtualKey? primary = settings.Keys
            .Where(k => !IsModifier(k))
            .Cast<VirtualKey?>()
            .FirstOrDefault();

        if (primary is null)
            return false;

        return KeysMatch(primary.Value, key);
    }

    private static bool KeysMatch(VirtualKey a, VirtualKey b)
    {
        if (a == b)
            return true;

        if (IsControlKey(a) && IsControlKey(b))
            return true;
        if (IsAltKey(a) && IsAltKey(b))
            return true;
        if (IsShiftKey(a) && IsShiftKey(b))
            return true;
        if (IsWindowsKey(a) && IsWindowsKey(b))
            return true;

        return false;
    }

    private static bool IsWindowsKey(VirtualKey key) =>
        key is VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static bool IsControlKey(VirtualKey key) =>
        key is VirtualKey.Control or
            VirtualKey.LeftControl or
            VirtualKey.RightControl;

    private static bool IsAltKey(VirtualKey key) =>
        key is VirtualKey.Menu or
            VirtualKey.LeftMenu or
            VirtualKey.RightMenu;

    private static bool IsShiftKey(VirtualKey key) =>
        key is VirtualKey.Shift or
            VirtualKey.LeftShift or
            VirtualKey.RightShift;

    private static bool IsModifier(VirtualKey key) =>
        IsWindowsKey(key) ||
        IsControlKey(key) ||
        IsAltKey(key) ||
        IsShiftKey(key);

    private bool TryHandleShortcut(
        VirtualKey key,
        bool ctrl,
        bool shift,
        bool alt)
    {
        bool win =
            (GetKeyState(VirtualKey.LeftWindows) & 0x8000) != 0 ||
            (GetKeyState(VirtualKey.RightWindows) & 0x8000) != 0;

        foreach (BrowserShortcut action in Enum.GetValues<BrowserShortcut>())
        {
            if (MatchesHotkey(_shortcuts[action], key, ctrl, shift, alt, win))
            {
                InvokeShortcut(action);
                return true;
            }
        }

        // ctrl + shift + R -> hard reload (fixed)
        if (ctrl && shift && !alt && key == VirtualKey.R)
        {
            _ = SelectedWebView?.CoreWebView2?
                .CallDevToolsProtocolMethodAsync(
                    "Page.reload",
                    "{\"ignoreCache\":true}");
            return true;
        }

        // F5 -> reload (fixed secondary)
        if (!ctrl && !shift && !alt && key == VirtualKey.F5)
        {
            SelectedWebView?.Reload();
            return true;
        }

        // ctrl + Tab / ctrl + shift + Tab
        if (ctrl && !alt && key == VirtualKey.Tab)
        {
            CycleTab(shift ? -1 : 1);
            return true;
        }

        if (ctrl && !shift && !alt)
        {
            if (key is VirtualKey.Add or (VirtualKey)187)
            {
                if (SelectedWebView is WebView2 wv)
                    _ = SetZoomFactorAsync(wv, Math.Min(GetZoomFactor(wv) + 0.25, 3.0));
                return true;
            }

            if (key is VirtualKey.Subtract or (VirtualKey)189)
            {
                if (SelectedWebView is WebView2 wv)
                    _ = SetZoomFactorAsync(wv, Math.Max(GetZoomFactor(wv) - 0.25, 0.25));
                return true;
            }

            if (key == VirtualKey.Number0)
            {
                if (SelectedWebView is WebView2 wv)
                    _ = SetZoomFactorAsync(wv, 1.0);
                return true;
            }

            // Secondary focus-address: Ctrl+E
            if (key == VirtualKey.E)
            {
                AddressBar.Focus(FocusState.Programmatic);
                return true;
            }

            if (key == VirtualKey.U)
            {
                if (SelectedWebView?.Source is Uri uri)
                    OpenNewTab($"view-source:{uri}");
                return true;
            }

            if (key == VirtualKey.P)
            {
                SelectedWebView?.CoreWebView2?.ShowPrintUI(
                    CoreWebView2PrintDialogKind.Browser);
                return true;
            }
        }

        if (alt && !ctrl && !shift)
        {
            if (key == VirtualKey.Left && SelectedWebView is { CanGoBack: true } back)
            {
                back.GoBack();
                return true;
            }

            if (key == VirtualKey.Right && SelectedWebView is { CanGoForward: true } fwd)
            {
                fwd.GoForward();
                return true;
            }
        }

        if (ctrl && shift && !alt && key == VirtualKey.D)
        {
            if (SelectedWebView?.Source is Uri uri)
                OpenNewTab(uri.AbsoluteUri);
            return true;
        }

        if (!ctrl && !shift && !alt && key == VirtualKey.F11)
        {
            ToggleBrowserFullscreen();
            return true;
        }

        if ((!ctrl && !shift && !alt && key == VirtualKey.F12) ||
            (ctrl && shift && !alt && key == VirtualKey.I))
        {
            SelectedWebView?.CoreWebView2?.OpenDevToolsWindow();
            return true;
        }

        return false;
    }

    private void CycleTab(int direction)
    {
        int count = MainTabView.TabItems.Count;
        if (count <= 1) return;

        int current = MainTabView.SelectedIndex;
        if (current < 0) current = 0;

        MainTabView.SelectedIndex = (current + direction + count) % count;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetKeyState(VirtualKey nVirtKey);
}