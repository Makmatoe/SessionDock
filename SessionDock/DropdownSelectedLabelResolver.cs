using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace SessionDock;

/// <summary>
/// Supplies the user-facing label for an object-backed drop-down option.
/// </summary>
public interface IDropdownLabel
{
    string DisplayName { get; }
}

public static class DropdownLabel
{
    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.RegisterAttached(
            "Placeholder",
            typeof(string),
            typeof(DropdownLabel),
            new FrameworkPropertyMetadata(string.Empty));

    public static string GetPlaceholder(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string)element.GetValue(PlaceholderProperty);
    }

    public static void SetPlaceholder(
        DependencyObject element,
        string value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(PlaceholderProperty, value);
    }
}

/// <summary>
/// Resolves the selected ComboBox label without falling back to an arbitrary
/// object's <see cref="object.ToString"/> implementation.
/// </summary>
public sealed class DropdownSelectedLabelResolver : IMultiValueConverter
{
    public object Convert(
        object[] values,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(values);
        _ = targetType;
        _ = parameter;
        _ = culture;

        var selectedItem = values.Length > 0 ? values[0] : null;
        var displayMemberPath = values.Length > 1 ? values[1] as string : null;
        var selectionBoxItem = values.Length > 2 ? values[2] : null;
        var placeholder = values.Length > 3 ? values[3] as string : null;
        return ResolveLabel(
            selectedItem,
            displayMemberPath,
            selectionBoxItem,
            placeholder);
    }

    public object[] ConvertBack(
        object value,
        Type[] targetTypes,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();

    internal static string ResolveLabel(
        object? selectedItem,
        string? displayMemberPath,
        object? selectionBoxItem = null,
        string? placeholder = null)
    {
        if (selectedItem is null ||
            ReferenceEquals(selectedItem, DependencyProperty.UnsetValue))
        {
            return placeholder ?? string.Empty;
        }

        if (TryReadSafeLabel(selectionBoxItem, out var selectionBoxLabel))
            return selectionBoxLabel;

        if (selectedItem is ComboBoxItem comboBoxItem &&
            TryReadSafeLabel(comboBoxItem.Content, out var itemLabel))
        {
            return itemLabel;
        }

        if (TryReadSafeLabel(selectedItem, out var directLabel))
            return directLabel;

        return TryReadDisplayMemberPath(
            selectedItem,
            displayMemberPath,
            out var pathLabel)
                ? pathLabel
                : placeholder ?? string.Empty;
    }

    private static bool TryReadDisplayMemberPath(
        object source,
        string? displayMemberPath,
        out string label)
    {
        label = string.Empty;
        if (string.IsNullOrWhiteSpace(displayMemberPath))
            return false;

        object? current = source;
        foreach (var segment in displayMemberPath.Split('.'))
        {
            if (current is null || string.IsNullOrWhiteSpace(segment))
                return false;

            var property = TypeDescriptor.GetProperties(current)[segment];
            if (property is null)
                return false;

            current = property.GetValue(current);
        }

        return TryReadSafeLabel(current, out label);
    }

    private static bool TryReadSafeLabel(object? value, out string label)
    {
        switch (value)
        {
            case string text when !string.IsNullOrWhiteSpace(text):
                label = text;
                return true;
            case IDropdownLabel option when
                !string.IsNullOrWhiteSpace(option.DisplayName):
                label = option.DisplayName;
                return true;
            case TextBlock textBlock when
                !string.IsNullOrWhiteSpace(textBlock.Text):
                label = textBlock.Text;
                return true;
            case AccessText accessText when
                !string.IsNullOrWhiteSpace(accessText.Text):
                label = accessText.Text;
                return true;
            default:
                label = string.Empty;
                return false;
        }
    }
}
