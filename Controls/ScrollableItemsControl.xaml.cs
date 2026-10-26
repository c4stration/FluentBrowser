using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections;

namespace FluentBrowser.Controls;

public sealed partial class ScrollableItemsControl : UserControl
{
    private const double ScrollAmount = 50.0;

    private RepeatButton? _scrollDecreaseButton;
    private RepeatButton? _scrollIncreaseButton;

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable),
            typeof(ScrollableItemsControl),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ItemTemplateProperty =
        DependencyProperty.Register(
            nameof(ItemTemplate),
            typeof(DataTemplate),
            typeof(ScrollableItemsControl),
            new PropertyMetadata(null));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public DataTemplate? ItemTemplate
    {
        get => (DataTemplate?)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public ItemsPanelTemplate ItemsPanel
    {
        get => (ItemsPanelTemplate)GetValue(ItemsPanelProperty);
        set => SetValue(ItemsPanelProperty, value);
    }

    public static readonly DependencyProperty ItemsPanelProperty =
        DependencyProperty.Register(
            nameof(ItemsPanel),
            typeof(ItemsPanelTemplate),
            typeof(ScrollableItemsControl),
            new PropertyMetadata(null));

    public TransitionCollection ItemContainerTransitions
    {
        get => (TransitionCollection)GetValue(ItemContainerTransitionsProperty);
        set => SetValue(ItemContainerTransitionsProperty, value);
    }

    public static readonly DependencyProperty ItemContainerTransitionsProperty =
        DependencyProperty.Register(
            nameof(ItemContainerTransitions),
            typeof(TransitionCollection),
            typeof(ScrollableItemsControl),
            new PropertyMetadata(null));

    public ScrollableItemsControl()
    {
        InitializeComponent();
    }

    private void ItemsScrollViewer_Loaded(object sender, RoutedEventArgs e)
    {
        if (_scrollDecreaseButton is not null)
        {
            _scrollDecreaseButton.Click -= ScrollDecreaseButton_Click;
        }

        if (_scrollIncreaseButton is not null)
        {
            _scrollIncreaseButton.Click -= ScrollIncreaseButton_Click;
        }

        _scrollDecreaseButton =
            FindDescendantByName<RepeatButton>(
                ItemsScrollViewer,
                "ScrollDecreaseButton");

        _scrollIncreaseButton =
            FindDescendantByName<RepeatButton>(
                ItemsScrollViewer,
                "ScrollIncreaseButton");

        if (_scrollDecreaseButton is not null)
        {
            _scrollDecreaseButton.Click += ScrollDecreaseButton_Click;
        }

        if (_scrollIncreaseButton is not null)
        {
            _scrollIncreaseButton.Click += ScrollIncreaseButton_Click;
        }

        UpdateScrollButtons();
    }

    private void ItemsScrollViewer_ViewChanged(
        object sender,
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

    private void ItemsList_SizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        UpdateScrollButtons();
    }

    private void ScrollDecreaseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        ItemsScrollViewer.ChangeView(
            Math.Max(
                0.0,
                ItemsScrollViewer.HorizontalOffset - ScrollAmount),
            null,
            null);
    }

    private void ScrollIncreaseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        ItemsScrollViewer.ChangeView(
            Math.Min(
                ItemsScrollViewer.ScrollableWidth,
                ItemsScrollViewer.HorizontalOffset + ScrollAmount),
            null,
            null);
    }

    private void UpdateScrollButtons()
    {
        if (_scrollDecreaseButton is null ||
            _scrollIncreaseButton is null)
        {
            return;
        }

        const double minThreshold = 0.1;

        double horizontalOffset = ItemsScrollViewer.HorizontalOffset;
        double scrollableWidth = ItemsScrollViewer.ScrollableWidth;

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
            {
                return element;
            }

            T? descendant = FindDescendantByName<T>(child, name);

            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}