using System;
using System.Globalization;
using System.Windows.Data;

namespace ACP.NINA.Plugin.Sequencer {

    /// <summary>
    /// Shows InputCoordinates.DecDegrees with its sign, and reads a signed
    /// number back into the NegativeDec flag plus the unsigned degrees.
    ///
    /// NINA keeps the sign on a separate NegativeDec flag so that a
    /// declination between 0 and -1 degrees (typed as "-0 30 00") survives.
    /// The values array is [NegativeDec, DecDegrees] in that order on
    /// purpose: WPF writes ConvertBack's results in binding order, and
    /// DecDegrees' setter uses NegativeDec when it recomputes the angle, so
    /// the flag has to land first.
    /// </summary>
    public class DecDegreesConverter : IMultiValueConverter {

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) {
            if (values == null || values.Length < 2) return "";
            var negative = values[0] is bool b && b;
            var degrees = values[1] is int i ? Math.Abs(i) : 0;
            return (negative ? "-" : "") + degrees.ToString(culture);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) {
            var text = (value as string ?? "").Trim();
            var negative = text.StartsWith("-", StringComparison.Ordinal);
            if (!int.TryParse(text.TrimStart('-', '+'), NumberStyles.Integer, culture, out var degrees)) {
                return new object[] { Binding.DoNothing, Binding.DoNothing };
            }
            degrees = Math.Min(90, Math.Abs(degrees));
            return new object[] { negative, degrees };
        }
    }
}
