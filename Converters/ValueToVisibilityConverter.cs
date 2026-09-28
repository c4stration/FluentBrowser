using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;
using System.Collections;

namespace FluentBrowser.Converters;

public sealed class ValueToVisibilityConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        string language)
    {
        bool visible;

        if (value is bool boolean)
            visible = boolean;
        else if (value is string text)
            visible = !string.IsNullOrEmpty(text);
        else if (value is ICollection collection)
            visible = collection.Count > 0;
        else
            visible = value is not null;

        if (parameter?.ToString() == "Invert")
            visible = !visible;

        return visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        string language)
    {
        throw new NotSupportedException();
    }
}