using System;
using Xunit;

namespace RussianMetadata.Tests;

public sealed class ReconcileCollectionMetadataTaskTests
{
    [Theory]
    [InlineData(1, 15)]
    [InlineData(2, 30)]
    [InlineData(3, 60)]
    [InlineData(8, 1440)]
    [InlineData(20, 1440)]
    public void RetryDelayUsesBoundedExponentialBackoff(int attempts, int expectedMinutes)
    {
        Assert.Equal(
            TimeSpan.FromMinutes(expectedMinutes),
            ReconcileCollectionMetadataTask.RetryDelay(attempts));
    }

    [Fact]
    public void NeedsRefresh_QueuesIdentityAuditOnceEvenForLocalizedCollection()
    {
        var now = DateTime.UtcNow;
        var entry = new CollectionReconciliationEntry();

        Assert.True(ReconcileCollectionMetadataTask.NeedsRefresh(entry, true, true, now));

        entry.IdentityAuditQueued = true;
        Assert.False(ReconcileCollectionMetadataTask.NeedsRefresh(entry, true, true, now));
    }

    [Fact]
    public void NeedsRefresh_KeepsRetryingUnlocalizedCollectionAfterDelay()
    {
        var now = DateTime.UtcNow;
        var entry = new CollectionReconciliationEntry
        {
            IdentityAuditQueued = true,
            NextAttemptUtc = now.AddMinutes(-1),
        };

        Assert.True(ReconcileCollectionMetadataTask.NeedsRefresh(entry, true, false, now));
    }

    [Fact]
    public void NeedsRefresh_AuditsIdentityWhenLocalizationIsDisabled()
    {
        var now = DateTime.UtcNow;
        var entry = new CollectionReconciliationEntry();

        Assert.True(ReconcileCollectionMetadataTask.NeedsRefresh(entry, false, true, now));

        entry.IdentityAuditQueued = true;
        Assert.False(ReconcileCollectionMetadataTask.NeedsRefresh(entry, false, false, now));
    }
}
