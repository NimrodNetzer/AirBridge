using AirBridge.Core.Interfaces;
using AirBridge.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace AirBridge.App.Converters;

/// <summary>Converts a <see cref="bool"/> to <see cref="Visibility"/>.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility.Visible;
}

/// <summary>Converts a <see cref="bool"/> to <see cref="Visibility"/> (negated).</summary>
public sealed class BoolToVisibilityNegatedConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility.Collapsed;
}

/// <summary>Negates a <see cref="bool"/> value.</summary>
public sealed class BoolNegateConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is not true;

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is not true;
}

/// <summary>Converts a scanning bool to a Segoe MDL2 icon glyph string.</summary>
public sealed class BoolToScanIconConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? "\uE711" : "\uE721"; // Stop / Search

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Converts a scanning bool to a label string.</summary>
public sealed class BoolToScanTextConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? "Stop Scanning" : "Start Scanning";

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts a <see cref="DeviceType"/> to a Segoe MDL2 Assets font glyph.
/// </summary>
public sealed class DeviceTypeToIconConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, string language)
        => value switch
        {
            DeviceType.WindowsPc      => "\uE7F8",
            DeviceType.AndroidPhone   => "\uE8EA",
            DeviceType.AndroidTablet  => "\uE70A",
            _                         => "\uE8EA"
        };

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Maps a <see cref="TransferState"/> enum value to a human-readable string.</summary>
public sealed class TransferStateToStringConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, string language)
        => value switch
        {
            TransferState.Pending   => "Pending",
            TransferState.Active    => "Active",
            TransferState.Paused    => "Paused",
            TransferState.Completed => "Complete",
            TransferState.Failed    => "Failed",
            TransferState.Cancelled => "Cancelled",
            _                       => value?.ToString() ?? string.Empty
        };

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Converts a byte count to a human-readable string (KB / MB / GB).</summary>
public sealed class BytesToReadableConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not long bytes) return "0 B";
        return bytes switch
        {
            < 1024                => $"{bytes} B",
            < 1024 * 1024         => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _                     => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };
    }

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
