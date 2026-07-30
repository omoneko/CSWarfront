using System;
using System.Collections.Generic;
using ColossalFramework.UI;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// ロード済みアセット（プロップ・建物・車両・樹木。Workshopのサブスクライブ済みアセット含む）を
    /// 種類(<see cref="AssetKind"/>)×名前で列挙・解決するヘルパー（Task36でPropCatalogとして導入、
    /// Task41で建物/車両/樹木にも対応するため AssetCatalog へ一般化）。
    ///
    /// 借用するのは常に m_mesh（描画）と m_material.mainTexture（テクスチャ、UnitMaterialFactory経由）
    /// のみで、AIやCS側のMaterialオブジェクトそのものは一切借用しない（UnitMeshSource/UnitMaterialFactory
    /// と同じ方針）。これは4種類とも共通の安全性保証であり、種類を問わず安全にユニットモデルとして
    /// 使える理由でもある: AIをインスタンス化しない（メッシュしか読まない）ため、
    /// 建物AI/車両AI/樹木の成長ロジック等の副作用・クラッシュが原理的に起こらない。
    ///
    /// PropInfo/BuildingInfo/VehicleInfo/TreeInfo の m_mesh は共通の基底クラス(PrefabInfo)には無く
    /// 型ごとに別々に宣言されているため、走査・単発解決とも Scan&lt;T&gt;/TryGetField&lt;T,TResult&gt;
    /// という小さなジェネリックヘルパーに種類ごとのセレクタ(Func&lt;T,TResult&gt;)を渡す形で共通化した
    /// （4種類分の重複コードを避けつつ、リフレクションは使わない＝実行時コストなし）。
    /// m_isCustomContent/m_Atlas/m_Thumbnail は全種類とも PrefabInfo 基底で共通のため、サムネイル解決
    /// (TryGetThumbnail)は種類に依らず共通コード1本で済む。
    ///
    /// Assembly-CSharp.dll をリフレクションで検証済み（.superpowers/sdd/task-41-report.md 参照）:
    ///   PropInfo.m_mesh/m_material                      … 直接宣言
    ///   BuildingInfo.m_mesh/m_material                   … BuildingInfoBase（基底）で宣言
    ///   VehicleInfo.m_mesh/m_material                    … VehicleInfoBase（基底）で宣言
    ///   TreeInfo.m_mesh/m_material                       … 直接宣言（m_lodMeshは無いため使わない）
    ///   PrefabInfo.m_isCustomContent/m_Atlas/m_Thumbnail … 4種類共通の基底
    ///   PrefabCollection&lt;T&gt;.LoadedCount()/GetLoaded(uint)/FindLoaded(string) … 4種類とも同一シグネチャ
    ///
    /// メインスレッド専用（PrefabCollectionアクセスを伴う）。
    /// </summary>
    internal static class AssetCatalog
    {
        private struct Entry
        {
            public string Name;
            public bool IsCustomContent;
        }

        private const int KindCount = 4; // AssetKind.Prop/Building/Vehicle/Tree

        // 種類ごとの全プレハブ走査結果キャッシュ。null は「未スキャン」を表すセンチネル。
        // 走査は高コストなため、Rescan() が明示的に呼ばれたとき（＝UIパネルを開いたとき）だけ行う。
        // 建物/車両の総数はプロップより桁違いに多いことがあるため、種類ごとに個別キャッシュし
        // 実際に一覧表示された種類だけを都度スキャンする（4種類まとめての一括スキャンはしない）。
        private static readonly List<Entry>[] _all = new List<Entry>[KindCount];

        /// <summary>全種類の走査結果を破棄し、次回 GetNames 呼び出し時に再走査させる。
        /// AssetAssignPanel が開かれるたびに呼ぶことで「今サブスクライブしているアセット」を反映する。</summary>
        public static void Rescan()
        {
            for (int i = 0; i < KindCount; i++) _all[i] = null;
        }

        /// <summary>
        /// 使えるメッシュ(m_mesh)を持つ、指定種類のアセット名一覧を返す（名前昇順ソート）。
        /// customOnly=true の場合 m_isCustomContent==true（Workshop/カスタムコンテンツ）のみに絞る。
        /// filter が非空の場合、大文字小文字を無視した部分一致でさらに絞る。
        /// </summary>
        public static List<string> GetNames(AssetKind kind, bool customOnly, string filter)
        {
            EnsureScanned(kind);

            List<string> result = new List<string>();
            List<Entry> list = _all[(int)kind];
            if (list == null) return result;

            for (int i = 0; i < list.Count; i++)
            {
                Entry e = list[i];
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
        /// バインディング変更が隠れることを避けるため意図的にキャッシュしない。PropCatalog時代からの
        /// 方針を踏襲）。</summary>
        public static bool TryGetMesh(AssetKind kind, string name, out Mesh mesh)
        {
            mesh = null;
            if (string.IsNullOrEmpty(name)) return false;

            try
            {
                switch (kind)
                {
                    case AssetKind.Prop: return TryGetField<PropInfo, Mesh>(name, p => p.m_mesh, out mesh);
                    case AssetKind.Building: return TryGetField<BuildingInfo, Mesh>(name, b => b.m_mesh, out mesh);
                    case AssetKind.Vehicle: return TryGetField<VehicleInfo, Mesh>(name, v => v.m_mesh, out mesh);
                    case AssetKind.Tree: return TryGetField<TreeInfo, Mesh>(name, t => t.m_mesh, out mesh);
                    default: return false;
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetCatalog.TryGetMesh(" + kind + "," + name + ") error: " + e);
                mesh = null;
                return false;
            }
        }

        /// <summary>名前からメインテクスチャ（m_material.mainTexture）を直接解決する（TryGetMeshと同じく
        /// 都度FindLoadedする単発ルックアップ、キャッシュしない）。UnitMaterialFactory がマテリアル生成の
        /// テクスチャ元として呼ぶ。CS側のMaterialオブジェクトそのものは一切返さない（テクスチャのみ）。</summary>
        public static bool TryGetTexture(AssetKind kind, string name, out Texture texture)
        {
            texture = null;
            if (string.IsNullOrEmpty(name)) return false;

            try
            {
                switch (kind)
                {
                    case AssetKind.Prop: return TryGetField<PropInfo, Texture>(name, p => p.m_material != null ? p.m_material.mainTexture : null, out texture);
                    case AssetKind.Building: return TryGetField<BuildingInfo, Texture>(name, b => b.m_material != null ? b.m_material.mainTexture : null, out texture);
                    case AssetKind.Vehicle: return TryGetField<VehicleInfo, Texture>(name, v => v.m_material != null ? v.m_material.mainTexture : null, out texture);
                    case AssetKind.Tree: return TryGetField<TreeInfo, Texture>(name, t => t.m_material != null ? t.m_material.mainTexture : null, out texture);
                    default: return false;
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetCatalog.TryGetTexture(" + kind + "," + name + ") error: " + e);
                texture = null;
                return false;
            }
        }

        /// <summary>
        /// 指定種類・名前のアセットのサムネイル（PrefabInfo.m_Atlas / m_Thumbnail、4種類共通の基底で
        /// 宣言されているため種類分岐は FindLoadedByKind の中だけで完結する）を解決する。
        /// 多くのアセットはサムネイルを持たない（m_Atlas==null または m_Thumbnail が空）ため、その場合は
        /// false を返す。呼び出し側（AssetAssignPanel）はfalse時にサムネイル用UISpriteを隠すこと。
        /// </summary>
        public static bool TryGetThumbnail(AssetKind kind, string name, out UITextureAtlas atlas, out string spriteName)
        {
            atlas = null;
            spriteName = null;
            if (string.IsNullOrEmpty(name)) return false;

            try
            {
                PrefabInfo info = FindLoadedByKind(kind, name);
                if (info == null || info.m_Atlas == null || string.IsNullOrEmpty(info.m_Thumbnail)) return false;

                atlas = info.m_Atlas;
                spriteName = info.m_Thumbnail;
                return true;
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetCatalog.TryGetThumbnail(" + kind + "," + name + ") error: " + e);
                atlas = null;
                spriteName = null;
                return false;
            }
        }

        private static PrefabInfo FindLoadedByKind(AssetKind kind, string name)
        {
            switch (kind)
            {
                case AssetKind.Prop: return PrefabCollection<PropInfo>.FindLoaded(name);
                case AssetKind.Building: return PrefabCollection<BuildingInfo>.FindLoaded(name);
                case AssetKind.Vehicle: return PrefabCollection<VehicleInfo>.FindLoaded(name);
                case AssetKind.Tree: return PrefabCollection<TreeInfo>.FindLoaded(name);
                default: return null;
            }
        }

        /// <summary>PrefabCollection&lt;T&gt;.FindLoaded(name) で見つけたインスタンスから、selector で
        /// 指定したフィールド（m_mesh または m_material.mainTexture）を読み取る共通ヘルパー。
        /// selector が null を返した場合（メッシュ/テクスチャ無し）は false を返す。</summary>
        private static bool TryGetField<T, TResult>(string name, Func<T, TResult> selector, out TResult result)
            where T : PrefabInfo
            where TResult : class
        {
            result = null;
            T info = PrefabCollection<T>.FindLoaded(name);
            if (info == null) return false;

            TResult value = selector(info);
            if (value == null) return false;

            result = value;
            return true;
        }

        private static void EnsureScanned(AssetKind kind)
        {
            int idx = (int)kind;
            if (idx < 0 || idx >= KindCount) return;
            if (_all[idx] != null) return;

            List<Entry> list;
            switch (kind)
            {
                case AssetKind.Prop: list = Scan<PropInfo>(p => p.m_mesh); break;
                case AssetKind.Building: list = Scan<BuildingInfo>(b => b.m_mesh); break;
                case AssetKind.Vehicle: list = Scan<VehicleInfo>(v => v.m_mesh); break;
                case AssetKind.Tree: list = Scan<TreeInfo>(t => t.m_mesh); break;
                default: list = new List<Entry>(); break;
            }

            // Task66バグ調査で判明: 以前はプロセス内で種類ごとに1回しかログしなかった（_loggedScanOnce
            // ガード）ため、メインメニュー時点（0件）の走査だけが記録され、都市ロード後にRescan()経由で
            // 再走査され直しても件数が更新されたことがログから追えなかった（「割り当てたアセットが反映
            // されない」調査を著しく困難にした）。Rescan()はUIパネルを開いた時だけ呼ばれる低頻度パス
            // （毎フレームではない）なので、常にログしてもスパムにならない。
            ModConfig.Log("AssetCatalog: " + kind + " scan complete, " + list.Count + " with mesh");

            _all[idx] = list;
        }

        /// <summary>PrefabCollection&lt;T&gt; を全走査し、meshSelector が非nullを返すエントリだけを
        /// 候補化する共通実装（種類ごとの差分は meshSelector デリゲートだけ）。</summary>
        private static List<Entry> Scan<T>(Func<T, Mesh> meshSelector) where T : PrefabInfo
        {
            List<Entry> list = new List<Entry>();
            try
            {
                int count = PrefabCollection<T>.LoadedCount();
                for (uint i = 0; i < (uint)count; i++)
                {
                    T info = PrefabCollection<T>.GetLoaded(i);
                    if (info == null) continue;

                    Mesh mesh = meshSelector(info);
                    if (mesh == null) continue; // 使えるメッシュを持つ物だけを候補にする

                    string name = info.name;
                    if (string.IsNullOrEmpty(name)) continue;

                    list.Add(new Entry { Name = name, IsCustomContent = info.m_isCustomContent });
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetCatalog.Scan<" + typeof(T).Name + "> error: " + e);
            }

            return list;
        }
    }
}
