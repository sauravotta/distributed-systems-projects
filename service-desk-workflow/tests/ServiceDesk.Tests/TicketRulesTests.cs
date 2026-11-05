using ServiceDesk.Contracts;

namespace ServiceDesk.Tests;

public sealed class TicketRulesTests
{
    [Theory]
    [InlineData("account")]
    [InlineData("hardware")]
    [InlineData("software")]
    [InlineData("network")]
    [InlineData("other")]
    [InlineData(" NETWORK ")]
    public void IsValid_AcceptsSupportedCategories(string category)
    {
        var request = new CreateTicketRequest(
            "helpdesk@example.com",
            "Cannot connect",
            "The device cannot reach the corporate network.",
            category);

        Assert.True(TicketRules.IsValid(request));
    }

    [Theory]
    [InlineData("invalid-email")]
    [InlineData("")]
    public void IsValid_RejectsInvalidEmail(string email)
    {
        var request = new CreateTicketRequest(email, "Subject", "Description", "network");

        Assert.False(TicketRules.IsValid(request));
    }

    [Fact]
    public void IsValid_RejectsNullEmailFromMalformedJson()
    {
        var request = new CreateTicketRequest(null!, "Subject", "Description", "network");

        Assert.False(TicketRules.IsValid(request));
    }

    [Fact]
    public void IsValid_RejectsUnsupportedCategory()
    {
        var request = new CreateTicketRequest(
            "helpdesk@example.com",
            "Cannot connect",
            "Description",
            "streaming");

        Assert.False(TicketRules.IsValid(request));
    }

    [Fact]
    public void NormalizeCategory_TrimsAndLowercases()
    {
        Assert.Equal("hardware", TicketRules.NormalizeCategory(" Hardware "));
    }
}

public sealed class FaultControllerTests
{
    [Fact]
    public async Task TransientMode_FailsExactlyTwiceBeforeRecovery()
    {
        var faults = new FaultController();
        faults.Configure("transient");

        Assert.Equal("transient_failure", await faults.ApplyAsync(CancellationToken.None));
        Assert.Equal("transient_failure", await faults.ApplyAsync(CancellationToken.None));
        Assert.Null(await faults.ApplyAsync(CancellationToken.None));
        Assert.Equal(new FaultConfiguration("transient", 0), faults.Current);
    }

    [Fact]
    public async Task UnavailableMode_AlwaysReturnsAnOutage()
    {
        var faults = new FaultController();
        faults.Configure("unavailable");

        Assert.Equal("dependency_unavailable", await faults.ApplyAsync(CancellationToken.None));
        Assert.Equal("dependency_unavailable", await faults.ApplyAsync(CancellationToken.None));
    }

    [Fact]
    public void Configure_RejectsUnknownMode()
    {
        var faults = new FaultController();

        Assert.Throws<ArgumentOutOfRangeException>(() => faults.Configure("forever"));
    }
}
