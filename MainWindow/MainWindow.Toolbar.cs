using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    // TODO: move the toolbar button code to here!

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