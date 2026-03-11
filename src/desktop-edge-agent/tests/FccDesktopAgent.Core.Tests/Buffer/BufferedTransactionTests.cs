using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer.Entities;
using FluentAssertions;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Buffer;

public sealed class BufferedTransactionTests
{
    [Fact]
    public void NewTransaction_ShouldHavePendingSyncStatus()
    {
        var tx = new BufferedTransaction();
        tx.SyncStatus.Should().Be(SyncStatus.Pending);
    }

    [Fact]
    public void NewTransaction_ShouldHaveNonEmptyId()
    {
        var tx = new BufferedTransaction();
        tx.Id.Should().NotBeNullOrEmpty();
        Guid.TryParse(tx.Id, out _).Should().BeTrue("ID must be a valid UUID v4");
    }

    [Fact]
    public void AmountMinorUnits_ShouldUseLong_NotFloat()
    {
        // Architecture rule #6: NEVER floating point for money
        var tx = new BufferedTransaction { AmountMinorUnits = 12345L };
        tx.AmountMinorUnits.Should().Be(12345L);
        tx.AmountMinorUnits.GetType().Should().Be(typeof(long));
    }

    [Fact]
    public void VolumeMicrolitres_ShouldUseLong_NotFloat()
    {
        // Architecture rule #6: NEVER floating point for volume
        var tx = new BufferedTransaction { VolumeMicrolitres = 50_000_000L };
        tx.VolumeMicrolitres.Should().Be(50_000_000L);
        tx.VolumeMicrolitres.GetType().Should().Be(typeof(long));
    }

    [Fact]
    public void NewTransaction_ShouldHavePendingTransactionStatus()
    {
        var tx = new BufferedTransaction();
        tx.Status.Should().Be(TransactionStatus.Pending);
    }

    [Fact]
    public void PumpNumber_ShouldBeInt_NotString()
    {
        // FCC pump number is an integer, not the old PumpId string
        var tx = new BufferedTransaction { PumpNumber = 3 };
        tx.PumpNumber.Should().Be(3);
        tx.PumpNumber.GetType().Should().Be(typeof(int));
    }
}
