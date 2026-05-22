using System.ComponentModel;
using System.Text.Json;

namespace LTAI.Agent.Tools;

[Description("Date, time, and timezone utility tools")]
public sealed class DateTimeTools
{
    [Description("Get the current date and time in various formats and timezones.")]
    public static string GetCurrentDateTime(
        [Description("Optional timezone offset, e.g. '+08:00' for China, '-05:00' for US Eastern")] string? timezoneOffset = null)
    {
        var now = DateTimeOffset.UtcNow;
        var local = DateTimeOffset.Now;

        object? target = null;
        if (!string.IsNullOrWhiteSpace(timezoneOffset))
        {
            if (TimeSpan.TryParse(timezoneOffset, out var offset))
                target = now.ToOffset(offset);
        }

        return JsonSerializer.Serialize(new
        {
            utc = now.ToString("O"),
            local = local.ToString("O"),
            target,
            unixSeconds = now.ToUnixTimeSeconds(),
            unixMilliseconds = now.ToUnixTimeMilliseconds(),
            iso8601 = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            dayOfWeek = now.DayOfWeek.ToString(),
            weekOfYear = System.Globalization.ISOWeek.GetWeekOfYear(now.DateTime),
            dayOfYear = now.DayOfYear
        });
    }

    [Description("Format a Unix timestamp (seconds or milliseconds) to human-readable date/time.")]
    public static string FromTimestamp(
        [Description("Unix timestamp value")] long timestamp,
        [Description("Unit: 'seconds' or 'milliseconds'")] string unit = "seconds")
    {
        try
        {
            var dt = unit.Equals("milliseconds", StringComparison.OrdinalIgnoreCase)
                ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
                : DateTimeOffset.FromUnixTimeSeconds(timestamp);
            return JsonSerializer.Serialize(new
            {
                input = timestamp,
                unit,
                utc = dt.ToString("O"),
                local = dt.ToLocalTime().ToString("O"),
                relative = GetRelativeTime(dt)
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Calculate the difference between two dates. Returns days, hours, minutes, and total seconds.")]
    public static string DateDifference(
        [Description("First date/time string, ISO 8601 format")] string date1,
        [Description("Second date/time string, ISO 8601 format")] string date2)
    {
        try
        {
            var d1 = DateTimeOffset.Parse(date1);
            var d2 = DateTimeOffset.Parse(date2);
            var diff = d2 - d1;
            return JsonSerializer.Serialize(new
            {
                date1 = d1.ToString("O"),
                date2 = d2.ToString("O"),
                totalDays = Math.Round(diff.TotalDays, 4),
                totalHours = Math.Round(diff.TotalHours, 2),
                totalMinutes = Math.Round(diff.TotalMinutes, 2),
                totalSeconds = Math.Round(diff.TotalSeconds, 2),
                absolute = Math.Abs(diff.TotalSeconds) < 60
                    ? $"{Math.Abs(Math.Round(diff.TotalSeconds))}s"
                    : Math.Abs(diff.TotalSeconds) < 3600
                        ? $"{Math.Abs(Math.Round(diff.TotalMinutes))}min"
                        : $"{Math.Abs(Math.Round(diff.TotalHours, 1))}h"
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Add or subtract time from a date. Returns the resulting date/time.")]
    public static string DateAdd(
        [Description("Base date/time, ISO 8601 format, or 'now' for current time")] string dateStr,
        [Description("Amount to add (use negative to subtract)")] double amount,
        [Description("Unit: seconds, minutes, hours, days, weeks, months, years")] string unit = "days")
    {
        try
        {
            var dt = dateStr.Equals("now", StringComparison.OrdinalIgnoreCase)
                ? DateTimeOffset.UtcNow
                : DateTimeOffset.Parse(dateStr);

            var result = unit.ToLowerInvariant() switch
            {
                "seconds" => dt.AddSeconds(amount),
                "minutes" => dt.AddMinutes(amount),
                "hours" => dt.AddHours(amount),
                "days" => dt.AddDays(amount),
                "weeks" => dt.AddDays(amount * 7),
                "months" => dt.AddMonths((int)amount),
                "years" => dt.AddYears((int)amount),
                _ => dt.AddDays(amount)
            };

            return JsonSerializer.Serialize(new
            {
                baseDate = dt.ToString("O"),
                operation = $"{amount:+0.##;-0.##} {unit}",
                result = result.ToString("O"),
                isPast = result < DateTimeOffset.UtcNow,
                isFuture = result > DateTimeOffset.UtcNow
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private static string GetRelativeTime(DateTimeOffset dt)
    {
        var diff = DateTimeOffset.UtcNow - dt;
        var totalSeconds = Math.Abs(diff.TotalSeconds);
        if (totalSeconds < 60) return $"{Math.Abs(Math.Round(diff.TotalSeconds))} seconds ago";
        if (totalSeconds < 3600) return $"{Math.Abs(Math.Round(diff.TotalMinutes))} minutes ago";
        if (totalSeconds < 86400) return $"{Math.Abs(Math.Round(diff.TotalHours))} hours ago";
        if (totalSeconds < 2592000) return $"{Math.Abs(Math.Round(diff.TotalDays))} days ago";
        if (diff.TotalDays > 365) return $"{Math.Abs(Math.Round(diff.TotalDays / 365, 1))} years ago";
        return $"{Math.Abs(Math.Round(diff.TotalDays / 30, 1))} months ago";
    }

    [Description("Extract individual date parts: year, month, day, hour, minute, second, dayOfWeek, dayOfYear, quarter, weekOfYear.")]
    public static string DatePart(
        [Description("Date string in ISO 8601 format, or 'now' for current time")] string dateStr = "now",
        [Description("Timezone offset, e.g. '+08:00'")] string? timezoneOffset = null)
    {
        try
        {
            var dt = dateStr.Equals("now", StringComparison.OrdinalIgnoreCase)
                ? DateTimeOffset.UtcNow
                : DateTimeOffset.Parse(dateStr);

            if (!string.IsNullOrWhiteSpace(timezoneOffset) && TimeSpan.TryParse(timezoneOffset, out var offset))
                dt = dt.ToOffset(offset);

            return JsonSerializer.Serialize(new
            {
                iso8601 = dt.ToString("O"),
                year = dt.Year,
                month = dt.Month,
                monthName = dt.ToString("MMMM"),
                day = dt.Day,
                dayOfWeek = dt.DayOfWeek.ToString(),
                dayOfYear = dt.DayOfYear,
                hour = dt.Hour,
                minute = dt.Minute,
                second = dt.Second,
                millisecond = dt.Millisecond,
                quarter = (dt.Month - 1) / 3 + 1,
                weekOfYear = System.Globalization.ISOWeek.GetWeekOfYear(dt.DateTime),
                isWeekend = dt.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
                isLeapYear = DateTime.IsLeapYear(dt.Year)
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}
