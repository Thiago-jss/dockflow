namespace DockFlow.Domain;

public readonly record struct ReservationWindow
{
    public DateTimeOffset Start { get; }
    public DateTimeOffset End { get; }

    private ReservationWindow(DateTimeOffset start, DateTimeOffset end) => (Start, End) = (start, end);

    public static ReservationWindow? Normalize(DateTimeOffset start, DateTimeOffset end)
    {
        if (start >= end) return null;
        start = UtcMinute(start);
        end = UtcMinute(end);
        return start < end ? new(start, end) : null;
    }

    private static DateTimeOffset UtcMinute(DateTimeOffset instant) =>
        new(instant.UtcTicks - instant.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
}
