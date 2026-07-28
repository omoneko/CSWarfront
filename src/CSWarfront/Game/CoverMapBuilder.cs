using System;
using ColossalFramework;
using CSWarfront.Core;
namespace CSWarfront.Game
{
    /// <summary>
    /// CSの建物（BuildingManager）から遮蔽物マップ（Core.CoverMap）を構築する（simスレッド専用、Task44）。
    /// RoadGraphBuilderと同じ供給パターン：simスレッドでCSバッファを読み取り、UnityEngine非依存の
    /// POCOへ詰め替えてWarState.Coverへ渡す。
    ///
    /// 検証済みシグネチャ（Assembly-CSharp.dllをリフレクションで確認、Task44）:
    ///  - BuildingManager.m_buildings: Array16&lt;Building&gt;（フィールドm_bufferはBuilding[]）
    ///  - Building.m_flags: Building.Flags（[Flags]enum、Created/Deleted/Hiddenあり）
    ///  - Building.Info: BuildingInfoプロパティ（getter、BasePlacementWatcherが既に使用）
    ///  - Building.m_position: UnityEngine.Vector3
    ///  - BuildingInfo.m_cellWidth / m_cellLength: 共にSystem.Int32（1セル=8メートル換算）
    ///
    /// Propについて（Task44、要件で許容されている代替案）: PropManager.m_props /
    /// PropInstance.m_flags / PropInstance.Info(PropInfo) / PropInstance.Position は
    /// リフレクションで存在を確認できたが、PropInfoには大きさを表す数値フィールド
    /// （半径/幅/長さ相当）が無く、実際のサイズは UnityEngine.Mesh（m_mesh）の bounds からしか
    /// 求められない。Meshはアセット側のUnityオブジェクトであり、simスレッド専用の「CS実体データ読み取り」
    /// という前提から外れる（本プロジェクトの規約はsimスレッドでのUnityオブジェクト操作を禁止している）。
    /// そのため本タスクではPropは対象外とし、建物のみを遮蔽物として登録する
    /// （仕様書に明記された "props turn out to be impractical" のケースに該当）。
    /// </summary>
    internal static class CoverMapBuilder
    {
        /// <summary>1セルあたりの実寸（メートル、CSの建物グリッド基準）。</summary>
        private const float MetersPerCell = 8f;

        /// <summary>BuildingInfo.m_cellWidth/m_cellLengthが取得できない場合の既定半径。</summary>
        private const float FallbackRadius = 8f;

        /// <summary>登録する遮蔽物の総数上限（Task44）。巨大な都市でも毎tickの遮蔽探索コストを
        /// 一定に保つための防御的キャップ。上限に達したら以降の建物は単純に無視する。</summary>
        public const int MaxCoverPoints = 4000;

        // RoadGraphBuilderと同じ間引きパターン：ビルド失敗ログの連続出力を防ぐ。
        private static bool _failureAlreadyLogged;

        /// <summary>
        /// BuildingManagerの建物バッファからCoverMapを構築する。失敗時はnull（呼び出し側は既存マップを維持すること）。
        /// </summary>
        public static CoverMap Build()
        {
            try
            {
                if (!Singleton<BuildingManager>.exists)
                {
                    if (!_failureAlreadyLogged)
                    {
                        ModConfig.LogError("CoverMapBuilder.Build: BuildingManager not ready; skip");
                        _failureAlreadyLogged = true;
                    }
                    return null;
                }

                Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;

                var map = new CoverMap();
                int accepted = 0;
                int skippedNotCreated = 0;
                int skippedOwnBase = 0;
                int skippedNoInfo = 0;
                int cappedOut = 0;

                for (int i = 0; i < buildings.Length; i++)
                {
                    if (map.Count >= MaxCoverPoints)
                    {
                        cappedOut = buildings.Length - i;
                        break;
                    }

                    Building b = buildings[i];
                    if ((b.m_flags & Building.Flags.Created) == 0)
                    {
                        skippedNotCreated++;
                        continue;
                    }

                    BuildingInfo info = b.Info;
                    if (info == null)
                    {
                        skippedNoInfo++;
                        continue;
                    }

                    // 自軍の軍事基地プレハブは遮蔽物として扱わない（拠点の真上/直近に遮蔽点が立つのは
                    // 意味がなく、BasePlacementWatcherと同じ「参照一致 OR 名前一致」で判定する）。
                    bool isOwnBase = ReferenceEquals(info, WarfrontBasePrefab.Prefab) ||
                        (WarfrontBasePrefab.IsRegistered && info.name == WarfrontBasePrefab.PrefabName);
                    if (isOwnBase)
                    {
                        skippedOwnBase++;
                        continue;
                    }

                    float radius = RadiusFor(info);
                    var pos = b.m_position;
                    map.Add(new WorldPos(pos.x, pos.y, pos.z), radius);
                    accepted++;
                }

                ModConfig.Log("CoverMapBuilder: built points=" + map.Count +
                    " acceptedBuildings=" + accepted +
                    " skippedNotCreated=" + skippedNotCreated +
                    " skippedNoInfo=" + skippedNoInfo +
                    " skippedOwnBase=" + skippedOwnBase +
                    " cappedOut=" + cappedOut +
                    " props=0(not supported, see CoverMapBuilder doc comment)");
                _failureAlreadyLogged = false;
                return map;
            }
            catch (Exception e)
            {
                if (!_failureAlreadyLogged)
                {
                    ModConfig.LogError("CoverMapBuilder.Build exception: " + e);
                    _failureAlreadyLogged = true;
                }
                return null;
            }
        }

        /// <summary>BuildingInfo.m_cellWidth/m_cellLengthからおおよその半径（マップ単位）を導く。
        /// 取得できない/非正の場合はFallbackRadiusにフォールバックする。</summary>
        private static float RadiusFor(BuildingInfo info)
        {
            int cellWidth = info.m_cellWidth;
            int cellLength = info.m_cellLength;
            if (cellWidth <= 0 || cellLength <= 0) return FallbackRadius;

            float widthMeters = cellWidth * MetersPerCell;
            float lengthMeters = cellLength * MetersPerCell;
            // 幅・奥行きそれぞれの半分（=中心から縁までの距離）の平均を「おおよその半径」とする。
            return (widthMeters + lengthMeters) / 4f;
        }
    }
}
