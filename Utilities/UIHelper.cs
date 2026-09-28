using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FluentBrowser.Utilities;

public static class UIHelpers
{
    public static T? FindDescendant<T>(
        DependencyObject parent,
        string? name = null)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);

        for (int i = 0; i < count; i++)
        {
            DependencyObject child =
                VisualTreeHelper.GetChild(parent, i);

            if (child is T result &&
                (name == null ||
                 child is FrameworkElement element &&
                 element.Name == name))
            {
                return result;
            }

            T? descendant = FindDescendant<T>(child, name);

            if (descendant != null)
                return descendant;
        }

        return null;
    }
}