using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;

namespace FluentBrowser.Controls;

public sealed class ScrollableListView : ListView
{
    private const double ScrollAmount = 50.0;

    private ScrollViewer? _itemsScrollViewer;
    private RepeatButton? _scrollDecreaseButton;
    private RepeatButton? _scrollIncreaseButton;
    private Panel? _itemsPanel;

    public ScrollableListView()
    {
        ScrollViewer.SetBringIntoViewOnFocusChange(this, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollMode(this, ScrollMode.Enabled);
        ScrollViewer.SetIsDeferredScrollingEnabled(this, false);
        ScrollViewer.SetIsHorizontalRailEnabled(this, false);
        ScrollViewer.SetIsVerticalRailEnabled(this, false);
        ScrollViewer.SetVerticalScrollBarVisibility(this, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollMode(this, ScrollMode.Disabled);
        ScrollViewer.SetZoomMode(this, ZoomMode.Disabled);

        Loaded += ScrollableListView_Loaded;
    }

    protected override void OnApplyTemplate()
    {
        if (_itemsScrollViewer is not null)
        {
            _itemsScrollViewer.Loaded -= ItemsScrollViewer_Loaded;
            _itemsScrollViewer.ViewChanged -= ItemsScrollViewer_ViewChanged;
            _itemsScrollViewer.SizeChanged -= ItemsScrollViewer_SizeChanged;
        }

        DetachScrollButtons();

        if (_itemsPanel is not null)
            _itemsPanel.SizeChanged -= ItemsPanel_SizeChanged;

        _itemsPanel = null;

        base.OnApplyTemplate();

        _itemsScrollViewer = GetTemplateChild("ScrollViewer") as ScrollViewer;

        if (_itemsScrollViewer is null)
            return;

        _itemsScrollViewer.Style =
            (Style)Application.Current.Resources["ScrollableItemsScrollViewerStyle"];

        _itemsScrollViewer.Loaded += ItemsScrollViewer_Loaded;
        _itemsScrollViewer.ViewChanged += ItemsScrollViewer_ViewChanged;
        _itemsScrollViewer.SizeChanged += ItemsScrollViewer_SizeChanged;

        _itemsScrollViewer.ApplyTemplate();
        AttachScrollButtons();
        AttachItemsPanel();
    }

    private void ScrollableListView_Loaded(object sender, RoutedEventArgs e)
    {
        AttachItemsPanel();
        UpdateScrollButtons();
    }

    private void AttachItemsPanel()
    {
        Panel? panel = ItemsPanelRoot;

        if (ReferenceEquals(panel, _itemsPanel))
            return;

        if (_itemsPanel is not null)
            _itemsPanel.SizeChanged -= ItemsPanel_SizeChanged;

        _itemsPanel = panel;

        if (_itemsPanel is not null)
            _itemsPanel.SizeChanged += ItemsPanel_SizeChanged;
    }

    private void ItemsScrollViewer_Loaded(object sender, RoutedEventArgs e)
    {
        AttachScrollButtons();
        UpdateScrollButtons();
    }

    private void AttachScrollButtons()
    {
        DetachScrollButtons();

        if (_itemsScrollViewer is null)
            return;

        _scrollDecreaseButton =
            FindDescendantByName<RepeatButton>(
                _itemsScrollViewer,
                "ScrollDecreaseButton");

        _scrollIncreaseButton =
            FindDescendantByName<RepeatButton>(
                _itemsScrollViewer,
                "ScrollIncreaseButton");

        if (_scrollDecreaseButton is not null)
            _scrollDecreaseButton.Click += ScrollDecreaseButton_Click;

        if (_scrollIncreaseButton is not null)
            _scrollIncreaseButton.Click += ScrollIncreaseButton_Click;
    }

    private void DetachScrollButtons()
    {
        if (_scrollDecreaseButton is not null)
            _scrollDecreaseButton.Click -= ScrollDecreaseButton_Click;

        if (_scrollIncreaseButton is not null)
            _scrollIncreaseButton.Click -= ScrollIncreaseButton_Click;

        _scrollDecreaseButton = null;
        _scrollIncreaseButton = null;
    }

    private void ItemsScrollViewer_ViewChanged(
        object? sender,
        ScrollViewerViewChangedEventArgs e)
    {
        UpdateScrollButtons();
    }

    private void ItemsScrollViewer_SizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        UpdateScrollButtons();
    }

    private void ItemsPanel_SizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        UpdateScrollButtons();
    }

    private void ScrollDecreaseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_itemsScrollViewer is null)
            return;

        _itemsScrollViewer.ChangeView(
            Math.Max(
                0.0,
                _itemsScrollViewer.HorizontalOffset - ScrollAmount),
            null,
            null);
    }

    private void ScrollIncreaseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_itemsScrollViewer is null)
            return;

        _itemsScrollViewer.ChangeView(
            Math.Min(
                _itemsScrollViewer.ScrollableWidth,
                _itemsScrollViewer.HorizontalOffset + ScrollAmount),
            null,
            null);
    }

    private void UpdateScrollButtons()
    {
        if (_itemsScrollViewer is null ||
            _scrollDecreaseButton is null ||
            _scrollIncreaseButton is null)
        {
            return;
        }

        const double minThreshold = 0.1;

        double horizontalOffset = _itemsScrollViewer.HorizontalOffset;
        double scrollableWidth = _itemsScrollViewer.ScrollableWidth;

        if (Math.Abs(horizontalOffset - scrollableWidth) < minThreshold)
        {
            _scrollDecreaseButton.IsEnabled = true;
            _scrollIncreaseButton.IsEnabled = false;
        }
        else if (Math.Abs(horizontalOffset) < minThreshold)
        {
            _scrollDecreaseButton.IsEnabled = false;
            _scrollIncreaseButton.IsEnabled = true;
        }
        else
        {
            _scrollDecreaseButton.IsEnabled = true;
            _scrollIncreaseButton.IsEnabled = true;
        }
    }

    private static T? FindDescendantByName<T>(
        DependencyObject parent,
        string name)
        where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);

            if (child is T element && element.Name == name)
                return element;

            T? descendant = FindDescendantByName<T>(child, name);

            if (descendant is not null)
                return descendant;
        }

        return null;
    }
}