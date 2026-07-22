using FluentAssertions;
using ReportService.Services;

namespace ClubReportHub.Tests;

public sealed class FutureEventReportWorkflowTests
{
    private static readonly DateOnly Today = new(2026, 7, 22);

    [Fact]
    public void FutureEventProposal_RequiresExactlyOneFutureEvent()
    {
        FutureEventReportRules.Validate("FUTURE_EVENT", [], Today)
            .Should().Contain("exactly one");

        FutureEventReportRules.Validate("FUTURE_EVENT", [
            new FutureEventDetailInput(Today, "Hackathon", "Plan", "Hall A")
        ], Today).Should().Contain("future");

        FutureEventReportRules.Validate("FUTURE_EVENT", [
            new FutureEventDetailInput(Today.AddDays(1), "Hackathon", "Plan", "Hall A")
        ], Today).Should().BeNull();
    }

    [Fact]
    public void CompletedReport_KeepsItsExistingValidationFlow()
    {
        FutureEventReportRules.Validate("QUARTERLY", [], Today).Should().BeNull();
    }

    [Fact]
    public void FutureEventFinance_IsHiddenFromOrdinaryMembers()
    {
        FutureEventReportRules.CanExposeFinance(isFutureEvent: true, isPrivilegedViewer: false).Should().BeFalse();
        FutureEventReportRules.CanExposeFinance(isFutureEvent: true, isPrivilegedViewer: true).Should().BeTrue();
        FutureEventReportRules.CanExposeFinance(isFutureEvent: false, isPrivilegedViewer: false).Should().BeTrue();
    }
}
