namespace StreetEcoTool;

public static class ReminderRules
{
    public static DateOnly? PeriodStart(string frequency, DateOnly today) => frequency switch
    {
        "weekly" => today.AddDays(-(((int)today.DayOfWeek + 6) % 7)),
        "monthly" => new DateOnly(today.Year, today.Month, 1),
        "quarterly" => new DateOnly(today.Year, ((today.Month - 1) / 3) * 3 + 1, 1),
        "irregular" => null,
        _ => throw new ArgumentException("未知排查频次。", nameof(frequency))
    };

    public static bool IsOverdue(DateOnly deadline, string status, DateOnly today) => status != "closed" && deadline < today;
    public static bool IsDueSoon(DateOnly deadline, string status, DateOnly today) => status != "closed" && deadline >= today && deadline <= today.AddDays(3);
    public static bool IsAwaitingReview(string status) => status == "feedback";
    public static bool NeedsInspection(string frequency, bool disabled, DateOnly? lastInspectionDate, DateOnly today)
    {
        if (disabled) return false;
        var start = PeriodStart(frequency, today);
        return start.HasValue && (!lastInspectionDate.HasValue || lastInspectionDate < start || lastInspectionDate > today);
    }

    public static bool NeedsInspection(string frequency, bool disabled, IEnumerable<DateOnly> inspectionDates, DateOnly today)
    {
        if (disabled) return false;
        var start = PeriodStart(frequency, today);
        return start.HasValue && !inspectionDates.Any(date => date >= start.Value && date <= today);
    }
}
