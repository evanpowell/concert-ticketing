namespace Ticketing.Api.Domain;

public abstract record HoldOutcome
{
    public sealed record Created(int HoldId, DateTimeOffset ExpiresAt, int[] ShowSeatIds) : HoldOutcome;
    public sealed record SeatUnavailable : HoldOutcome;
    public sealed record ShowOrSeatNotFound : HoldOutcome;
}

public abstract record ConfirmOutcome
{
    public sealed record Confirmed(int OrderId) : ConfirmOutcome;
    public sealed record HoldNotFound : ConfirmOutcome;
    public sealed record HoldNoLongerActive : ConfirmOutcome;
}

public abstract record ReleaseOutcome
{
    public sealed record Released : ReleaseOutcome;
    public sealed record HoldNotFound : ReleaseOutcome;
    public sealed record HoldNoLongerActive : ReleaseOutcome;
}
