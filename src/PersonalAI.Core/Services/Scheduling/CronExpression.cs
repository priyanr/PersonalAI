namespace PersonalAI.Core.Services.Scheduling;

/// <summary>A minimal parser and next-occurrence calculator for standard 5-field cron expressions
/// ("minute hour day-of-month month day-of-week"), evaluated in UTC. Supports "*", "*/step", lists
/// ("1,2,5"), ranges ("1-5"), and stepped ranges ("1-30/5") in each field. Day-of-month and day-of-week
/// are combined with OR (cron semantics: a match on either field is enough), matching the traditional
/// vixie-cron behavior when both are restricted.</summary>
public sealed class CronExpression
{
    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _daysOfMonth = new bool[32]; // 1-31
    private readonly bool[] _months = new bool[13]; // 1-12
    private readonly bool[] _daysOfWeek = new bool[7]; // 0-6, Sunday = 0
    private readonly bool _restrictedDayOfMonth;
    private readonly bool _restrictedDayOfWeek;

    private CronExpression(bool[] minutes, bool[] hours, bool[] daysOfMonth, bool[] months, bool[] daysOfWeek,
        bool restrictedDayOfMonth, bool restrictedDayOfWeek)
    {
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _restrictedDayOfMonth = restrictedDayOfMonth;
        _restrictedDayOfWeek = restrictedDayOfWeek;
    }

    public static CronExpression Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new FormatException("Cron expression is empty.");

        var fields = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
            throw new FormatException($"Cron expression must have 5 fields (minute hour day-of-month month day-of-week), got {fields.Length}.");

        var minutes = ParseField(fields[0], 0, 59);
        var hours = ParseField(fields[1], 0, 23);
        var daysOfMonth = ParseField(fields[2], 1, 31);
        var months = ParseField(fields[3], 1, 12);
        var daysOfWeek = ParseField(fields[4], 0, 6);

        return new CronExpression(minutes, hours, daysOfMonth, months, daysOfWeek,
            restrictedDayOfMonth: fields[2].Trim() != "*",
            restrictedDayOfWeek: fields[4].Trim() != "*");
    }

    /// <summary>Returns the next UTC instant strictly after <paramref name="fromUtc"/> (seconds truncated)
    /// that matches this expression, searching up to ~4 years ahead. Throws if none is found in that window
    /// (e.g. an expression like "day 31" combined with "month 2" can never match).</summary>
    public DateTimeOffset GetNextOccurrence(DateTimeOffset fromUtc)
    {
        var candidate = fromUtc.ToUniversalTime().AddSeconds(-fromUtc.Second).AddTicks(-(fromUtc.Ticks % TimeSpan.TicksPerSecond));
        candidate = candidate.AddMinutes(1);

        var limit = fromUtc.AddYears(4);
        while (candidate <= limit)
        {
            if (_months[candidate.Month] && DayMatches(candidate) && _hours[candidate.Hour] && _minutes[candidate.Minute])
                return candidate;
            candidate = candidate.AddMinutes(1);
        }

        throw new InvalidOperationException("Cron expression does not match any time in the next 4 years.");
    }

    private bool DayMatches(DateTimeOffset candidate)
    {
        var dayOfMonthMatch = _daysOfMonth[candidate.Day];
        var dayOfWeekMatch = _daysOfWeek[(int)candidate.DayOfWeek];

        // Cron semantics: when both day-of-month and day-of-week are restricted (not "*"), a match on
        // either is sufficient. When only one is restricted, that one alone governs.
        if (_restrictedDayOfMonth && _restrictedDayOfWeek) return dayOfMonthMatch || dayOfWeekMatch;
        if (_restrictedDayOfMonth) return dayOfMonthMatch;
        if (_restrictedDayOfWeek) return dayOfWeekMatch;
        return true;
    }

    private static bool[] ParseField(string field, int min, int max)
    {
        var flags = new bool[max + 1];
        foreach (var part in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
            ParsePart(part, min, max, flags);
        return flags;
    }

    private static void ParsePart(string part, int min, int max, bool[] flags)
    {
        int step = 1;
        string range = part;

        var slashIndex = part.IndexOf('/');
        if (slashIndex >= 0)
        {
            range = part[..slashIndex];
            if (!int.TryParse(part[(slashIndex + 1)..], out step) || step <= 0)
                throw new FormatException($"Invalid step in cron field part '{part}'.");
        }

        int rangeStart, rangeEnd;
        if (range == "*")
        {
            rangeStart = min;
            rangeEnd = max;
        }
        else if (range.Contains('-'))
        {
            var bounds = range.Split('-', 2);
            if (!int.TryParse(bounds[0], out rangeStart) || !int.TryParse(bounds[1], out rangeEnd))
                throw new FormatException($"Invalid range in cron field part '{part}'.");
        }
        else
        {
            if (!int.TryParse(range, out rangeStart))
                throw new FormatException($"Invalid value in cron field part '{part}'.");
            rangeEnd = rangeStart;
        }

        if (rangeStart < min || rangeEnd > max || rangeStart > rangeEnd)
            throw new FormatException($"Cron field part '{part}' is out of range [{min}, {max}].");

        for (var v = rangeStart; v <= rangeEnd; v += step)
            flags[v] = true;
    }
}
