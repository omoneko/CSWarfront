using System.Collections.Generic;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// メインスレッド駆動（OnMainVisualUpdate）向けの MilitaryManager 追加メンバー。MilitaryManager.cs
    /// の500行制限のため分離した partial class（Task34のMilitaryManagerManualProduction等と同じ方針）。
    /// _stateLock / State は MilitaryManager.cs 側で宣言された private static メンバーで、
    /// partial class なのでこちらからもそのままアクセスできる。
    ///
    /// OnMainVisualUpdate は ThreadingExtensionBase.OnUpdate 経由でメインスレッドから呼ばれ、
    /// ユニットの見た目（Unity GameObject）のみを同期する。CS実体（Vehicle/Building等）には
    /// 一切触れない。_stateLock はスナップショットを構築する間だけ保持し、ロックを解放してから
    /// Unity API呼び出しを行う（ロック保持中の重い/ブロッキング処理を避け、simスレッドを待たせないため）。
    /// </summary>
    public static partial class MilitaryManager
    {
        // OnMainVisualUpdate で使い回すスナップショット（GC回避）。メインスレッド専用アクセス。
        private static readonly List<UnitVisualState> _visualSnapshot = new List<UnitVisualState>();

        // Task60: 同上、軍事拠点（BaseVisuals）向けのスナップショット。メインスレッド専用アクセス。
        private static readonly List<BaseVisualState> _baseVisualSnapshot = new List<BaseVisualState>();

        // Task42: OnMainVisualUpdate で使い回す発砲イベントのスナップショット（GC回避）。
        // メインスレッド専用アクセス。State.RecentShotsの内容を_stateLock内でここへコピーしてから、
        // ロック解放後にCombatFx.Spawnへ渡す（UnitVisuals向けの_visualSnapshotと同じパターン）。
        private static readonly List<ShotEvent> _shotSnapshot = new List<ShotEvent>();

        // Task51: 同上、State.RecentKillsの内容を_stateLock内でここへコピーしてから、ロック解放後に
        // CombatFx.SpawnKillSoundsへ渡す（_shotSnapshotと全く同じパターン）。
        private static readonly List<KillEvent> _killSnapshot = new List<KillEvent>();

        // Task63: 同上、State.MissilesInFlight向けのスナップショット（MissileVisuals.Sync用）。
        private static readonly List<MissileVisualState> _missileSnapshot = new List<MissileVisualState>();

        // Task63: 同上、State.RecentImpactsの内容を_stateLock内でここへコピーしてから、ロック解放後に
        // MissileVisuals.HandleImpactsへ渡す（_shotSnapshot/_killSnapshotと全く同じパターン）。
        private static readonly List<MissileImpactEvent> _missileImpactSnapshot = new List<MissileImpactEvent>();

        // Task62: 同上、選択中ユニットの進撃/集結先（UI.OrderDestinationMarkers向け）。
        // UI.UnitBoxSelection.SelectedIds（Game層・main-thread専用の状態）を_stateLock内で参照するのは
        // OnMainVisualUpdate自体がメインスレッド専用のため問題ない（他のGame層main-thread状態と同じ扱い）。
        private static readonly List<UI.OrderDestinationState> _orderMarkerSnapshot = new List<UI.OrderDestinationState>();

        /// <summary>
        /// メインスレッド（ThreadingExtensionBase.OnUpdate経由）：ユニットの見た目（Unity GameObject）
        /// のみを同期する。CS実体（Vehicle/Building等）には一切触れない。
        /// _stateLock はスナップショットを構築する間だけ保持し、ロックを解放してから
        /// UnitVisuals.Sync（Unity API呼び出し）を行う（ロック保持中の重い/ブロッキング処理を避け、
        /// simスレッドを待たせないため）。
        /// </summary>
        public static void OnMainVisualUpdate()
        {
            if (State == null) return;

            // Task94: 外部襲来の発生通知（simスレッドが立てたフラグをメインスレッドで消費）。
            if (_invasionToastPending)
            {
                _invasionToastPending = false;
                UI.CommandToast.Show("Invasion force approaching the city!");
            }

            _visualSnapshot.Clear();
            _baseVisualSnapshot.Clear();
            _shotSnapshot.Clear();
            _killSnapshot.Clear();
            _orderMarkerSnapshot.Clear();
            _missileSnapshot.Clear();
            _missileImpactSnapshot.Clear();
            lock (_stateLock)
            {
                for (int i = 0; i < State.Units.Count; i++)
                {
                    var u = State.Units[i];
                    if (u.State == UnitState.Dead) continue;
                    var type = State.Types.Get(u.TypeKey);
                    _visualSnapshot.Add(new UnitVisualState
                    {
                        InstanceId = u.InstanceId,
                        TypeKey = u.TypeKey,
                        FactionId = u.FactionId,
                        Position = new Vector3(u.Position.X, u.Position.Y, u.Position.Z),
                        AssetPrefabName = type != null ? type.AssetPrefabName : ""
                    });
                }

                // Task62: 選択中ユニット（UI.UnitBoxSelection.SelectedIds）の進撃/集結先を同じロック内で
                // 収集する（M&B風の目的地マーカー、UI.OrderDestinationMarkers向け）。Hold中・目的地未設定の
                // ユニットは対象外（マーカーを出さない＝仕様どおり「目的地が無い」ことを意味する）。
                var selectedIds = UI.UnitBoxSelection.SelectedIds;
                for (int i = 0; i < selectedIds.Count; i++)
                {
                    UnitInstance u = State.FindUnit(selectedIds[i]);
                    if (u == null || !u.IsAlive) continue;

                    if (u.Order == UnitOrder.RallyHold)
                    {
                        if (!u.RallyPoint.HasValue) continue;
                        WorldPos p = u.RallyPoint.Value;
                        _orderMarkerSnapshot.Add(new UI.OrderDestinationState
                        {
                            Position = new Vector3(p.X, p.Y, p.Z),
                            Kind = UI.OrderMarkerKind.Rally
                        });
                    }
                    else if (u.Order != UnitOrder.Hold && u.OrderTargetPos.HasValue)
                    {
                        WorldPos p = u.OrderTargetPos.Value;
                        _orderMarkerSnapshot.Add(new UI.OrderDestinationState
                        {
                            Position = new Vector3(p.X, p.Y, p.Z),
                            Kind = UI.OrderMarkerKind.Advance
                        });
                    }
                }

                // Task60: 軍事拠点も同じロック内でスナップショットを組み立てる。位置(WorldPos)は
                // Core（State.Bases、基地配置時にBasePlacementWatcherが一度だけ記録した不変値
                // ＝拠点は配置後に移動しないため再読込不要）から、向きはBasePlacementWatcher
                // ._baseAngles（simスレッドがCS建物バッファから既に読み取り済みのキャッシュ、
                // このロックと同じ_stateLockで書き込まれるため、ここで読むのは安全）から取る。
                // BuildingManagerバッファへメインスレッドから直接アクセスすることは一切無い
                // （CS実体はsimスレッド専用というルールを維持する）。
                for (int i = 0; i < State.Bases.Count; i++)
                {
                    MilitaryBase b = State.Bases[i];
                    if (b.OwnerFactionId == null) continue; // 未所属の拠点は勢力別割り当ての対象外

                    float angle;
                    if (!BasePlacementWatcher.TryGetAngle(b.BaseId, out angle)) angle = 0f;

                    _baseVisualSnapshot.Add(new BaseVisualState
                    {
                        BaseId = b.BaseId,
                        FactionId = b.OwnerFactionId.Value,
                        Position = new Vector3(b.Position.X, b.Position.Y, b.Position.Z),
                        Angle = angle,
                        Type = b.Type // Task66: 基地種別ごとのモデル割り当てキーを解決するために必要
                    });
                }

                // Task42: 発砲エフェクトも同じロック内でコピーする（State.RecentShotsはsimスレッドが
                // 書き込むトランジェント・バッファのため、ロック外で読むとレースになる）。
                for (int i = 0; i < State.RecentShots.Count; i++)
                    _shotSnapshot.Add(State.RecentShots[i]);

                // Task51: 撃破イベントも同じロック内・同じ理由でコピーする。
                for (int i = 0; i < State.RecentKills.Count; i++)
                    _killSnapshot.Add(State.RecentKills[i]);

                // Task63: 飛翔中ミサイルと着弾/迎撃イベントも同じロック内でスナップショットを組み立てる
                // （State.MissilesInFlight/RecentImpactsはsimスレッドが書き込むため、ロック外で読むとレースになる）。
                for (int i = 0; i < State.MissilesInFlight.Count; i++)
                {
                    MissileInFlight m = State.MissilesInFlight[i];
                    _missileSnapshot.Add(new MissileVisualState
                    {
                        Id = m.Id,
                        FactionId = m.FactionId,
                        From = new Vector3(m.From.X, m.From.Y, m.From.Z),
                        To = new Vector3(m.To.X, m.To.Y, m.To.Z),
                        Progress = m.Progress
                    });
                }
                for (int i = 0; i < State.RecentImpacts.Count; i++)
                    _missileImpactSnapshot.Add(State.RecentImpacts[i]);
            }

            UnitVisuals.Sync(_visualSnapshot);
            BaseVisuals.Sync(_baseVisualSnapshot); // Task60: ロック解放後、Unity操作はここで行う
            UI.OrderDestinationMarkers.Sync(_orderMarkerSnapshot); // Task62: 同上
            MissileVisuals.Sync(_missileSnapshot); // Task63: 同上

            // Task42: Unity操作（GameObject生成/破棄/移動）はロック解放後に行う
            // （UnitVisuals.Syncと同じ規約：ロック保持中にUnity APIを呼ぶとsimスレッドを長時間ブロックしうる）。
            CombatFx.Spawn(_shotSnapshot);
            UnitVisuals.NotifyShots(_shotSnapshot); // Task83: 発砲したユニットは射撃方向を向く
            CombatFx.SpawnKillSounds(_killSnapshot); // Task51: 撃破音（視覚エフェクトは無し）
            KillFx.Spawn(_killSnapshot); // Task65: 撃破爆発エフェクト（音とは別のエフェクト専用クラス、同じカテゴリ判定を共有）
            CombatFx.Update(Time.deltaTime);
            KillFx.Update(Time.deltaTime);
            BombFx.Update(Time.deltaTime); // Task87: 落下中の爆弾のアニメーション
            AaMissileFx.Update(Time.deltaTime); // Task90: 飛翔中の対空ミサイル（追尾・フレア・回避）

            // Task63: 着弾/迎撃の演出（フラッシュ/爆発+音）と、生存中の演出の実時間更新。
            MissileVisuals.HandleImpacts(_missileImpactSnapshot);
            MissileVisuals.UpdateFx(Time.deltaTime);
        }
    }
}
