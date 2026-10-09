using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FluentBrowser.Controls;

public sealed class PersistentMenuFlyout : MenuFlyout
{
    private bool _allowClose;

    public PersistentMenuFlyout()
    {
        Closing += OnClosing;
    }

    private void OnClosing(
        FlyoutBase sender,
        FlyoutBaseClosingEventArgs e)
    {
        e.Cancel = !_allowClose;
    }

    public void CloseExplicitly()
    {
        if (!IsOpen)
            return;

        _allowClose = true;

        try
        {
            Hide();
        }
        finally
        {
            _allowClose = false;
        }
    }
}