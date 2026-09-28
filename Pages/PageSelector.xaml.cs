using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace FluentBrowser.Pages;

public sealed partial class PageSelector : Page
{
    private readonly List<PageDefinition> _pages;

    public PageSelector()
    {
        InitializeComponent();

        ErrorStatusComboBox.ItemsSource =
            Enum.GetValues<CoreWebView2WebErrorStatus>()
                .Select(status =>
                    new ErrorStatusDefinition(
                        status,
                        status.ToString()))
                .ToList();

        ErrorStatusComboBox.SelectedIndex = 0;

        _pages = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(type =>
                type.Namespace == "FluentBrowser.Pages" &&
                typeof(Page).IsAssignableFrom(type) &&
                !type.IsAbstract &&
                type != typeof(PageSelector))
            .OrderBy(type => type.Name)
            .Select(type =>
                new PageDefinition(
                    type,
                    type.Name))
            .ToList();

        PageComboBox.ItemsSource = _pages;

        if (_pages.Count > 0)
            PageComboBox.SelectedIndex = 0;
    }

    private void PageComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (PageComboBox.SelectedItem is not PageDefinition page)
            return;

        bool isCantOpenPage =
            page.Type == typeof(CantOpenPage);

        ErrorStatusComboBox.Visibility =
            isCantOpenPage
                ? Visibility.Visible
                : Visibility.Collapsed;

        UrlTextBox.Visibility =
            isCantOpenPage
                ? Visibility.Visible
                : Visibility.Collapsed;

        NavigateButton.Visibility =
            isCantOpenPage
                ? Visibility.Visible
                : Visibility.Collapsed;

        NavigateSelectedPage();
    }

    private void NavigateSelectedPage()
    {
        if (PageComboBox.SelectedItem is not PageDefinition page)
            return;

        if (page.Type == typeof(CantOpenPage))
        {
            if (ErrorStatusComboBox.SelectedItem
                is not ErrorStatusDefinition errorStatus)
            {
                return;
            }

            ContentFrame.Content =
                Activator.CreateInstance(
                    page.Type,
                    errorStatus.Status,
                    UrlTextBox.Text,
                    null);

            return;
        }

        ContentFrame.Navigate(page.Type, null, new DrillInNavigationTransitionInfo());
    }

    private void NavigateButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        NavigateSelectedPage();
    }

    private sealed class PageDefinition
    {
        public Type Type { get; }
        public string Name { get; }

        public PageDefinition(
            Type type,
            string name)
        {
            Type = type;
            Name = name;
        }
    }

    private sealed class ErrorStatusDefinition
    {
        public CoreWebView2WebErrorStatus Status { get; }
        public string Name { get; }

        public ErrorStatusDefinition(
            CoreWebView2WebErrorStatus status,
            string name)
        {
            Status = status;
            Name = name;
        }
    }
}