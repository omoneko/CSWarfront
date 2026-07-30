namespace CSWarfront.Core
{
    /// <summary>MovementStepの続き（Task79: 自爆ドローン(UnitCategoryFlags.IsKamikaze)のダイブ移動）。
    /// 500行/ファイルの上限に収めるため、ダイブ先解決とダイブ移動本体だけをこのファイルへ分離した
    /// （MovementStepSea.csと同じpartial classパターン）。</summary>
    public static partial class MovementStep
    {
        /// <summary>Task79: KamikazeStepが書いたロック(TargetId/TargetThreatId/TargetBaseId)から、
        /// ダイブ先の「目標の現在位置」を解決する。ロックしたユニットが撃破/消滅していた、脅威が
        /// 撃破(IsDefeated)されていた、または基地がもはや敵対所有でなくなっていた（占領猶予中の基地は
        /// KamikazeStepがそもそもロックしないため、猶予再突入によるnull化は起こらない）場合はnullを
        /// 返す（このtickはダイブせず、呼び出し元が通常のResolveDomainObjectiveへフォールスルーする）。
        /// TargetId/TargetThreatId/TargetBaseIdの全てがnull（まだ何もロックしていない）の場合もnullを
        /// 返す。</summary>
        private static WorldPos? ResolveKamikazeTarget(WarState state, UnitInstance u)
        {
            if (u.TargetId.HasValue)
            {
                UnitInstance target = state.FindUnit(u.TargetId.Value);
                if (target != null && target.IsAlive) return target.Position;
                return null;
            }

            if (u.TargetThreatId.HasValue)
            {
                for (int i = 0; i < state.Threats.Count; i++)
                {
                    ExternalThreat threat = state.Threats[i];
                    if (threat.Id == u.TargetThreatId.Value)
                        return threat.IsDefeated ? (WorldPos?)null : threat.Position;
                }
                return null;
            }

            if (u.TargetBaseId.HasValue)
            {
                for (int i = 0; i < state.Bases.Count; i++)
                {
                    MilitaryBase b = state.Bases[i];
                    if (b.BaseId != u.TargetBaseId.Value) continue;
                    if (b.OwnerFactionId == null || b.OwnerFactionId.Value == u.FactionId) return null;
                    if (!state.Relations.Get(u.FactionId, b.OwnerFactionId.Value).IsHostile()) return null;
                    return b.Position;
                }
                return null;
            }

            return null;
        }

        /// <summary>Task79: 自爆ドローンのダイブ移動。目標の現在位置(target)へ向けて3D（X/Y/Z全て）で
        /// 直線的に突入する。CruiseAltitude（巡航高度の維持）・IHeightSampler（地表スナップ）・
        /// IWaterSampler（水域チェック）のいずれにも一切触れない（「ignoring cover/paths」という
        /// 仕様どおり、地形・障害物を無視して最短距離で目標へ向かう）。速度はtype.Speed（呼び出し元の
        /// stepLen=Speed×dt）にDiveSpeedMultiplierを掛けた実効値を使う。</summary>
        private static void AdvanceKamikaze(UnitInstance u, float stepLen, WorldPos target)
        {
            float diveStepLen = stepLen * DiveSpeedMultiplier;
            float dist = u.Position.DistanceTo(target);
            if (dist <= diveStepLen || dist <= 0.01f)
            {
                u.Position = target;
                return;
            }

            float t = diveStepLen / dist;
            float nx = u.Position.X + (target.X - u.Position.X) * t;
            float ny = u.Position.Y + (target.Y - u.Position.Y) * t;
            float nz = u.Position.Z + (target.Z - u.Position.Z) * t;
            u.Position = new WorldPos(nx, ny, nz);
        }
    }
}
