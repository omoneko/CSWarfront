using System;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task49: ユニット上の勢力アイコン（小さな球、CSの犯罪/火事アイコンのような振る舞い）向けの
    /// UnitVisuals 追加メンバー。UnitVisuals.cs の500行制限のため分離した partial class
    /// （Task34の MilitaryManagerManualProduction / Task48の MilitaryManagerUnitCommands と同じ方針）。
    /// UnitVisuals.cs 側で宣言された private ネスト型 VisualEntry や private const（IconGapAboveMesh等）は
    /// 同じ partial class の一部としてこちらからもそのままアクセスできる。
    ///
    /// 全メソッドはメインスレッド専用（Unity GameObject/Material APIのため）。UnitVisuals.Sync から
    /// スナップショット1件ごとに（生成/移動どちらの経路でも）毎フレーム呼ばれる想定。
    /// </summary>
    public static partial class UnitVisuals
    {
        // Task49: CSの犯罪/火事アイコンのように、カメラ距離に関わらず見かけの大きさをほぼ一定に保つための
        // パラメータ。worldSize = distance * IconApparentSizeFactor をワールド単位のスケールとして毎フレーム
        // 設定する（透視投影では screenSize ∝ worldSize / distance のため、worldSize を distance に比例させると
        // screenSize が一定に近づく）。MinIconWorldSize/MaxIconWorldSizeはその安全域クランプ:
        // 至近距離で0に潰れて消えないための下限、超望遠（大ズームアウト）で際限なく巨大化しないための上限。
        private const float IconApparentSizeFactor = 0.02f;
        private const float MinIconWorldSize = 2f;
        private const float MaxIconWorldSize = 20f;

        /// <summary>
        /// 勢力アイコン（小さな球）を毎フレーム同期する。WarfrontSettings.ShowFactionIconsがOFFなら
        /// 既存のアイコンを破棄してnullに戻す。ONで未生成なら遅延生成し、生成済みならカメラ距離に応じて
        /// スケールを更新する（常にカメラへ正対する必要はない: 球はどの角度から見ても同じ見た目のため、
        /// ビルボード回転は不要）。fromAssignedProp（割り当て済みアセット）ユニットも区別せず同じ経路で
        /// 扱う（要件: 両方で動作する）。
        /// </summary>
        private static void UpdateFactionIcon(VisualEntry entry, byte factionId, Camera mainCamera)
        {
            if (entry == null || entry.GameObject == null) return;

            if (!WarfrontSettings.ShowFactionIcons)
            {
                if (entry.Icon != null)
                {
                    UnityEngine.Object.Destroy(entry.Icon);
                    entry.Icon = null;
                }
                return;
            }

            if (entry.Icon == null)
            {
                entry.Icon = CreateFactionIcon(entry.GameObject, factionId, entry.IconLocalHeightY);
                if (entry.Icon == null) return; // マテリアル解決不能等。CreateFactionIcon内でログ済み。次フレームまた試みる。
            }

            if (mainCamera == null) return; // スケール計算不能。既存スケールのまま維持（見た目は生成時点のまま）。

            Vector3 iconWorldPos = entry.Icon.transform.position;

            // Task49: 画面外ならCPU側の距離計算・スケール更新をスキップする（描画自体はUnityの
            // フラスタムカリングで既に省かれているため、GPU負荷はこのチェックの有無で変わらない）。
            Vector3 viewportPoint = mainCamera.WorldToViewportPoint(iconWorldPos);
            bool onScreen = viewportPoint.z > 0f
                && viewportPoint.x > -0.1f && viewportPoint.x < 1.1f
                && viewportPoint.y > -0.1f && viewportPoint.y < 1.1f;
            if (!onScreen) return;

            float distance = Vector3.Distance(iconWorldPos, mainCamera.transform.position);
            float worldSize = Mathf.Clamp(distance * IconApparentSizeFactor, MinIconWorldSize, MaxIconWorldSize);
            entry.Icon.transform.localScale = new Vector3(worldSize, worldSize, worldSize);
        }

        /// <summary>
        /// 勢力アイコン用の球を生成する（メインスレッド専用）。CombatFx.CreateSmallSphereと同じ手法:
        /// プリミティブ球のColliderはraycast/クリック選択を邪魔しないよう無効化するのみで破棄はしない
        /// （既存のクリック当たり判定は別途マーカー/ルートのColliderが担う）。マテリアルは
        /// UnitMaterialFactory.TryGetFactionMaterialを再利用し、既存の勢力色（本体マテリアルや
        /// マーカー立方体と同じ配色）と一致させる。失敗時はnullを返す（呼び出し側は次フレーム再試行）。
        /// </summary>
        private static GameObject CreateFactionIcon(GameObject parent, byte factionId, float localHeightY)
        {
            try
            {
                Material material;
                if (!UnitMaterialFactory.TryGetFactionMaterial(factionId, out material)) return null;

                GameObject icon = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Collider col = icon.GetComponent<Collider>();
                if (col != null) col.enabled = false;

                icon.name = "CSWarfrontFactionIcon";
                icon.transform.SetParent(parent.transform, false);
                icon.transform.localPosition = new Vector3(0f, localHeightY, 0f);
                icon.transform.localScale = new Vector3(MinIconWorldSize, MinIconWorldSize, MinIconWorldSize); // 次のUpdateFactionIconで距離に応じて即座に補正される

                Renderer renderer = icon.GetComponent<Renderer>();
                if (renderer != null) renderer.sharedMaterial = material;

                return icon;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitVisuals.CreateFactionIcon error: " + e);
                return null;
            }
        }
    }
}
