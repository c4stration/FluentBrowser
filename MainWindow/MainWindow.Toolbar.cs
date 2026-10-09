using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

// TODO: move every toolbar button handlers to this file and add every toolbar item to the pool
// also make it look good!

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private const string ToolbarLayoutSettingKey = "ToolbarLayout";

    private readonly List<ToolbarItem> _allToolbarItems = [];
    private readonly List<string> _activeToolbarIds = [];
    private readonly Dictionary<Control, bool> _toolbarItemEnabledStates = [];
    private bool _isCustomizingToolbar;
    private string? _draggedToolbarItemId;
    private bool _isToolbarPoolPointerPressed;
    private bool _isToolbarPoolDragActive;

    private void ToolbarListView_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListView listView)
            return;

        RemoveListViewTransitions(listView);

        if (_allToolbarItems.Count > 0)
            return;

        CaptureExistingToolbarButtons(listView);
        LoadToolbarLayout();
        RebuildToolbarLists();
    }

    private static void RemoveListViewTransitions(ListView listView)
    {
        var transitions = listView.ItemContainerTransitions;

        for (int i = transitions.Count - 1; i >= 0; i--)
        {
            if (transitions[i] is ReorderThemeTransition or ContentThemeTransition)
                transitions.RemoveAt(i);
        }
    }

    private void CaptureExistingToolbarButtons(ListView listView)
    {
        var captured = new List<(string Id, UIElement Content)>();

        foreach (var obj in listView.Items.ToList())
        {
            if (obj is not ListViewItem container)
                continue;

            if (container.Content is not UIElement content)
                continue;

            string id = content switch
            {
                FrameworkElement fe when !string.IsNullOrEmpty(fe.Name) => fe.Name,
                _ => $"item_{captured.Count}"
            };

            container.Content = null;
            captured.Add((id, content));
        }

        listView.Items.Clear();

        foreach (var (id, content) in captured)
        {
            _allToolbarItems.Add(new ToolbarItem
            {
                Id = id,
                DisplayName = GetToolbarItemDisplayName(id),
                Content = content
            });
            _activeToolbarIds.Add(id);
        }
    }

    private void LoadToolbarLayout()
    {
        if (_settings.Values[ToolbarLayoutSettingKey] is not string json ||
            string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            var savedIds = JsonSerializer.Deserialize<List<string>>(json);
            if (savedIds is null || savedIds.Count == 0)
                return;

            var known = new HashSet<string>(
                _allToolbarItems.Select(i => i.Id),
                StringComparer.Ordinal);

            _activeToolbarIds.Clear();
            foreach (string id in savedIds)
            {
                if (known.Contains(id))
                    _activeToolbarIds.Add(id);
            }
        }
        catch
        {
            // yes
        }
    }

    private void SaveToolbarLayout()
    {
        _settings.Values[ToolbarLayoutSettingKey] =
            JsonSerializer.Serialize(_activeToolbarIds);
    }

    private void RebuildToolbarLists()
    {
        RebuildActiveToolbar();
        RebuildAvailableToolbar();
    }

    private void RebuildActiveToolbar()
    {
        if (ToolbarListView is null)
            return;

        ClearListView(ToolbarListView);

        foreach (string id in _activeToolbarIds)
        {
            var item = FindToolbarItem(id);
            if (item is null)
                continue;

            ToolbarListView.Items.Add(WrapInListViewItem(item.Content));
        }
    }

    private void RebuildAvailableToolbar()
    {
        if (AvailableToolbarListView is null)
            return;

        ClearListView(AvailableToolbarListView);

        foreach (var item in _allToolbarItems)
        {
            if (_activeToolbarIds.Contains(item.Id))
                continue;

            AvailableToolbarListView.Items.Add(
                CreateAvailableToolbarItem(item));
        }
    }

    private static string GetToolbarItemDisplayName(string id) => id switch
    {
        "BackButton" => "Back",
        "ForwardButton" => "Forward",
        "DevToolsButton" => "Developer Tools",
        "ExtensionButton" => "Extensions",
        "DownloadsButton" => "Downloads",
        "MoreButton" => "More",
        _ => id
    };

    private static string GetToolbarItemGlyph(string id) => id switch
    {
        "BackButton" => "\uE72B",
        "ForwardButton" => "\uE72A",
        "DevToolsButton" => "\uEBE8",
        "ExtensionButton" => "\uEA86",
        "DownloadsButton" => "\uE896",
        "MoreButton" => "\uE712",
        _ => "\uE71C"
    };

    private Grid CreateAvailableToolbarItem(ToolbarItem item)
    {
        var grid = new Grid
        {
            Tag = item.Id,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 4,
            IsHitTestVisible = false
        };

        var icon = new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 18,
            Glyph = GetToolbarItemGlyph(item.Id),
            IsHitTestVisible = false
        };

        var label = new TextBlock
        {
            Text = item.DisplayName,
            HorizontalAlignment = HorizontalAlignment.Center,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            IsHitTestVisible = false
        };

        stack.Children.Add(icon);
        stack.Children.Add(label);
        grid.Children.Add(stack);

        return grid;
    }

    private ToolbarItem? FindToolbarItem(string id) =>
        _allToolbarItems.FirstOrDefault(i => i.Id == id);

    private static ListViewItem WrapInListViewItem(UIElement content)
    {
        return new ListViewItem
        {
            Content = content,
            MinWidth = 0,
            Width = double.NaN,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center
        };
    }

    private static void ClearListView(ListView listView)
    {
        foreach (var item in listView.Items.OfType<ListViewItem>())
        {
            item.Content = null;
        }

        listView.Items.Clear();
    }

    private void CustomizeToolbar_Click(object sender, RoutedEventArgs e) =>
        EnterToolbarCustomizeMode();

    private void EnterToolbarCustomizeMode()
    {
        _isCustomizingToolbar = true;
        _draggedToolbarItemId = null;

        foreach (var item in _allToolbarItems)
        {
            if (item.Content is not Control control)
                continue;

            if (!_toolbarItemEnabledStates.ContainsKey(control))
                _toolbarItemEnabledStates[control] = control.IsEnabled;

            control.IsEnabled = false;
        }

        RebuildToolbarLists();
        FlyoutBase.ShowAttachedFlyout(Toolbar);
    }

    private void SyncActiveToolbarIdsFromListView()
    {
        if (ToolbarListView is null)
            return;

        var ids = ToolbarListView.Items
            .Select(ResolveItemId)
            .Where(id => id is not null)
            .Cast<string>()
            .ToList();

        _activeToolbarIds.Clear();
        _activeToolbarIds.AddRange(ids);
    }

    private void ExitToolbarCustomizeMode()
    {
        if (!_isCustomizingToolbar)
            return;

        SyncActiveToolbarIdsFromListView();

        _isCustomizingToolbar = false;
        _draggedToolbarItemId = null;
        ToolbarFlyout.CloseExplicitly();

        ClearListView(ToolbarListView);
        ClearListView(AvailableToolbarListView);

        SaveToolbarLayout();

        foreach (var (element, wasEnabled) in _toolbarItemEnabledStates)
            element.IsEnabled = wasEnabled;

        _toolbarItemEnabledStates.Clear();

        RebuildActiveToolbar();
    }

    private void FinishToolbarCustomize_Click(object sender, RoutedEventArgs e) =>
        ExitToolbarCustomizeMode();

    private void CustomizeToolbarTeachingTip_Closed(
        TeachingTip sender,
        TeachingTipClosedEventArgs args)
    {
        if (_isCustomizingToolbar)
            ExitToolbarCustomizeMode();
    }

    private void ToolbarItem_DragStarting(
        UIElement sender,
        DragStartingEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.Tag is not string id ||
            FindToolbarItem(id) is null)
        {
            e.Cancel = true;
            return;
        }

        _draggedToolbarItemId = id;
        e.Data.SetText(id);
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void Toolbar_DragItemsStarting(
        object sender,
        DragItemsStartingEventArgs e)
    {
        if (e.Items.Count == 0)
            return;

        _isToolbarPoolDragActive = true;

        string? id = ResolveItemId(e.Items[0]);
        if (id is null)
        {
            e.Cancel = true;
            return;
        }

        _draggedToolbarItemId = id;
        e.Data.SetText(id);
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void Toolbar_DragOver(object sender, DragEventArgs e)
    {
        if (_draggedToolbarItemId is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsGlyphVisible = false;
        e.DragUIOverride.IsCaptionVisible = false;
    }

    private void ToolbarItemsPanel_DragOver(object sender, DragEventArgs e)
    {
        if (_draggedToolbarItemId is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsGlyphVisible = false;
        e.DragUIOverride.IsCaptionVisible = false;
    }

    private async void Toolbar_Drop(object sender, DragEventArgs e)
    {
        if (sender is not ListView targetList)
            return;

        string? id = _draggedToolbarItemId;

        if (id is null && e.DataView.Contains(StandardDataFormats.Text))
            id = await e.DataView.GetTextAsync();

        _draggedToolbarItemId = null;

        if (string.IsNullOrEmpty(id) || FindToolbarItem(id) is null)
            return;

        if (ReferenceEquals(targetList, ToolbarListView))
        {
            int oldIndex = _activeToolbarIds.IndexOf(id);
            _activeToolbarIds.Remove(id);

            int insertIndex = GetDropInsertIndex(targetList, e);

            if (oldIndex >= 0 && oldIndex < insertIndex)
                insertIndex--;

            insertIndex = Math.Clamp(insertIndex, 0, _activeToolbarIds.Count);
            _activeToolbarIds.Insert(insertIndex, id);
        }
        else if (ReferenceEquals(targetList, AvailableToolbarListView))
        {
            _activeToolbarIds.Remove(id);
        }

        RebuildToolbarLists();
        e.Handled = true;
    }

    private async void ToolbarItemsPanel_Drop(
        object sender,
        DragEventArgs e)
    {
        string? id = await ResolveDraggedItemId(e);

        if (id is null || FindToolbarItem(id) is null)
            return;

        _draggedToolbarItemId = null;
        _activeToolbarIds.Remove(id);

        RebuildToolbarLists();
        e.Handled = true;
    }

    private void AvailableToolbarListView_PointerPressed(
        object sender,
        PointerRoutedEventArgs e)
    {
        _isToolbarPoolPointerPressed = true;
    }

    private void AvailableToolbarListView_PointerReleased(
        object sender,
        PointerRoutedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _isToolbarPoolPointerPressed = false;
        });
    }

    private void AvailableToolbarListView_DragItemsCompleted(
        ListViewBase sender,
        DragItemsCompletedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _isToolbarPoolDragActive = false;
        });
    }

    private static async Task<string?> ResolveDraggedItemId(DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.Text))
            return await e.DataView.GetTextAsync();

        return null;
    }

    private string? ResolveItemId(object? item)
    {
        UIElement? content = item switch
        {
            ListViewItem lvi => lvi.Content as UIElement,
            UIElement el => el,
            _ => null
        };

        if (content is null)
            return null;

        var match = _allToolbarItems.FirstOrDefault(t =>
            ReferenceEquals(t.Content, content));
        if (match is not null)
            return match.Id;

        if (content is FrameworkElement fe)
        {
            if (!string.IsNullOrEmpty(fe.Name))
                return fe.Name;

            if (fe.Tag is string tagId && !string.IsNullOrEmpty(tagId))
                return tagId;
        }

        return null;
    }

    private static int GetDropInsertIndex(ListView listView, DragEventArgs e)
    {
        int index = listView.Items.Count;

        if (listView.ItemsPanelRoot is not Panel panel)
            return index;

        Point pos = e.GetPosition(panel);

        for (int i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i] is not UIElement child)
                continue;

            var transform = child.TransformToVisual(panel);
            var bounds = transform.TransformBounds(
                new Rect(0, 0, child.ActualSize.X, child.ActualSize.Y));

            double midX = bounds.X + bounds.Width / 2;
            if (pos.X < midX)
                return i;
        }

        return index;
    }

}

public sealed class ToolbarItem
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required UIElement Content { get; init; }
}