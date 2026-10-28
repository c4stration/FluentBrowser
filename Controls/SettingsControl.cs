using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace FluentBrowser.Controls;

[ContentProperty(Name = nameof(ControlContent))]
public sealed class SettingsControl : ContentControl
{
    private readonly Grid _root = new();
    private readonly SettingsCard _card = new();
    private readonly SettingsExpander _expander = new();

    public SettingsControl()
    {
        _card.MinHeight = 68;
        _expander.MinHeight = 68;
        MinHeight = 68;

        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        _root.HorizontalAlignment = HorizontalAlignment.Stretch;
        _root.VerticalAlignment = VerticalAlignment.Stretch;

        _card.HorizontalAlignment = HorizontalAlignment.Stretch;
        _card.VerticalAlignment = VerticalAlignment.Stretch;

        _expander.HorizontalAlignment = HorizontalAlignment.Stretch;
        _expander.VerticalAlignment = VerticalAlignment.Stretch;

        _root.Children.Add(_card);
        _root.Children.Add(_expander);

        Content = _root;

        Items.CollectionChanged += OnItemsChanged;

        UpdateControl();
    }

    public static readonly DependencyProperty IsExpanderProperty =
        DependencyProperty.Register(
            nameof(IsExpander),
            typeof(bool),
            typeof(SettingsControl),
            new PropertyMetadata(false, OnPropertyChanged));

    public bool IsExpander
    {
        get => (bool)GetValue(IsExpanderProperty);
        set => SetValue(IsExpanderProperty, value);
    }

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(
            nameof(Header),
            typeof(object),
            typeof(SettingsControl),
            new PropertyMetadata(null, OnPropertyChanged));

    public object Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(
            nameof(Description),
            typeof(object),
            typeof(SettingsControl),
            new PropertyMetadata(null, OnPropertyChanged));

    public object Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public static readonly DependencyProperty HeaderIconProperty =
        DependencyProperty.Register(
            nameof(HeaderIcon),
            typeof(IconElement),
            typeof(SettingsControl),
            new PropertyMetadata(null, OnPropertyChanged));

    public IconElement? HeaderIcon
    {
        get => (IconElement?)GetValue(HeaderIconProperty);
        set => SetValue(HeaderIconProperty, value!);
    }

    public static readonly DependencyProperty ControlContentProperty =
        DependencyProperty.Register(
            nameof(ControlContent),
            typeof(object),
            typeof(SettingsControl),
            new PropertyMetadata(null, OnPropertyChanged));

    public object ControlContent
    {
        get => GetValue(ControlContentProperty);
        set => SetValue(ControlContentProperty, value);
    }

    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.Register(
            nameof(IsExpanded),
            typeof(bool),
            typeof(SettingsControl),
            new PropertyMetadata(false, OnPropertyChanged));

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(object),
            typeof(SettingsControl),
            new PropertyMetadata(null, OnPropertyChanged));

    public object ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty ItemTemplateProperty =
        DependencyProperty.Register(
            nameof(ItemTemplate),
            typeof(DataTemplate),
            typeof(SettingsControl),
            new PropertyMetadata(null, OnPropertyChanged));

    public DataTemplate ItemTemplate
    {
        get => (DataTemplate)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public static readonly DependencyProperty ItemContainerStyleSelectorProperty =
        DependencyProperty.Register(
            nameof(ItemContainerStyleSelector),
            typeof(StyleSelector),
            typeof(SettingsControl),
            new PropertyMetadata(null, OnPropertyChanged));

    public StyleSelector ItemContainerStyleSelector
    {
        get => (StyleSelector)GetValue(ItemContainerStyleSelectorProperty);
        set => SetValue(ItemContainerStyleSelectorProperty, value);
    }

    public ObservableCollection<object> Items { get; } = [];

    private static void OnPropertyChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        ((SettingsControl)sender).UpdateControl();
    }

    private void OnItemsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs args)
    {
        _expander.Items.Clear();

        foreach (object item in Items)
        {
            _expander.Items.Add(item);
        }
    }

    private void UpdateControl()
    {
        bool isExpander = IsExpander;

        _card.Visibility = isExpander
            ? Visibility.Collapsed
            : Visibility.Visible;

        _expander.Visibility = isExpander
            ? Visibility.Visible
            : Visibility.Collapsed;

        _card.Content = null!;
        _card.HeaderIcon = null!;
        _card.Description = null!;

        _expander.Content = null!;
        _expander.HeaderIcon = null!;
        _expander.Description = null!;

        if (isExpander)
        {
            _expander.Header = Header;
            _expander.Description = Description;
            _expander.HeaderIcon = HeaderIcon!;
            _expander.Content = ControlContent;
            _expander.IsExpanded = IsExpanded;
            _expander.ItemsSource = ItemsSource;
            _expander.ItemTemplate = ItemTemplate;
            _expander.ItemContainerStyleSelector = ItemContainerStyleSelector;
        }
        else
        {
            _card.Header = Header;
            _card.Description = Description;
            _card.HeaderIcon = HeaderIcon!;
            _card.Content = ControlContent;
        }
    }
}