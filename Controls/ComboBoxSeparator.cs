using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace FluentBrowser.Controls;

public sealed class ComboBoxSeparator : ComboBoxItem
{
    public ComboBoxSeparator()
    {
        IsEnabled = false;
        IsHitTestVisible = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Center;

        MinHeight = 0;
        Height = 9;
        Padding = new Thickness(0);
        Margin = new Thickness(-13, -2, -13, -2);

        Style = null;

        Content = new Rectangle
        {
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(12, 0, 12, 0),
            Fill = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"]
        };
    }
}