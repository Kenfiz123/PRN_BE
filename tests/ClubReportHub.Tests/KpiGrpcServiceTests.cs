using KpiGrpcService.Services;

namespace ClubReportHub.Tests;

public sealed class KpiGrpcServiceTests
{
    // KPI rules:
    // APPROVED_REPORT: +50 per approved report
    // ACTIVITY: +5 per activity detail
    // PARTICIPATION: +0.1 per participant
    // REJECTED_REPORT: -10 penalty
    // OVERDUE_REPORT: -20 penalty
    // Floor: minimum score is 0

    [Fact]
    public void CalculateScore_AllZero_ReturnsZero()
    {
        var score = KpiServiceImpl.CalculateScore(0, 0, 0, 0, 0);
        Assert.Equal(0, score);
    }

    [Fact]
    public void CalculateScore_OneApprovedReport_Returns50()
    {
        var score = KpiServiceImpl.CalculateScore(1, 0, 0, 0, 0);
        Assert.Equal(50m, score);
    }

    [Fact]
    public void CalculateScore_MultipleReports_AccumulatesCorrectly()
    {
        // 2 approved * 50 = 100
        var score = KpiServiceImpl.CalculateScore(2, 0, 0, 0, 0);
        Assert.Equal(100m, score);
    }

    [Fact]
    public void CalculateScore_Activities_Adds5PerActivity()
    {
        // 1 approved * 50 + 3 activities * 5 = 50 + 15 = 65
        var score = KpiServiceImpl.CalculateScore(1, 3, 0, 0, 0);
        Assert.Equal(65m, score);
    }

    [Fact]
    public void CalculateScore_Participants_AddsPoint1PerParticipant()
    {
        // 1 approved * 50 + 10 participants * 0.1 = 50 + 1 = 51
        var score = KpiServiceImpl.CalculateScore(1, 0, 10, 0, 0);
        Assert.Equal(51m, score);
    }

    [Fact]
    public void CalculateScore_RejectedReport_Penalizes10()
    {
        // 1 approved * 50 - 1 rejected * 10 = 40
        var score = KpiServiceImpl.CalculateScore(1, 0, 0, 1, 0);
        Assert.Equal(40m, score);
    }

    [Fact]
    public void CalculateScore_OverdueReport_Penalizes20()
    {
        // 1 approved * 50 - 1 overdue * 20 = 30
        var score = KpiServiceImpl.CalculateScore(1, 0, 0, 0, 1);
        Assert.Equal(30m, score);
    }

    [Fact]
    public void CalculateScore_FullScenario_CalculatesCorrectly()
    {
        // 3 approved * 50 = 150
        // 10 activities * 5 = 50
        // 50 participants * 0.1 = 5
        // 1 rejected * -10 = -10
        // 2 overdue * -20 = -40
        // Total = 150 + 50 + 5 - 10 - 40 = 155
        var score = KpiServiceImpl.CalculateScore(3, 10, 50, 1, 2);
        Assert.Equal(155m, score);
    }

    [Fact]
    public void CalculateScore_AllPenaltiesDoesNotGoBelowZero()
    {
        // Only penalties: -10 -20 = -30, floor to 0
        var score = KpiServiceImpl.CalculateScore(0, 0, 0, 1, 1);
        Assert.Equal(0m, score);
    }

    [Fact]
    public void CalculateScore_RoundsToTwoDecimals()
    {
        // 1 approved * 50 + 33 participants * 0.1 = 50 + 3.3 = 53.3
        var score = KpiServiceImpl.CalculateScore(1, 0, 33, 0, 0);
        Assert.Equal(53.3m, score);
    }

    [Theory]
    [InlineData(500, "Excellent")]
    [InlineData(499, "Good")]
    [InlineData(200, "Good")]
    [InlineData(199, "Average")]
    [InlineData(50, "Average")]
    [InlineData(49, "Needs Improvement")]
    [InlineData(0, "Needs Improvement")]
    public void DetermineRating_ReturnsCorrectCategory(decimal score, string expectedRating)
    {
        var rating = KpiServiceImpl.DetermineRating(score);
        Assert.Equal(expectedRating, rating);
    }
}
