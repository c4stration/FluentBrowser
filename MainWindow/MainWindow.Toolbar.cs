using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    // TODO: move the toolbar button code here!

    private void ToolbarListView_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListView listView)
        {
            return;
        }

        var transitions = listView.ItemContainerTransitions;

        for (int i = transitions.Count - 1; i >= 0; i--)
        {
            if (transitions[i] is ReorderThemeTransition)
            {
                transitions.RemoveAt(i);
            }
        }
    }

    private void CustomizeToolbar_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in ToolbarListView.Items)
        {
            if (ToolbarListView.ContainerFromItem(item) is ListViewItem listViewItem &&
                listViewItem.Content is Button button)
            {
                button.IsEnabled = false;
            }
        }
    }
}