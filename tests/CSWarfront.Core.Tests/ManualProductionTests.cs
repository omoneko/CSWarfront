using CSWarfront.Core;
using Xunit;

public class ManualProductionTests
{
    private static WarState WithBase(float treasury, byte? ownerId = 0)
    {
        var s = new WarState();
        LandUnitRoster.RegisterAll(s.Types);
        if (ownerId.HasValue)
        {
            var f = new Faction(ownerId.Value, "Red");
            f.AddTreasury(treasury);
            s.Factions.Add(f);
        }
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = ownerId;
        s.Bases.Add(b);
        return s;
    }

    // --- TryEnqueue ---

    [Fact]
    public void TryEnqueue_Ok_appends_order_and_spends_treasury()
    {
        var s = WithBase(100f); // Infantry_T1 costs 20
        QueueResult r = ManualProduction.TryEnqueue(s, 100, "Infantry_T1");

        Assert.Equal(QueueResult.Ok, r);
        Assert.Single(s.Bases[0].Queue);
        Assert.Equal("Infantry_T1", s.Bases[0].Queue[0].TypeKey);
        Assert.Equal(80f, s.Factions[0].Treasury, 3);
    }

    [Fact]
    public void TryEnqueue_BaseNotFound_when_baseId_unknown()
    {
        var s = WithBase(100f);
        QueueResult r = ManualProduction.TryEnqueue(s, 999, "Infantry_T1");
        Assert.Equal(QueueResult.BaseNotFound, r);
        Assert.Empty(s.Bases[0].Queue);
    }

    [Fact]
    public void TryEnqueue_NoOwner_when_base_unowned()
    {
        var s = WithBase(100f, ownerId: null);
        QueueResult r = ManualProduction.TryEnqueue(s, 100, "Infantry_T1");
        Assert.Equal(QueueResult.NoOwner, r);
        Assert.Empty(s.Bases[0].Queue);
    }

    [Fact]
    public void TryEnqueue_UnknownType_when_typeKey_unregistered()
    {
        var s = WithBase(100f);
        QueueResult r = ManualProduction.TryEnqueue(s, 100, "NoSuchUnit_T1");
        Assert.Equal(QueueResult.UnknownType, r);
        Assert.Empty(s.Bases[0].Queue);
    }

    [Fact]
    public void TryEnqueue_NotAffordable_when_treasury_too_low_and_does_not_deduct()
    {
        var s = WithBase(5f); // Infantry_T1 costs 20
        QueueResult r = ManualProduction.TryEnqueue(s, 100, "Infantry_T1");
        Assert.Equal(QueueResult.NotAffordable, r);
        Assert.Empty(s.Bases[0].Queue);
        Assert.Equal(5f, s.Factions[0].Treasury, 3); // untouched
    }

    [Fact]
    public void TryEnqueue_QueueFull_at_ManualQueueCap_not_auto_cap()
    {
        var s = WithBase(10000f);
        // ManualQueueCap = 5, well above ProductionPlanning.QueueCap = 2.
        for (int i = 0; i < MilitaryBase.ManualQueueCap; i++)
        {
            QueueResult ok = ManualProduction.TryEnqueue(s, 100, "Infantry_T1");
            Assert.Equal(QueueResult.Ok, ok);
        }
        Assert.Equal(MilitaryBase.ManualQueueCap, s.Bases[0].Queue.Count);

        QueueResult full = ManualProduction.TryEnqueue(s, 100, "Infantry_T1");
        Assert.Equal(QueueResult.QueueFull, full);
        Assert.Equal(MilitaryBase.ManualQueueCap, s.Bases[0].Queue.Count);
    }

    // --- Task35: TryEnqueue TierLocked ---

    [Fact]
    public void TryEnqueue_TierLocked_when_type_tier_exceeds_UnlockedTier()
    {
        var s = WithBase(10000f); // new Faction defaults to UnlockedTier = 1
        QueueResult r = ManualProduction.TryEnqueue(s, 100, "Tank_T4");

        Assert.Equal(QueueResult.TierLocked, r);
        Assert.Empty(s.Bases[0].Queue);
        Assert.Equal(10000f, s.Factions[0].Treasury, 3); // untouched, nothing was spent
    }

    [Fact]
    public void TryEnqueue_succeeds_when_UnlockedTier_covers_the_types_tier()
    {
        var s = WithBase(10000f);
        s.Factions[0].UnlockedTier = 4;

        QueueResult r = ManualProduction.TryEnqueue(s, 100, "Tank_T4");

        Assert.Equal(QueueResult.Ok, r);
        Assert.Single(s.Bases[0].Queue);
    }

    // --- TryCancelLast ---

    [Fact]
    public void TryCancelLast_Ok_removes_last_order_and_refunds_its_cost()
    {
        var s = WithBase(1000f);
        s.Bases[0].Queue.Add(new ProductionOrder("Tank_T1", 60f, 8f) { Progress = 0.5f }); // in progress
        s.Bases[0].Queue.Add(new ProductionOrder("Infantry_T1", 20f, 4f)); // last, not started

        QueueResult r = ManualProduction.TryCancelLast(s, 100);

        Assert.Equal(QueueResult.Ok, r);
        Assert.Single(s.Bases[0].Queue);
        Assert.Equal("Tank_T1", s.Bases[0].Queue[0].TypeKey); // in-progress order untouched
        Assert.Equal(1020f, s.Factions[0].Treasury, 3); // refunded exactly the last order's cost
    }

    [Fact]
    public void TryCancelLast_BaseNotFound_when_baseId_unknown()
    {
        var s = WithBase(1000f);
        QueueResult r = ManualProduction.TryCancelLast(s, 999);
        Assert.Equal(QueueResult.BaseNotFound, r);
    }

    [Fact]
    public void TryCancelLast_NoOwner_when_base_unowned()
    {
        var s = WithBase(1000f, ownerId: null);
        s.Bases[0].Queue.Add(new ProductionOrder("Infantry_T1", 20f, 4f));
        QueueResult r = ManualProduction.TryCancelLast(s, 100);
        Assert.Equal(QueueResult.NoOwner, r);
        Assert.Single(s.Bases[0].Queue); // untouched
    }

    [Fact]
    public void TryCancelLast_returns_QueueFull_when_queue_empty()
    {
        var s = WithBase(1000f);
        QueueResult r = ManualProduction.TryCancelLast(s, 100);
        Assert.Equal(QueueResult.QueueFull, r);
        Assert.Equal(1000f, s.Factions[0].Treasury, 3);
    }

    // --- Task35: in-progress orders can now be cancelled with a partial refund ---

    [Fact]
    public void TryCancelLast_can_cancel_sole_order_already_in_progress_with_partial_refund()
    {
        var s = WithBase(1000f);
        s.Bases[0].Queue.Add(new ProductionOrder("Tank_T1", 60f, 8f) { Progress = 0.5f });

        QueueResult r = ManualProduction.TryCancelLast(s, 100);

        Assert.Equal(QueueResult.Ok, r);
        Assert.Empty(s.Bases[0].Queue);
        // refund = Cost * (1 - Progress) = 60 * 0.5 = 30
        Assert.Equal(1030f, s.Factions[0].Treasury, 3);
    }

    [Fact]
    public void TryCancelLast_barely_started_order_refunds_almost_the_full_cost()
    {
        var s = WithBase(1000f);
        s.Bases[0].Queue.Add(new ProductionOrder("Tank_T1", 60f, 8f) { Progress = 0.1f });

        QueueResult r = ManualProduction.TryCancelLast(s, 100);

        Assert.Equal(QueueResult.Ok, r);
        Assert.Empty(s.Bases[0].Queue);
        // refund = 60 * (1 - 0.1) = 54
        Assert.Equal(1054f, s.Factions[0].Treasury, 3);
    }

    [Fact]
    public void TryCancelLast_nearly_finished_order_refunds_almost_nothing()
    {
        var s = WithBase(1000f);
        s.Bases[0].Queue.Add(new ProductionOrder("Tank_T1", 60f, 8f) { Progress = 0.95f });

        QueueResult r = ManualProduction.TryCancelLast(s, 100);

        Assert.Equal(QueueResult.Ok, r);
        Assert.Empty(s.Bases[0].Queue);
        // refund = 60 * (1 - 0.95) = 3
        Assert.Equal(1003f, s.Factions[0].Treasury, 3);
    }

    [Fact]
    public void TryCancelLast_can_cancel_sole_order_when_not_yet_started()
    {
        var s = WithBase(1000f);
        s.Bases[0].Queue.Add(new ProductionOrder("Tank_T1", 60f, 8f)); // Progress == 0f by default

        QueueResult r = ManualProduction.TryCancelLast(s, 100);

        Assert.Equal(QueueResult.Ok, r);
        Assert.Empty(s.Bases[0].Queue);
        Assert.Equal(1060f, s.Factions[0].Treasury, 3);
    }

    [Fact]
    public void TryCancelLast_with_three_orders_removes_only_the_last()
    {
        var s = WithBase(1000f);
        s.Bases[0].Queue.Add(new ProductionOrder("Tank_T1", 60f, 8f) { Progress = 0.9f });
        s.Bases[0].Queue.Add(new ProductionOrder("Infantry_T1", 20f, 4f));
        s.Bases[0].Queue.Add(new ProductionOrder("Apc_T1", 45f, 6f));

        QueueResult r = ManualProduction.TryCancelLast(s, 100);

        Assert.Equal(QueueResult.Ok, r);
        Assert.Equal(2, s.Bases[0].Queue.Count);
        Assert.Equal("Tank_T1", s.Bases[0].Queue[0].TypeKey);
        Assert.Equal("Infantry_T1", s.Bases[0].Queue[1].TypeKey);
        Assert.Equal(1045f, s.Factions[0].Treasury, 3);
    }
}
