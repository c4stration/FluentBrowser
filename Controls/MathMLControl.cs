using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FluentBrowser.Controls;

public sealed class MathMLControl : RichEditBox
{
    public static readonly DependencyProperty MathMLProperty =
        DependencyProperty.Register(
            nameof(MathML),
            typeof(string),
            typeof(MathMLControl),
            new PropertyMetadata(string.Empty, OnMathMLChanged));

    public static readonly DependencyProperty MathFontFamilyProperty =
        DependencyProperty.Register(
            nameof(MathFontFamily),
            typeof(string),
            typeof(MathMLControl),
            new PropertyMetadata("Cambria Math", OnMathFontFamilyChanged));

    private bool _isLoaded;
    private bool _isMathModeEnabled;

    public string MathML
    {
        get => (string)GetValue(MathMLProperty);
        set => SetValue(MathMLProperty, value);
    }

    public string MathFontFamily
    {
        get => (string)GetValue(MathFontFamilyProperty);
        set => SetValue(MathFontFamilyProperty, value);
    }

    public MathMLControl()
    {
        IsReadOnly = true;
        IsTabStop = false;
        IsSpellCheckEnabled = false;
        IsTextPredictionEnabled = false;
        AcceptsReturn = false;
        TextWrapping = TextWrapping.Wrap;
        TextReadingOrder = TextReadingOrder.UseFlowDirection;

        Background = new SolidColorBrush(Colors.Transparent);
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        MinHeight = 0;

        ContextFlyout = null!;
        SelectionFlyout = null!;

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        RegisterPropertyChangedCallback(
            FlowDirectionProperty,
            OnFlowDirectionChanged);

        Loaded += MathMLControl_Loaded;
        Unloaded += MathMLControl_Unloaded;
        ActualThemeChanged += MathMLControl_ActualThemeChanged;
    }

    private void OnFlowDirectionChanged(
        DependencyObject sender,
        DependencyProperty property)
    {
        if (!_isLoaded || !_isMathModeEnabled)
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isLoaded && _isMathModeEnabled)
                UpdateMathML(MathML);
        });
    }

    private static void OnMathMLChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        var control = (MathMLControl)sender;

        if (control._isLoaded && control._isMathModeEnabled)
            control.UpdateMathML((string?)args.NewValue);
    }

    private static void OnMathFontFamilyChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        var control = (MathMLControl)sender;

        if (control._isLoaded && control._isMathModeEnabled)
            control.UpdateMathML(control.MathML);
    }

    private void MathMLControl_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;

        if (!_isMathModeEnabled)
        {
            TextDocument.SetMathMode(RichEditMathMode.MathOnly);
            _isMathModeEnabled = true;
        }

        UpdateMathML(MathML);

        DispatcherQueue.TryEnqueue(() => ApplyDocumentForeground());
    }

    private void MathMLControl_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
    }

    private void MathMLControl_ActualThemeChanged(
        FrameworkElement sender,
        object args)
    {
        if (!_isLoaded || !_isMathModeEnabled)
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isLoaded && _isMathModeEnabled)
                UpdateMathML(MathML);
        });
    }

    private void ApplyDocumentForeground()
    {
        if (!_isLoaded ||
            !_isMathModeEnabled ||
            Foreground is not SolidColorBrush foreground)
        {
            return;
        }

        bool wasReadOnly = IsReadOnly;

        try
        {
            IsReadOnly = false;

            ITextRange range = TextDocument.GetRange(0, 0);
            range.SetRange(0, range.StoryLength);

            range.CharacterFormat.ForegroundColor = foreground.Color;
        }
        finally
        {
            IsReadOnly = wasReadOnly;
        }
    }

    private void UpdateMathML(string? mathML)
    {
        if (!_isLoaded || !_isMathModeEnabled)
            return;

        bool wasReadOnly = IsReadOnly;

        try
        {
            IsReadOnly = false;

            if (string.IsNullOrWhiteSpace(mathML))
            {
                TextDocument.SetText(
                    TextSetOptions.None,
                    string.Empty);

                return;
            }

            TextDocument.SetMathML(mathML);

            TextDocument.GetText(
                TextGetOptions.FormatRtf,
                out string rtf);

            System.Diagnostics.Debug.WriteLine(rtf);

            ApplyMathFontFamily(MathFontFamily);
        }
        finally
        {
            IsReadOnly = wasReadOnly;
        }
    }

    private void ApplyMathFontFamily(string? fontFamily)
    {
        if (string.IsNullOrWhiteSpace(fontFamily))
            fontFamily = "Cambria Math";

        fontFamily = fontFamily.Trim();

        TextDocument.GetText(
            TextGetOptions.FormatRtf,
            out string rtf);

        rtf = SetMathFontInRtf(rtf, fontFamily);

        TextDocument.SetText(
            TextSetOptions.FormatRtf |
            TextSetOptions.ApplyRtfDocumentDefaults,
            rtf);

        ApplyDocumentForeground();
    }

    private static string SetMathFontInRtf(
        string rtf,
        string fontFamily)
    {
        const string fontTableMarker = @"{\fonttbl";

        int fontTableStart = rtf.IndexOf(
            fontTableMarker,
            StringComparison.Ordinal);

        if (fontTableStart < 0)
        {
            throw new InvalidOperationException(
                "The RTF font table could not be found.");
        }

        int fontTableEnd = FindRtfGroupEnd(
            rtf,
            fontTableStart);

        string fontTable = rtf.Substring(
            fontTableStart,
            fontTableEnd - fontTableStart + 1);

        int fontIndex = 0;

        foreach (Match match in Regex.Matches(
            fontTable,
            @"\\f(\d+)(?![a-zA-Z])"))
        {
            if (int.TryParse(
                match.Groups[1].Value,
                out int currentIndex))
            {
                fontIndex = Math.Max(
                    fontIndex,
                    currentIndex + 1);
            }
        }

        string fontEntry =
            $@"{{\f{fontIndex}\fnil\fcharset0 {EscapeRtfFontName(fontFamily)};}}";

        rtf = rtf.Insert(fontTableEnd, fontEntry);

        int updatedFontTableEnd = FindRtfGroupEnd(
            rtf,
            fontTableStart);

        rtf = SetRtfMathFontProperty(
            rtf,
            fontIndex,
            updatedFontTableEnd + 1);

        rtf = UpdateMathZoneFontReferences(
            rtf,
            fontIndex);

        return rtf;
    }

    private static string SetRtfMathFontProperty(
        string rtf,
        int fontIndex,
        int insertPosition)
    {
        Match match = Regex.Match(
            rtf,
            @"\{\\(?:\*\\)?mmathPr(?=[\\\s}])");

        string mathFontControl =
            $@"\mmathFont{fontIndex}";

        if (match.Success)
        {
            int groupEnd = FindRtfGroupEnd(
                rtf,
                match.Index);

            string group = rtf.Substring(
                match.Index,
                groupEnd - match.Index + 1);

            string updatedGroup = Regex.Replace(
                group,
                @"\\mmathFont(?:\s*-?\d+)?(?=[^a-zA-Z]|$)",
                _ => mathFontControl);

            if (updatedGroup == group)
            {
                updatedGroup = group.Insert(
                    group.Length - 1,
                    mathFontControl);
            }

            return rtf.Remove(
                    match.Index,
                    group.Length)
                .Insert(
                    match.Index,
                    updatedGroup);
        }

        string mathPropertiesGroup =
            $@"{{\*\mmathPr{mathFontControl}}}";

        return rtf.Insert(
            insertPosition,
            mathPropertiesGroup);
    }

    private static string UpdateMathZoneFontReferences(
        string rtf,
        int fontIndex)
    {
        const string marker = @"{\*\moMath";

        string fontControl = $@"\f{fontIndex}";

        int searchIndex = 0;
        bool foundMathZone = false;

        while (true)
        {
            int start = rtf.IndexOf(
                marker,
                searchIndex,
                StringComparison.Ordinal);

            if (start < 0)
                break;

            int controlEnd = start + marker.Length;

            if (controlEnd < rtf.Length &&
                char.IsLetter(rtf[controlEnd]))
            {
                searchIndex = controlEnd;
                continue;
            }

            foundMathZone = true;

            int end = FindRtfGroupEnd(rtf, start);

            string mathZone = rtf.Substring(
                start,
                end - start + 1);

            bool hasFontReference = Regex.IsMatch(
                mathZone,
                @"\\f\d+(?![a-zA-Z])");

            string updatedMathZone = Regex.Replace(
                mathZone,
                @"\\f\d+(?![a-zA-Z])",
                _ => fontControl);

            if (!hasFontReference)
            {
                updatedMathZone = updatedMathZone.Insert(
                    marker.Length,
                    fontControl);
            }

            rtf = rtf.Remove(
                    start,
                    mathZone.Length)
                .Insert(
                    start,
                    updatedMathZone);

            searchIndex = start + updatedMathZone.Length;
        }

        if (!foundMathZone)
        {
            System.Diagnostics.Debug.WriteLine(
                "No MathML math zones found in RTF.");
        }

        return rtf;
    }

    private static int FindRtfGroupEnd(
        string rtf,
        int start)
    {
        int depth = 0;

        for (int i = start; i < rtf.Length; i++)
        {
            if (rtf[i] == '\\')
            {
                i++;
                continue;
            }

            if (rtf[i] == '{')
            {
                depth++;
            }
            else if (rtf[i] == '}')
            {
                depth--;

                if (depth == 0)
                    return i;
            }
        }

        throw new FormatException(
            "Invalid RTF group.");
    }

    private static string EscapeRtfFontName(
        string fontFamily)
    {
        var builder = new StringBuilder();

        foreach (char character in fontFamily)
        {
            switch (character)
            {
                case '\\':
                    builder.Append(@"\\");
                    break;

                case '{':
                    builder.Append(@"\{");
                    break;

                case '}':
                    builder.Append(@"\}");
                    break;

                case ';':
                    builder.Append(@"\'3b");
                    break;

                default:
                    if (character >= ' ' && character <= '~')
                    {
                        builder.Append(character);
                    }
                    else
                    {
                        builder.Append(@"\u")
                            .Append(unchecked((short)character)
                                .ToString(CultureInfo.InvariantCulture))
                            .Append('?');
                    }

                    break;
            }
        }

        return builder.ToString();
    }
}