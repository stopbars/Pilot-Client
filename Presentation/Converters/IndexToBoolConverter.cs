using System;
using System.Globalization;
using System.Windows.Data;

namespace BARS_Client_V2;

/// <summary>
/// Converts an integer index to a boolean for RadioButton IsChecked binding.
/// The ConverterParameter specifies which index should return true.
/// </summary>
public sealed class IndexToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int index && parameter is string paramStr && int.TryParse(paramStr, out int targetIndex))
        {
            return index == targetIndex;
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isChecked && isChecked && parameter is string paramStr && int.TryParse(paramStr, out int targetIndex))
        {
            return targetIndex;
        }
        return Binding.DoNothing;
    }
}
