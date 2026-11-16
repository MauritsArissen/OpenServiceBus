using OpenServiceBus.Core.Storage;

namespace OpenServiceBus.Core.Tests;

public class LockDeadlinesTests
{
    private static readonly DateTimeOffset Base = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

    [Fact]
    public void Advance_CandidateLaterAtMillisecondGranularity_ReturnsCandidate()
    {
        var candidate = Base.AddMilliseconds(5);

        LockDeadlines.Advance(Base, candidate).ShouldBe(candidate);
    }

    [Fact]
    public void Advance_CandidateOnTheSameMillisecond_ReturnsPreviousPlusOneMillisecond()
    {
        var candidate = Base.AddTicks(4000);

        LockDeadlines.Advance(Base, candidate).ShouldBe(Base.AddMilliseconds(1));
    }

    [Fact]
    public void Advance_CandidateEarlierThanPrevious_ReturnsPreviousPlusOneMillisecond()
    {
        var candidate = Base.AddMilliseconds(-30);

        LockDeadlines.Advance(Base, candidate).ShouldBe(Base.AddMilliseconds(1));
    }

    [Fact]
    public void ToWire_TruncatesToMillisecondAndKeepsOrderingAfterAdvance()
    {
        var previous = Base.AddTicks(9000);
        var advanced = LockDeadlines.Advance(previous, Base.AddTicks(4000));

        LockDeadlines.ToWire(previous).Ticks.ShouldBe(Base.UtcDateTime.Ticks);
        LockDeadlines.ToWire(advanced).ShouldBeGreaterThan(LockDeadlines.ToWire(previous));
        LockDeadlines.ToWire(advanced).Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Fact]
    public void ToWire_SubMillisecondGapWithinSameMillisecondThenAdvance_IsStrictlyIncreasing()
    {
        var previous = Base.AddTicks(9999);
        var candidate = Base.AddTicks(9999 + 10);
        var advanced = LockDeadlines.Advance(previous, candidate);

        LockDeadlines.ToWire(advanced).ShouldBeGreaterThan(LockDeadlines.ToWire(previous));
    }
}
