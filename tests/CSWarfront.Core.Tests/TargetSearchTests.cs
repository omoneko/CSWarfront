using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

public class TargetSearchTests
{
    private static UnitInstance U(uint id, byte fac, float x)
        => new UnitInstance(id, "Tank_T1", fac, 100f, new WorldPos(x, 0f, 0f));

    [Fact]
    public void Finds_nearest_hostile_in_range()
    {
        var rel = new RelationMatrix(5);
        rel.Set(0, 1, Relation.Hostile);
        var self = U(1, 0, 0f);
        var near = U(2, 1, 30f);
        var far = U(3, 1, 55f);
        var all = new List<UnitInstance> { self, near, far };
        var t = TargetSearch.FindNearestHostile(self, all, rel, 60f);
        Assert.Equal((uint)2, t.InstanceId);
    }

    [Fact]
    public void Ignores_non_hostile_and_out_of_range()
    {
        var rel = new RelationMatrix(5); // 0-1 は Neutral
        var self = U(1, 0, 0f);
        var neutral = U(2, 1, 10f);
        rel.Set(0, 2, Relation.Hostile);
        var hostileFar = U(3, 2, 100f); // 敵対だが射程外
        var all = new List<UnitInstance> { self, neutral, hostileFar };
        Assert.Null(TargetSearch.FindNearestHostile(self, all, rel, 60f));
    }

    [Fact]
    public void Ignores_dead_units()
    {
        var rel = new RelationMatrix(5);
        rel.Set(0, 1, Relation.Hostile);
        var self = U(1, 0, 0f);
        var dead = U(2, 1, 10f); dead.State = UnitState.Dead;
        var all = new List<UnitInstance> { self, dead };
        Assert.Null(TargetSearch.FindNearestHostile(self, all, rel, 60f));
    }
}
