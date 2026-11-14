using FluentBrowser.Shared;
using Microsoft.UI.Xaml.Controls;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace FluentBrowser;

public sealed partial class MainWindow
{
    private const int MaxDroppedTabs = 20;
    private bool _isTabDragInProgress;

    private void MainTabView_TabDragStarting(
        TabView sender,
        TabViewTabDragStartingEventArgs args) =>
        _isTabDragInProgress = true;

    private void MainTabView_TabDragCompleted(
        TabView sender,
        TabViewTabDragCompletedEventArgs args) =>
        _isTabDragInProgress = false;

    private void MainTabView_TabStripDragOver(
        object sender,
        Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (_isTabDragInProgress)
            return;

        DataPackageView view = e.DataView;

        if (!view.Contains(StandardDataFormats.StorageItems) &&
            !view.Contains(StandardDataFormats.WebLink) &&
            !view.Contains(StandardDataFormats.Text))
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = "Open in new tab";
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.IsCaptionVisible = true;
        e.Handled = true;
    }

    private async void MainTabView_TabStripDrop(
        object sender,
        Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (_isTabDragInProgress)
            return;

        int insertIndex = GetTabInsertionIndex(
            e.GetPosition(MainTabView).X);

        Microsoft.UI.Xaml.DragOperationDeferral deferral = e.GetDeferral();

        try
        {
            List<string> urls = await GetDroppedUrlsAsync(e.DataView);

            for (int i = 0; i < urls.Count && i < MaxDroppedTabs; i++)
            {
                OpenNewTab(
                    urls[i],
                    autoSelect: i == 0,
                    insertIndex: insertIndex + i);
            }

            if (urls.Count > 0)
                e.Handled = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"failed to handle tab strip drop: {ex}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private int GetTabInsertionIndex(double pointerX)
    {
        bool isRtl = MainTabView.FlowDirection ==
            Microsoft.UI.Xaml.FlowDirection.RightToLeft;

        for (int i = 0; i < MainTabView.TabItems.Count; i++)
        {
            if (MainTabView.TabItems[i] is not TabViewItem tab ||
                !tab.IsLoaded ||
                tab.ActualWidth <= 0)
            {
                continue;
            }

            double centerX = tab.TransformToVisual(MainTabView)
                .TransformPoint(
                    new Windows.Foundation.Point(tab.ActualWidth / 2, 0)).X;

            if (isRtl ? pointerX > centerX : pointerX < centerX)
                return i;
        }

        return MainTabView.TabItems.Count;
    }

    private static async Task<List<string>> GetDroppedUrlsAsync(
        DataPackageView view)
    {
        var urls = new List<string>();

        if (view.Contains(StandardDataFormats.StorageItems))
        {
            IReadOnlyList<IStorageItem> items =
                await view.GetStorageItemsAsync();

            foreach (IStorageItem item in items)
            {
                if (item is StorageFile file &&
                    !string.IsNullOrEmpty(file.Path))
                {
                    urls.Add(new Uri(file.Path).AbsoluteUri);
                }
            }

            if (urls.Count > 0)
                return urls;
        }

        if (view.Contains(StandardDataFormats.WebLink))
        {
            Uri link = await view.GetWebLinkAsync();

            if (Helpers.IsOpenableWebUri(link))
            {
                urls.Add(link.AbsoluteUri);
                return urls;
            }
        }

        if (view.Contains(StandardDataFormats.Text))
        {
            string text = (await view.GetTextAsync()).Trim();

            if (Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) &&
                Helpers.IsOpenableWebUri(uri))
            {
                urls.Add(uri.AbsoluteUri);
            }
        }

        return urls;
    }
}
