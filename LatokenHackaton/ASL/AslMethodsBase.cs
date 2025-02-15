namespace LatokenHackaton.ASL
{
    internal abstract class AslMethodsBase
    {
        public enum TimeUnit
        {
            Years,
            Months,
            Days,
            Hours,
            Minutes,
            Seconds
        }

        [AslDescription("Compares two date/time values. Returns 0 if they are equal, -1 if the first is earlier, or 1 if the first is later.")]
        public int CompareDates(
            [AslDescription("The first date/time.")] DateTime date1,
            [AslDescription("The second date/time.")] DateTime date2
        )
        {
            return date1.CompareTo(date2);
        }

        [AslDescription("Retrieves the total day difference between two date/time values.")]
        public double DifferenceInDays(
            [AslDescription("Starting date/time.")] DateTime startDate,
            [AslDescription("Ending date/time.")] DateTime endDate
        )
        {
            return (endDate - startDate).TotalDays;
        }

        [AslDescription("Alters a date/time by adding a chosen unit (years, months, days, hours, minutes, or seconds) in the specified amount.")]
        public DateTime AddTime(
            [AslDescription("The initial date/time.")] DateTime date,
            [AslDescription("The amount of the chosen unit to add.")] double value,
            [AslDescription("The unit to add.")] TimeUnit timeUnit
        )
        {
            return timeUnit switch
            {
                TimeUnit.Years => date.AddYears((int)value),
                TimeUnit.Months => date.AddMonths((int)value),
                TimeUnit.Days => date.AddDays(value),
                TimeUnit.Hours => date.AddHours(value),
                TimeUnit.Minutes => date.AddMinutes(value),
                TimeUnit.Seconds => date.AddSeconds(value),
                _ => date,
            };
        }

        [AslDescription("Parses a string into a numeric value. Returns null if parsing fails.")]
        public decimal? ParseNumber(
            [AslDescription("A text representing a number.")] string? numberString
        )
        {
            if (string.IsNullOrEmpty(numberString)) return null;
            if (decimal.TryParse(numberString, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var result)) return result;
            return null;
        }

        [AslDescription("Converts a numeric value to a text form.")]
        public string ConvertNumberToString(
            [AslDescription("The numeric value to convert.")] decimal number
        )
        {
            return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}