using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.System;

namespace FluentBrowser.Controls;

public sealed partial class ShortcutControl : UserControl, IDisposable
{
    private const int WhKeyboardLl = 13;

    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private readonly LowLevelKeyboardProc _keyboardHookProc;
    public event EventHandler? ShortcutChanged;

    private IntPtr _keyboardHook;
    private readonly HashSet<uint> _pressedKeys = new();

    private bool _dialogOpen;

    private HotkeySettings _draftSettings = new();
    private HotkeySettings _originalSettings = new();

    private readonly ShortcutDialogContentControl _dialogContent;
    private readonly ContentDialog _shortcutDialog;

    public ObservableCollection<ShortcutKey> PreviewKeys { get; } = new();

    public static readonly DependencyProperty HotkeySettingsProperty =
        DependencyProperty.Register(
            nameof(HotkeySettings),
            typeof(HotkeySettings),
            typeof(ShortcutControl),
            new PropertyMetadata(null, OnHotkeySettingsChanged));

    public static readonly DependencyProperty AllowDisableProperty =
        DependencyProperty.Register(
            nameof(AllowDisable),
            typeof(bool),
            typeof(ShortcutControl),
            new PropertyMetadata(false, OnAllowDisableChanged));

    public static readonly DependencyProperty HasConflictProperty =
        DependencyProperty.Register(
            nameof(HasConflict),
            typeof(bool),
            typeof(ShortcutControl),
            new PropertyMetadata(false, OnConflictChanged));

    public static readonly DependencyProperty ConflictDescriptionProperty =
        DependencyProperty.Register(
            nameof(ConflictDescription),
            typeof(string),
            typeof(ShortcutControl),
            new PropertyMetadata(string.Empty, OnConflictChanged));

    public HotkeySettings? HotkeySettings
    {
        get => (HotkeySettings?)GetValue(HotkeySettingsProperty);
        set => SetValue(HotkeySettingsProperty, value);
    }

    public bool AllowDisable
    {
        get => (bool)GetValue(AllowDisableProperty);
        set => SetValue(AllowDisableProperty, value);
    }

    public bool HasConflict
    {
        get => (bool)GetValue(HasConflictProperty);
        set => SetValue(HasConflictProperty, value);
    }

    public string ConflictDescription
    {
        get => (string)GetValue(ConflictDescriptionProperty);
        set => SetValue(ConflictDescriptionProperty, value);
    }

    public ShortcutControl()
    {
        InitializeComponent();

        _keyboardHookProc = KeyboardHookCallback;

        _dialogContent = new ShortcutDialogContentControl();

        _dialogContent.ResetClick += DialogContent_ResetClick;
        _dialogContent.ClearClick += DialogContent_ClearClick;

        _shortcutDialog = new ContentDialog
        {
            Title = "Activation shortcut",
            Content = _dialogContent,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Style = (Style)Application.Current.Resources["FixedContentDialogStyle"],
        };

        _shortcutDialog.Opened += ShortcutDialog_Opened;
        _shortcutDialog.Closing += ShortcutDialog_Closing;
        _shortcutDialog.PrimaryButtonClick += ShortcutDialog_PrimaryButtonClick;

        Loaded += ShortcutControl_Loaded;
        Unloaded += ShortcutControl_Unloaded;

        UpdatePreview();
        UpdateAllowDisableDescription();
        UpdateConflictState();
    }

    private static void OnHotkeySettingsChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (sender is ShortcutControl control)
        {
            control.UpdatePreview();
        }
    }

    private static void OnAllowDisableChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (sender is ShortcutControl control)
        {
            control.UpdateAllowDisableDescription();
        }
    }

    private static void OnConflictChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (sender is ShortcutControl control)
        {
            control.UpdateConflictState();
        }
    }

    private void ShortcutControl_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        UpdatePreview();
        UpdateAllowDisableDescription();
        UpdateConflictState();
    }

    private void ShortcutControl_Unloaded(
        object sender,
        RoutedEventArgs e)
    {
        RemoveKeyboardHook();

        if (_dialogOpen)
        {
            _shortcutDialog.Hide();
        }
    }

    private async void OpenDialogButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_dialogOpen)
        {
            return;
        }

        if (XamlRoot is null)
        {
            return;
        }

        _dialogOpen = true;

        try
        {
            _originalSettings = CloneSettings(
                HotkeySettings ?? new HotkeySettings());

            _draftSettings = CloneSettings(_originalSettings);

            RefreshDialog();

            _shortcutDialog.XamlRoot = XamlRoot;
            _shortcutDialog.RequestedTheme = ActualTheme;

            await _shortcutDialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void ShortcutDialog_Opened(
        ContentDialog sender,
        ContentDialogOpenedEventArgs args)
    {
        InstallKeyboardHook();

        _dialogContent.Focus(FocusState.Programmatic);

        RefreshDialog();
    }

    private void ShortcutDialog_Closing(
        ContentDialog sender,
        ContentDialogClosingEventArgs args)
    {
        RemoveKeyboardHook();

        _draftSettings = CloneSettings(_originalSettings);
        _pressedKeys.Clear();
    }

    private void ShortcutDialog_PrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        bool empty = IsEmptyShortcut(_draftSettings);
        bool valid = IsValidShortcut(_draftSettings) ||
                     (AllowDisable && empty);

        if (!valid)
        {
            args.Cancel = true;
            return;
        }

        HotkeySettings = CloneSettings(_draftSettings);
        UpdatePreview();
        ShortcutChanged?.Invoke(this, EventArgs.Empty);
        RemoveKeyboardHook();
    }

    private void DialogContent_ResetClick(object? sender, RoutedEventArgs e)
    {
        _draftSettings = CloneSettings(_originalSettings);
        NormalizeDraftKeys();
        RefreshDialog();
    }

    private void DialogContent_ClearClick(object? sender, RoutedEventArgs e)
    {
        _draftSettings = new HotkeySettings();
        RefreshDialog();
    }

    private void RefreshDialog()
    {
        List<ShortcutKey> keys = BuildKeyList(_draftSettings);
        _dialogContent.SetKeys(keys);

        bool empty = IsEmptyShortcut(_draftSettings);
        bool valid = IsValidShortcut(_draftSettings) ||
                     (AllowDisable && empty);

        _dialogContent.IsError =
            !empty &&
            !valid;
        _dialogContent.IsWarningAltGr =
            _draftSettings.Keys.Any(IsControlKey) &&
            _draftSettings.Keys.Any(IsAltKey) &&
            _draftSettings.Keys.Any(key => !IsModifier(key));
        _dialogContent.HasConflict = HasConflict;
        _dialogContent.ConflictMessage = ConflictDescription;

        _shortcutDialog.IsPrimaryButtonEnabled = valid;

        AutomationProperties.SetHelpText(
            EditButton,
            keys.Count == 0
                ? "Not set"
                : string.Join(" + ", keys.Select(key => key.Text)));
    }

    private void UpdatePreview()
    {
        List<ShortcutKey> keys = BuildKeyList(
            HotkeySettings ?? new HotkeySettings());

        PreviewKeys.Clear();

        foreach (ShortcutKey key in keys)
        {
            PreviewKeys.Add(key);
        }

        bool configured = keys.Count > 0;

        PreviewKeysControl.Visibility =
            configured
                ? Visibility.Visible
                : Visibility.Collapsed;

        PlaceholderPanel.Visibility =
            configured
                ? Visibility.Collapsed
                : Visibility.Visible;

        EditIcon.Visibility =
            configured
                ? Visibility.Visible
                : Visibility.Collapsed;

        AutomationProperties.SetHelpText(
            EditButton,
            configured
                ? string.Join(" + ", keys.Select(key => key.Text))
                : "Not set");
    }

    private void UpdateAllowDisableDescription()
    {
        _dialogContent.SetAllowDisableDescription(AllowDisable);
    }

    private void UpdateConflictState()
    {
        if (_dialogContent is null)
        {
            return;
        }

        _dialogContent.HasConflict = HasConflict;
        _dialogContent.ConflictMessage = ConflictDescription;
    }

    private bool IsBrowserWindowForeground()
    {
        IntPtr foregroundWindow = GetForegroundWindow();

        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        IntPtr browserWindow =
            WinRT.Interop.WindowNative.GetWindowHandle(
                App.MainWindow);

        return foregroundWindow == browserWindow;
    }

    private void InstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            return;
        }

        _keyboardHook = SetWindowsHookEx(
            WhKeyboardLl,
            _keyboardHookProc,
            GetModuleHandle(null),
            0);
    }

    private void RemoveKeyboardHook()
    {
        if (_keyboardHook == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_keyboardHook);
        _keyboardHook = IntPtr.Zero;

        _pressedKeys.Clear();
    }

    private IntPtr KeyboardHookCallback(
        int code,
        IntPtr wParam,
        IntPtr lParam)
    {
        if (code < 0)
        {
            return CallNextHookEx(
                _keyboardHook,
                code,
                wParam,
                lParam);
        }

        KbdLlHookStruct info =
            Marshal.PtrToStructure<KbdLlHookStruct>(lParam);

        uint message = unchecked((uint)wParam.ToInt64());

        bool keyDown =
            message == WmKeyDown ||
            message == WmSysKeyDown;

        bool keyUp =
            message == WmKeyUp ||
            message == WmSysKeyUp;

        if (!keyDown && !keyUp)
        {
            return CallNextHookEx(
                _keyboardHook,
                code,
                wParam,
                lParam);
        }

        if (!_dialogOpen || !IsBrowserWindowForeground())
        {
            return CallNextHookEx(
                _keyboardHook,
                code,
                wParam,
                lParam);
        }

        uint virtualKeyCode = info.vkCode;

        if (keyDown)
        {
            if (!_pressedKeys.Add(virtualKeyCode))
            {
                return (IntPtr)1;
            }

            VirtualKey key = (VirtualKey)virtualKeyCode;

            if (ShouldPassThrough(key))
            {
                return CallNextHookEx(
                    _keyboardHook,
                    code,
                    wParam,
                    lParam);
            }

            HandleKeyDown(key);

            return 1;
        }

        _pressedKeys.Remove(virtualKeyCode);

        VirtualKey releasedKey =
            (VirtualKey)virtualKeyCode;

        HandleKeyUp(releasedKey);

        if (IsModifier(releasedKey))
        {
            return (IntPtr)1;
        }

        return CallNextHookEx(
            _keyboardHook,
            code,
            wParam,
            lParam);
    }

    private bool ShouldPassThrough(VirtualKey key)
    {
        if (_shortcutDialog.XamlRoot is null)
        {
            return false;
        }

        // Capture function keys so F1–F12 can be bound alone.
        if (IsFunctionKey(key))
        {
            return false;
        }

        bool hasCommandModifier =
            _draftSettings.Keys.Any(IsControlKey) ||
            _draftSettings.Keys.Any(IsAltKey) ||
            _draftSettings.Keys.Any(IsWindowsKey);

        bool hasModifier =
            hasCommandModifier ||
            _draftSettings.Keys.Any(IsShiftKey);

        if (key == VirtualKey.Tab &&
            !hasCommandModifier)
        {
            return true;
        }

        if (key is VirtualKey.Enter or VirtualKey.Space)
        {
            if (!hasModifier &&
                FocusManager.GetFocusedElement(
                    _shortcutDialog.XamlRoot) is
                    Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)
            {
                return true;
            }

            if (!hasModifier)
            {
                return true;
            }
        }

        if (FocusManager.GetFocusedElement(
                _shortcutDialog.XamlRoot) is
            Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)
        {
            return true;
        }

        return false;
    }

    private void HandleKeyDown(VirtualKey key)
    {
        if (key == VirtualKey.Escape)
        {
            _draftSettings = new HotkeySettings();
            RefreshDialog();
            return;
        }

        if (IsModifier(key))
        {
            if (!_draftSettings.Keys.Contains(key))
            {
                _draftSettings.Keys.Add(key);
            }
        }
        else
        {
            _draftSettings.Keys.RemoveAll(existingKey => !IsModifier(existingKey));
            _draftSettings.Keys.Add(key);
        }

        NormalizeDraftKeys();
        RefreshDialog();
    }

    private void HandleKeyUp(VirtualKey key)
    {
        if (!IsModifier(key))
        {
            return;
        }

        if (_draftSettings.Keys.Any(
            existingKey => existingKey == key))
        {
            RefreshDialog();
        }
    }

    private static bool IsEmptyShortcut(HotkeySettings settings)
    {
        return settings.Keys.Count == 0;
    }

    private static bool IsValidShortcut(HotkeySettings settings)
    {
        bool hasKey =
            settings.Keys.Any(key => !IsModifier(key));

        if (!hasKey)
            return false;

        bool hasModifier =
            settings.Keys.Any(IsModifier);

        // Function keys are valid without a modifier (e.g. F11, F12).
        bool functionKeyOnly =
            settings.Keys.Count == 1 &&
            IsFunctionKey(settings.Keys[0]);

        return hasModifier || functionKeyOnly;
    }

    private static bool IsFunctionKey(VirtualKey key)
    {
        return key >= VirtualKey.F1 && key <= VirtualKey.F24;
    }

    private static int GetKeyOrder(VirtualKey key)
    {
        if (IsWindowsKey(key)) return 0;
        if (IsControlKey(key)) return 1;
        if (IsAltKey(key)) return 2;
        if (IsShiftKey(key)) return 3;
        return 4;
    }

    private void NormalizeDraftKeys()
    {
        _draftSettings.Keys = _draftSettings.Keys
            .OrderBy(GetKeyOrder)
            .ToList();
    }

    private static HotkeySettings CloneSettings(
        HotkeySettings settings)
    {
        return new HotkeySettings
        {
            Keys = [.. settings.Keys]
        };
    }

    private static List<ShortcutKey> BuildKeyList(HotkeySettings settings)
    {
        var keys = new List<ShortcutKey>();

        foreach (VirtualKey key in settings.Keys.OrderBy(GetKeyOrder))
        {
            if (IsWindowsKey(key))
            {
                keys.Add(new ShortcutKey { IsWindowsKey = true });
                continue;
            }

            if (IsControlKey(key))
            {
                keys.Add(new ShortcutKey { Text = "Ctrl" });
                continue;
            }

            if (IsAltKey(key))
            {
                keys.Add(new ShortcutKey { Text = "Alt" });
                continue;
            }

            if (IsShiftKey(key))
            {
                keys.Add(new ShortcutKey
                {
                    Text = "\uE752",
                    FontFamily = "Segoe Fluent Icons"
                });
                continue;
            }

            keys.Add(new ShortcutKey { Text = GetKeyName(key) });
        }

        return keys;
    }

    private static bool IsWindowsKey(VirtualKey key)
    {
        return key is
            VirtualKey.LeftWindows or
            VirtualKey.RightWindows;
    }

    private static bool IsControlKey(VirtualKey key)
    {
        return key is
            VirtualKey.Control or
            VirtualKey.LeftControl or
            VirtualKey.RightControl;
    }

    private static bool IsAltKey(VirtualKey key)
    {
        return key is
            VirtualKey.Menu or
            VirtualKey.LeftMenu or
            VirtualKey.RightMenu;
    }

    private static bool IsShiftKey(VirtualKey key)
    {
        return key is
            VirtualKey.Shift or
            VirtualKey.LeftShift or
            VirtualKey.RightShift;
    }

    private static bool IsModifier(VirtualKey key)
    {
        return IsWindowsKey(key) ||
               IsControlKey(key) ||
               IsAltKey(key) ||
               IsShiftKey(key);
    }

    private static string GetKeyName(VirtualKey key)
    {
        if (key >= VirtualKey.A &&
            key <= VirtualKey.Z)
        {
            return key.ToString();
        }

        if (key >= VirtualKey.Number0 &&
            key <= VirtualKey.Number9)
        {
            return key.ToString()[6..];
        }

        if (key >= VirtualKey.F1 &&
            key <= VirtualKey.F24)
        {
            return key.ToString();
        }

        return key switch
        {
            VirtualKey.Left => "←",
            VirtualKey.Right => "→",
            VirtualKey.Up => "↑",
            VirtualKey.Down => "↓",

            VirtualKey.Enter => "Enter",
            VirtualKey.Escape => "Esc",
            VirtualKey.Back => "Backspace",
            VirtualKey.Tab => "Tab",
            VirtualKey.Space => "Space",

            VirtualKey.Insert => "Insert",
            VirtualKey.Delete => "Delete",
            VirtualKey.Home => "Home",
            VirtualKey.End => "End",
            VirtualKey.PageUp => "Page Up",
            VirtualKey.PageDown => "Page Down",

            VirtualKey.Print => "Print Screen",
            VirtualKey.Scroll => "Scroll Lock",
            VirtualKey.Pause => "Pause",

            VirtualKey.NumberPad0 => "Num 0",
            VirtualKey.NumberPad1 => "Num 1",
            VirtualKey.NumberPad2 => "Num 2",
            VirtualKey.NumberPad3 => "Num 3",
            VirtualKey.NumberPad4 => "Num 4",
            VirtualKey.NumberPad5 => "Num 5",
            VirtualKey.NumberPad6 => "Num 6",
            VirtualKey.NumberPad7 => "Num 7",
            VirtualKey.NumberPad8 => "Num 8",
            VirtualKey.NumberPad9 => "Num 9",

            VirtualKey.Multiply => "*",
            VirtualKey.Add => "+",
            VirtualKey.Subtract => "-",
            VirtualKey.Decimal => ".",
            VirtualKey.Divide => "/",

            _ => key.ToString()
        };
    }

    public void Dispose()
    {
        RemoveKeyboardHook();

        _shortcutDialog.Opened -= ShortcutDialog_Opened;
        _shortcutDialog.Closing -= ShortcutDialog_Closing;
        _shortcutDialog.PrimaryButtonClick -=
            ShortcutDialog_PrimaryButtonClick;

        _dialogContent.ResetClick -=
            DialogContent_ResetClick;
        _dialogContent.ClearClick -=
            DialogContent_ClearClick;
    }

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelKeyboardProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(
        IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr GetModuleHandle(
        string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(
        int vKey);

    private delegate IntPtr LowLevelKeyboardProc(
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }
}

public sealed class ShortcutKey
{
    public string Text { get; set; } = string.Empty;
    public string FontFamily { get; set; } = "Segoe UI";
    public bool IsWindowsKey { get; set; }
}

public sealed class HotkeySettings
{
    public List<VirtualKey> Keys { get; set; } = [];
}