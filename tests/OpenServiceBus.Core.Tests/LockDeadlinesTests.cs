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
}
