using System;
using System.Globalization;
using System.Windows.Data;

namespace TradingConsole.Wpf.Converters
{
    /// <summary>
    /// Converts the absolute Change in OI value to a proportional width for a UI element.
    /// This version implements IMultiValueConverter to accept both the value and a dynamic maximum.
    /// </summary>
    public class ChangeOiToWidthConverter : IMultiValueConverter
    {
        /// <summary>
        /// The maximum width the bar can have in the UI.
        /// </summary>
        public double MaxWidth { get; set; } = 75;

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            // Expects two values: [0] = OI Change, [1] = Max OI Change
            if (values != null && values.Length == 2 && values[0] is double changeOi && values[1] is double maxOiChange)
            {
                if (maxOiChange > 0)
                {
                    // Use the absolute value for width calculation
                    double absoluteChange = Math.Abs(changeOi);

                    // Calculate the width proportionally
                    double width = (absoluteChange / maxOiChange) * MaxWidth;

                    // Ensure the width does not exceed the maximum width
                    return Math.Min(width, MaxWidth);
                }
            }

            // Return a width of 0 if values are invalid
            return 0;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            // This converter does not support converting back
            throw new NotImplementedException();
        }
    }
}
