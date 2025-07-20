using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TradingConsole.Wpf.Converters
{
    /// <summary>
    /// Converts a Change in OI value to a specific color brush.
    /// Green for positive change (OI addition).
    /// Red for negative change (OI reduction).
    /// </summary>
    public class ChangeOiToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double changeOi)
            {
                if (changeOi > 0)
                {
                    // Use a green brush for OI addition
                    return new SolidColorBrush(Color.FromRgb(0, 128, 0)); // Dark Green
                }
                if (changeOi < 0)
                {
                    // Use a red brush for OI reduction
                    return new SolidColorBrush(Color.FromRgb(255, 0, 0)); // Red
                }
            }
            // Return a transparent brush if value is zero or not a valid double
            return Brushes.Transparent;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // This converter does not support converting back
            throw new NotImplementedException();
        }
    }
}
