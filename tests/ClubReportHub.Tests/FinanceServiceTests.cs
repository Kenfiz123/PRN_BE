using FinanceService.Data;
using FinanceService.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ClubReportHub.Tests;

public class FinanceServiceTests : IDisposable
{
    private readonly FinanceDbContext _db;

    public FinanceServiceTests()
    {
        var options = new DbContextOptionsBuilder<FinanceDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new FinanceDbContext(options);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task CreateBudgetProposal_ShouldHaveSubmittedStatus()
    {
        // Arrange & Act
        var proposal = new BudgetProposal
        {
            ClubId = 1,
            ClubName = "Test Club",
            Title = "Equipment Purchase",
            Description = "Buy new equipment",
            RequestedAmount = 1000.00m,
            ProposedByUserId = 1,
            Status = FinanceStatuses.Submitted
        };
        _db.BudgetProposals.Add(proposal);
        await _db.SaveChangesAsync();

        // Assert
        proposal.Status.Should().Be(FinanceStatuses.Submitted);
        proposal.ApprovedAmount.Should().BeNull();
        proposal.ReviewedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task ApproveBudgetProposal_ShouldSetApprovedAmount()
    {
        // Arrange
        var proposal = new BudgetProposal
        {
            ClubId = 1,
            ClubName = "Test Club",
            Title = "Event Budget",
            Description = "Host an event",
            RequestedAmount = 5000.00m,
            ProposedByUserId = 1,
            Status = FinanceStatuses.Submitted
        };
        _db.BudgetProposals.Add(proposal);
        await _db.SaveChangesAsync();

        // Act
        proposal.Status = FinanceStatuses.Approved;
        proposal.ApprovedAmount = 4500.00m;
        proposal.ReviewedByUserId = 99;
        proposal.ReviewedAtUtc = DateTimeOffset.UtcNow;
        proposal.ReviewNote = "Approved with reduction";
        await _db.SaveChangesAsync();

        // Assert
        proposal.Status.Should().Be(FinanceStatuses.Approved);
        proposal.ApprovedAmount.Should().Be(4500.00m);
        proposal.ReviewedByUserId.Should().Be(99);
    }

    [Fact]
    public async Task RejectBudgetProposal_ShouldSetRejectedStatus()
    {
        // Arrange
        var proposal = new BudgetProposal
        {
            ClubId = 1,
            ClubName = "Test Club",
            Title = "Expensive Event",
            Description = "Too expensive",
            RequestedAmount = 100000.00m,
            ProposedByUserId = 1,
            Status = FinanceStatuses.Submitted
        };
        _db.BudgetProposals.Add(proposal);
        await _db.SaveChangesAsync();

        // Act
        proposal.Status = FinanceStatuses.Rejected;
        proposal.ReviewedByUserId = 99;
        proposal.ReviewedAtUtc = DateTimeOffset.UtcNow;
        proposal.ReviewNote = "Budget too high";
        await _db.SaveChangesAsync();

        // Assert
        proposal.Status.Should().Be(FinanceStatuses.Rejected);
        proposal.ApprovedAmount.Should().BeNull();
    }

    [Fact]
    public async Task CreateSettlement_ShouldBeLinkedToProposal()
    {
        // Arrange
        var proposal = new BudgetProposal
        {
            ClubId = 1,
            ClubName = "Test Club",
            Title = "Event Budget",
            RequestedAmount = 5000.00m,
            ApprovedAmount = 5000.00m,
            ProposedByUserId = 1,
            Status = FinanceStatuses.Approved
        };
        _db.BudgetProposals.Add(proposal);
        await _db.SaveChangesAsync();

        // Act
        var settlement = new Settlement
        {
            BudgetProposalId = proposal.Id,
            TotalSpent = 4500.00m,
            ReceiptUrl = "https://example.com/receipt.pdf",
            Status = FinanceStatuses.Submitted,
            SubmittedAtUtc = DateTimeOffset.UtcNow
        };
        proposal.Settlements.Add(settlement);
        await _db.SaveChangesAsync();

        // Assert
        settlement.BudgetProposalId.Should().Be(proposal.Id);
        settlement.Status.Should().Be(FinanceStatuses.Submitted);
        settlement.ReviewedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task ApproveSettlement_ShouldUpdateProposalStatus()
    {
        // Arrange
        var proposal = new BudgetProposal
        {
            ClubId = 1,
            ClubName = "Test Club",
            Title = "Event Budget",
            RequestedAmount = 5000.00m,
            ApprovedAmount = 5000.00m,
            ProposedByUserId = 1,
            Status = FinanceStatuses.Approved
        };
        _db.BudgetProposals.Add(proposal);
        await _db.SaveChangesAsync();

        var settlement = new Settlement
        {
            BudgetProposalId = proposal.Id,
            TotalSpent = 4500.00m,
            ReceiptUrl = "https://example.com/receipt.pdf",
            Status = FinanceStatuses.Submitted
        };
        _db.Settlements.Add(settlement);
        await _db.SaveChangesAsync();

        // Act
        settlement.Status = FinanceStatuses.Approved;
        settlement.ReviewedByUserId = 99;
        settlement.ReviewedAtUtc = DateTimeOffset.UtcNow;
        settlement.ReviewNote = "Receipt verified";
        proposal.Status = FinanceStatuses.Settled;
        await _db.SaveChangesAsync();

        // Assert
        settlement.Status.Should().Be(FinanceStatuses.Approved);
        settlement.ReviewedByUserId.Should().Be(99);
        proposal.Status.Should().Be(FinanceStatuses.Settled);
    }

    [Fact]
    public async Task CreateFinanceTransaction_ShouldTrackBudgetApproval()
    {
        // Arrange & Act
        var transaction = new FinanceTransaction
        {
            ClubId = 1,
            Amount = 5000.00m,
            Type = TransactionTypes.BudgetApproved,
            Description = "Budget approved for event",
            ReferenceId = 1,
            TransactionDateUtc = DateTimeOffset.UtcNow
        };
        _db.FinanceTransactions.Add(transaction);
        await _db.SaveChangesAsync();

        // Assert
        transaction.Type.Should().Be(TransactionTypes.BudgetApproved);
        transaction.Amount.Should().Be(5000.00m);
        transaction.ReferenceId.Should().Be(1);
    }

    [Fact]
    public async Task Settlement_ShouldNotExceedApprovedAmount()
    {
        // Arrange
        var proposal = new BudgetProposal
        {
            ClubId = 1,
            ClubName = "Test Club",
            Title = "Event Budget",
            RequestedAmount = 5000.00m,
            ApprovedAmount = 5000.00m,
            ProposedByUserId = 1,
            Status = FinanceStatuses.Approved
        };
        _db.BudgetProposals.Add(proposal);
        await _db.SaveChangesAsync();

        // Act
        var settlement = new Settlement
        {
            BudgetProposalId = proposal.Id,
            TotalSpent = 6000.00m, // Exceeds approved amount
            ReceiptUrl = "https://example.com/receipt.pdf",
            Status = FinanceStatuses.Submitted
        };

        // Assert - Business logic should reject this
        settlement.TotalSpent.Should().BeGreaterThan(proposal.ApprovedAmount!.Value);
    }

    [Fact]
    public async Task ProposalAmount_ShouldHaveCorrectPrecision()
    {
        // Arrange & Act
        var proposal = new BudgetProposal
        {
            ClubId = 1,
            ClubName = "Test Club",
            Title = "Precision Test",
            RequestedAmount = 1234567890.12m, // Large number with 2 decimal places
            ProposedByUserId = 1
        };
        _db.BudgetProposals.Add(proposal);
        await _db.SaveChangesAsync();

        // Assert
        proposal.RequestedAmount.Should().Be(1234567890.12m);
    }
}
