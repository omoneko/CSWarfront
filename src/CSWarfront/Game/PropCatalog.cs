using System;
using System.Collections.Generic;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// ロード済み PropInfo（プロップ、Workshopのサブスクライブ済みアセット含む）を名前で列挙・解決する
    /// ヘルパー（Task36）。UnitMeshSource と同じ方針で、借用するのは m_mesh のみ（AI等の概念はPropInfoには
    /// 存在しないため無関係だが、マテリアルもCS側のものは借用しない＝UnitMaterialFactory方針を踏襲）。
    /// PrefabCollection&lt;PropInfo&gt;.LoadedCount/GetLoaded/FindLoaded、PropInfo.m_mesh/m_material/
    /// m_isCustomContent は Assembly-CSharp.dll をリフレクションで検証済み（.superpowers/sdd/task-36-report.md 参照）。
    /// メインスレッド専用（PrefabCollectionアクセスを伴う）。
    /// </summary>
    internal static class PropCatalog
    {
        private struct Entry
        {
            public string Name;
            public bool IsCustomContent;
        }

        // 全プレハブ走査結果のキャッシュ。null は「未スキャン」を表すセンチネル（0件スキャン済みと区別する）。
        // 走査は高コストなため、Rescan() が明示的に呼ばれたときだけ（＝UIパネルを開いたときだけ）行う。
        private static List<Entry> _all;

        private static bool _loggedScanOnce;

        /// <summary>現在の走査結果を破棄し、次回 GetNames 呼び出し時に再走査させる。
        /// AssetAssignPanel が開かれるたびに呼ぶことで「今サブスクライブしているプロップ」を反映する。</summary>
        public static void Rescan()
        {
            _all = null;
        }

        /// <summary>
        /// 使えるメッシュ(m_mesh)を持つプロップの名前一覧を返す（名前昇順ソート）。
        /// customOnly=true の場合 m_isCustomContent==true（Workshop/カスタムコンテンツ）のみに絞る。
        /// filter が非空の場合、大文字小文字を無視した部分一致でさらに絞る。
        /// </summary>
        public static List<string> GetNames(bool customOnly, string filter)
        {
            EnsureScanned();

            List<string> result = new List<string>();
            if (_all == null) return result;

            for (int i = 0; i < _all.Count; i++)
            {
                Entry e = _all[i];
                if (customOnly && !e.IsCustomContent) continue;
                if (!string.IsNullOrEmpty(filter) &&
                    e.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                result.Add(e.Name);
            }

            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>名前からメッシュを直接解決する（走査キャッシュに依存しない、都度FindLoadedする単発の
        /// 名前引き。UnitMeshSourceがユニットのビジュアル生成時に呼ぶ経路で、キャッシュの有無で
        /// バインディング変更が隠れることを避けるため意図的にキャッシュしない）。</summary>
        public static bool TryGetMesh(string propName, out Mesh mesh)
        {
            mesh = null;
            if (string.IsNullOrEmpty(propName)) return false;

            try
            {
                PropInfo info = PrefabCollection<PropInfo>.FindLoaded(propName);
                if (info == null || info.m_mesh == null) return false;
                mesh = info.m_mesh;
                return true;
            }
            catch (Exception e)
            {
                ModConfig.LogError("PropCatalog.TryGetMesh(" + propName + ") error: " + e);
                mesh = null;
                return false;
            }
        }

        private static void EnsureScanned()
        {
            if (_all != null) return;

            List<Entry> list = new List<Entry>();
            try
            {
                int count = PrefabCollection<PropInfo>.LoadedCount();
                for (uint i = 0; i < (uint)count; i++)
                {
                    PropInfo info = PrefabCollection<PropInfo>.GetLoaded(i);
                    if (info == null) continue;
                    if (info.m_mesh == null) continue; // 使えるメッシュを持つ物だけを候補にする
                    string name = info.name;
                    if (string.IsNullOrEmpty(name)) continue;

                    list.Add(new Entry { Name = name, IsCustomContent = info.m_isCustomContent });
                }

                if (!_loggedScanOnce)
                {
                    _loggedScanOnce = true;
                    ModConfig.Log("PropCatalog: 走査完了、mesh有りプロップ " + list.Count + " 件");
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("PropCatalog.EnsureScanned error: " + e);
            }

            _all = list;
        }
    }
}
