using System;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// ユニット同士の戦闘域（State.CombatZones、Task54）付近で、まれに火災や建物崩壊を発生させる
    /// （Task65、ユーザー要望「ユニット同士の戦闘でも、付近にランダムな火災や建物の崩壊が起きるように
    /// してほしいです。頻度はまれでいいので」）。
    ///
    /// スレッド境界: DisasterHelpersはミサイル災害MOD（MissileDisaster.Game.ImpactResolver、
    /// C:\Users\omone\Desktop\G\ミサイル災害プロジェクト\src\MissileDisaster\Game\ImpactResolver.cs）
    /// と同じくsimスレッド専用のAPIのため、CombatRoadBlockerと全く同じスレッド境界に揃える
    /// （MilitaryManager.OnSimTickの_stateLock内から、CS建物バッファへ触れる他のsim専用処理と
    /// 同じ並びで呼ばれる想定）。
    ///
    /// DisasterHelpers.DestroyStuffの使い方はImpactResolver.ApplyBlastからそのまま踏襲する:
    ///   DestroyStuff(seed, area, position, preRadius, totalRadius, removeRadius,
    ///                destroyMin, destroyMax, burnMin, burnMax)
    ///   - destroyMin/destroyMax帯の建物は確率的に倒壊、burnMin/burnMax帯の建物は倒壊ではなく着火する
    ///     （docs/superpowers/plans/2026-07-15-fire-and-contamination.mdで検証済みの挙動＝「延焼帯」）。
    ///   - preRadius/totalRadiusは処理外周（destroyMax/burnMaxの大きい方に合わせないと外側が走査されない
    ///     というImpactResolverの既知の罠を踏襲して回避する）。
    ///   - removeRadius（基礎・道路ごと撤去する強い破壊）は常に0にする：ここでは「はぐれ砲弾」程度の
    ///     小さな巻き添え被害を狙うのであって、ミサイル着弾のような面制圧ではないため。
    ///   - DisasterHelpers.MakeCrater（地形のクレーター化）は一切呼ばない：地形変形はミサイル級の
    ///     演出であり、通常戦闘の巻き添えとしては大げさすぎるため意図的に対象外にした。
    ///   - seed引数自体（DestroyStuff内部の建物選択用）はImpactResolverと同じくSimulationManagerの
    ///     乱数器から取る（これはCS APIが要求する内部引数であり、下記の「発生するか/どこで発生するか」
    ///     の決定にはSystem.Randomはおろかこの乱数器すら使わない＝別の関心事）。
    /// </summary>
    internal static class CombatCollateral
    {
        /// <summary>戦闘域ごとの抽選間隔（ゲーム内時間）。CombatRoadBlocker.BlockUpdateIntervalHoursと
        /// 同じ「間引く」パターン（毎tick全域を評価しない）。</summary>
        public const float CollateralCheckIntervalHours = 0.5f;

        /// <summary>1回の抽選（1戦闘域×1チェック）あたり火災が発生する確率。「まれ」の要件どおり低い値。</summary>
        public const float FireChancePerCheck = 0.06f;

        /// <summary>1回の抽選あたり建物崩壊が発生する確率。火災よりさらにまれ。</summary>
        public const float CollapseChancePerCheck = 0.015f;

        /// <summary>ゲーム内1日(24h)あたりに許容する巻き添えイベント（火災+崩壊の合計）の上限。
        /// 複数の戦闘域が同時に存在する多正面戦でもログ・処理コストが際限なく増えないようにする
        /// 安全弁（CombatZoneTracker.MaxZonesと同じ思想の防御的上限）。</summary>
        public const int MaxEventsPerDay = 6;

        private const float HoursPerDay = 24f;

        /// <summary>火災（延焼帯のみ・倒壊なし）の半径。「はぐれ砲弾で近くの建物が燃え出す」程度に
        /// 小さく抑える（MissileDisasterの通常弾頭BurnRadius=40よりずっと小さい値を意図的に選んだ。
        /// あちらは着弾そのものの被害、こちらは戦闘の巻き添えという位置づけの違いのため）。</summary>
        private const float FireBurnRadius = 22f;

        /// <summary>建物崩壊（倒壊帯のみ・延焼なし）の半径。「1棟が巻き込まれる」程度に小さく抑え、
        /// 砲撃（bombardment）ではなく「はぐれ弾」に見えるようにする（要件どおり）。</summary>
        private const float CollapseDestroyRadius = 14f;

        private static float _checkAccum;
        private static float _dayAccum;
        private static int _eventsToday;
        private static uint _checkCounter;

        /// <summary>
        /// MilitaryManager.OnSimTickから毎tick呼ばれる（_stateLock内、CombatRoadBlocker.Advanceと
        /// 同じ並び位置）。CollateralCheckIntervalHoursごとにのみ実際の抽選を行う。例外は外へ伝播せず
        /// ログのみに留める（CombatRoadBlocker.Advanceと同じ方針＝simループを絶対に止めない）。
        /// </summary>
        public static void Advance(WarState state, float dt)
        {
            try
            {
                _dayAccum += dt;
                if (_dayAccum >= HoursPerDay)
                {
                    _dayAccum -= HoursPerDay;
                    if (_dayAccum < 0f) _dayAccum = 0f;
                    _eventsToday = 0; // 1日ごとに上限をリセットする
                }

                _checkAccum += dt;
                if (_checkAccum < CollateralCheckIntervalHours) return;
                _checkAccum -= CollateralCheckIntervalHours;
                if (_checkAccum < 0f) _checkAccum = 0f;

                var zones = state.CombatZones.Zones;
                if (zones.Count == 0) return;

                _checkCounter++; // この「チェック」自体を一意に識別する決定的な種

                for (int i = 0; i < zones.Count; i++)
                {
                    if (_eventsToday >= MaxEventsPerDay) return;

                    CombatZone zone = zones[i];

                    // 火災の抽選（zoneIndex+checkCounter+saltを混ぜたハッシュ、System.Random不使用）。
                    if (Roll(i, _checkCounter, 1u) < FireChancePerCheck)
                    {
                        Vector3 pos = RandomPointIn(zone, i, _checkCounter, 2u);
                        IgniteFire(pos);
                        _eventsToday++;
                        if (_eventsToday >= MaxEventsPerDay) return;
                    }

                    // 建物崩壊の抽選（火災と独立、別saltでハッシュを分離）。
                    if (Roll(i, _checkCounter, 3u) < CollapseChancePerCheck)
                    {
                        Vector3 pos = RandomPointIn(zone, i, _checkCounter, 4u);
                        CollapseBuilding(pos);
                        _eventsToday++;
                    }
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatCollateral.Advance exception: " + e);
            }
        }

        /// <summary>延焼帯のみ（destroyMin=destroyMax=0）でDestroyStuffを呼ぶ＝建物は倒壊せず着火する。</summary>
        private static void IgniteFire(Vector3 pos)
        {
            int seed = (int)SimulationManager.instance.m_randomizer.Int32(1000000u);
            DisasterHelpers.DestroyStuff(seed, null, pos, FireBurnRadius, FireBurnRadius, 0f,
                0f, 0f, 0f, FireBurnRadius);
            ModConfig.Log("CombatCollateral: fire at (" + pos.x.ToString("0") + ", " + pos.z.ToString("0") + ")");
        }

        /// <summary>倒壊帯のみ（burnMin=burnMax=0）でDestroyStuffを呼ぶ＝延焼させず建物だけを倒壊させる。
        /// removeRadius=0のため基礎・道路は残る（「1棟がひしゃげる」程度の見た目に留める）。</summary>
        private static void CollapseBuilding(Vector3 pos)
        {
            int seed = (int)SimulationManager.instance.m_randomizer.Int32(1000000u);
            DisasterHelpers.DestroyStuff(seed, null, pos, CollapseDestroyRadius, CollapseDestroyRadius, 0f,
                0f, CollapseDestroyRadius, 0f, 0f);
            ModConfig.Log("CombatCollateral: collapse at (" + pos.x.ToString("0") + ", " + pos.z.ToString("0") + ")");
        }

        /// <summary>戦闘域内の「ランダムだが再現可能」な1点を極座標で返す（System.Random不使用、
        /// 角度・半径ともHash経由の決定的疑似乱数）。</summary>
        private static Vector3 RandomPointIn(CombatZone zone, int zoneIndex, uint checkCounter, uint salt)
        {
            float angle01 = Roll(zoneIndex, checkCounter, salt);
            float radius01 = Roll(zoneIndex, checkCounter, salt + 100u);
            float angle = angle01 * 2f * Mathf.PI;
            float radius = radius01 * zone.Radius;
            float x = zone.Center.X + Mathf.Cos(angle) * radius;
            float z = zone.Center.Z + Mathf.Sin(angle) * radius;
            return new Vector3(x, zone.Center.Y, z);
        }

        /// <summary>決定的な0..1の疑似乱数値。BallisticMissiles.HashSeed/Hash（MurmurHash3のfinalizer
        /// 相当）と同じ手法をここでも独立に採用する（zoneIndex/checkCounter/saltの3値を混ぜる）。
        /// System.Randomは一切使わない：同じ入力（zoneIndex, checkCounter, salt）には常に同じ結果を返す。</summary>
        private static float Roll(int zoneIndex, uint checkCounter, uint salt)
        {
            uint seed = HashSeed((uint)zoneIndex, checkCounter, salt);
            return (seed % 1000000u) / 1000000f;
        }

        private static uint HashSeed(uint zoneIndex, uint checkCounter, uint salt)
        {
            unchecked
            {
                uint h = zoneIndex;
                h = h * 2654435761u + checkCounter;
                h = h * 2654435761u + salt;
                return Hash(h);
            }
        }

        private static uint Hash(uint x)
        {
            unchecked
            {
                x ^= x >> 16;
                x *= 0x7feb352dU;
                x ^= x >> 15;
                x *= 0x846ca68bU;
                x ^= x >> 16;
                return x;
            }
        }

        /// <summary>レベルアンロード時（MilitaryManager.Resetから呼ばれる）：内部の間引き用状態を
        /// クリアする。CS実体には一切触れないため単純な代入のみ（例外にならない）。</summary>
        public static void Reset()
        {
            _checkAccum = 0f;
            _dayAccum = 0f;
            _eventsToday = 0;
            _checkCounter = 0;
        }
    }
}
