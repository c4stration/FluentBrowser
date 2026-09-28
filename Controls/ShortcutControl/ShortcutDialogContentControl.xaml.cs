using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FluentBrowser.Controls;

public sealed partial class ShortcutDialogContentControl : UserControl
{
    public ObservableCollection<ShortcutKey> Keys { get; } = new();

    public static readonly DependencyProperty IsErrorProperty =
        DependencyProperty.Register(
            nameof(IsError),
            typeof(bool),
            typeof(ShortcutDialogContentControl),
            new PropertyMetadata(false));

    public static readonly DependencyProperty IsWarningAltGrProperty =
        DependencyProperty.Register(
            nameof(IsWarningAltGr),
            typeof(bool),
            typeof(ShortcutDialogContentControl),
            new PropertyMetadata(false));

    public static readonly DependencyProperty HasConflictProperty =
        DependencyProperty.Register(
            nameof(HasConflict),
            typeof(bool),
            typeof(ShortcutDialogContentControl),
            new PropertyMetadata(false));

    public static readonly DependencyProperty ConflictMessageProperty =
        DependencyProperty.Register(
            nameof(ConflictMessage),
            typeof(string),
            typeof(ShortcutDialogContentControl),
            new PropertyMetadata(string.Empty));

    public bool IsError
    {
        get => (bool)GetValue(IsErrorProperty);
        set => SetValue(IsErrorProperty, value);
    }

    public bool IsWarningAltGr
    {
        get => (bool)GetValue(IsWarningAltGrProperty);
        set => SetValue(IsWarningAltGrProperty, value);
    }

    public bool HasConflict
    {
        get => (bool)GetValue(HasConflictProperty);
        set => SetValue(HasConflictProperty, value);
    }

    public string ConflictMessage
    {
        get => (string)GetValue(ConflictMessageProperty);
        set => SetValue(ConflictMessageProperty, value);
    }

    public event RoutedEventHandler? ResetClick;
    public event RoutedEventHandler? ClearClick;

    public ShortcutDialogContentControl()
    {
        InitializeComponent();

        SetKeys(Array.Empty<ShortcutKey>());
    }

    public void SetKeys(IEnumerable<ShortcutKey> keys)
    {
        Keys.Clear();

        foreach (ShortcutKey key in keys)
        {
            Keys.Add(key);
        }

        bool hasKeys = Keys.Count > 0;

        KeysControl.Visibility =
            hasKeys
                ? Visibility.Visible
                : Visibility.Collapsed;

        NoKeysText.Visibility =
            hasKeys
                ? Visibility.Collapsed
                : Visibility.Visible;

        ClearBtn.Visibility =
            hasKeys
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    public void SetAllowDisableDescription(
        bool allowDisable)
    {
        DescriptionTextBlock.Text =
            allowDisable
                ? "Press a combination of keys to change this shortcut. Right-click to remove the key combination."
                : "Press a combination of keys to change this shortcut.";
    }

    private void ResetBtn_Click(
        object sender,
        RoutedEventArgs e)
    {
        ResetClick?.Invoke(this, e);
    }

    private void ClearBtn_Click(
        object sender,
        RoutedEventArgs e)
    {
        ClearClick?.Invoke(this, e);
    }
}