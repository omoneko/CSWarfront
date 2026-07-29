using System;
using System.Reflection;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task58: 他MOD（ゴジラ災害＝GodzillaDisaster、エイリアン侵略＝AlienInvasion。同一作者の別MODで、
    /// 導入されていない場合がある）が生成する怪獣・侵略者を、CSWarfrontの戦闘（Core/ThreatCombatStep）
    /// に「外部脅威」(ExternalThreat)として橋渡しする。simスレッド専用（MilitaryManager.OnSimTickの
    /// _stateLock内から呼ぶこと）。
    ///
    /// 設計方針:
    ///  - 相手MODのアセンブリをビルド時参照しない（csprojに参照を追加しない）。ロード済みアセンブリ
    ///    (AppDomain.CurrentDomain.GetAssemblies())から名前でリフレクション解決する。導入されていない
    ///    環境ではアセンブリが見つからないだけで、エラーにはしない。
    ///  - 型・メンバの解決はプロセス中1回だけ試み、結果（成功/失敗いずれも）をキャッシュする
    ///    （「解決できなかった」事実も含めてキャッシュするため、未導入環境で毎tickリフレクションの
    ///    コストを払わない）。
    ///  - 相手MODはHP/被弾/撃破APIを公開していないため、HPはCSWarfront側(ExternalThreat)が独自に
    ///    持つ。0まで削れたら、相手MODの「撃破/強制despawn」用メソッドを探して呼ぶ:
    ///    Defeat または ForceDespawn（将来追加されるかもしれない、Task58時点では存在しない）を優先し、
    ///    無ければ ResetForNewLevel（ForceCleanup相当）にフォールバックする。どちらが実際に見つかった
    ///    かは解決時に1行だけログする。
    ///  - 何が起きてもゲームループへ例外を投げない（try/catchで握りつぶし、1回だけログしてその
    ///    MODとの橋渡しをセッション中無効化する）。
    /// </summary>
    internal static class ExternalThreatBridge
    {
        /// <summary>他MODの現在状態（生死・位置）をState.Threatsへ反映する間隔（ゲーム内時間）。
        /// 毎tick問い合わせる必要はない＝巨大な怪獣がこの間隔の間だけ位置が古くなっても実害が無い
        /// （RoadGraph/CoverMapの再構築間引きと同じ考え方）。</summary>
        public const float ThreatSyncIntervalHours = 0.1f;

        // HP/装甲テーブル（design指定値、Task58）。相手MODはHPを持たないため、ここがCSWarfront側の
        // 唯一の真実源になる。Radiusは大型の当たり判定を確保するためThreatCombatStep.ThreatArmorと
        // 合わせてチューニングした値（ユニットのRangeへ加算される、Core/ThreatCombatStep参照）。
        //
        // Task64再調整（旧Godzilla20000/Alien8000→65000/26000）: Core/ThreatCombatStep.ThreatArmorを
        // 20→45へ引き上げた上で、Tier5戦車50両編成（DamagePerHit(104,45)=59 * accuracy0.868 ≈ 51.2/h、
        // 合計約2560/h）がおおよそゲーム内1日で仕留められる分量へHPを再設定した:
        //   Godzilla 65000 / 2560 ≈ 25.4h（≈1日強）
        //   Alien    26000 / 2560 ≈ 10.2h（Godzillaの約40%、より短時間で片付く「小型脅威」の位置づけ）
        // 爆撃機・観測支援を受けた砲兵はこれより大幅に速く削れる（詳細はtask-64レポート参照）。
        // 弾道ミサイル(BallisticMissiles.ImpactDamageThreat=2000)も同時に引き上げてあり、5発フル備蓄
        // (10000)はGodzillaの約15%・Alienの約38%に相当する「戦力を補う一撃」に留まる（単発で解決しない）。
        private const float GodzillaMaxHP = 65000f;
        private const float GodzillaRadius = 45f;
        private const float AlienMaxHP = 26000f;
        private const float AlienRadius = 25f;

        // 初期値をThreatSyncIntervalHoursにしておくことで、セッション開始後の最初のOnSimTick呼び出しで
        // 即座に同期する（RoadGraphBuildRetryと同じ「初回は待たない」方針）。
        private static float _accum = ThreatSyncIntervalHours;

        private static readonly MonsterModAdapter _godzilla = new MonsterModAdapter(
            "Godzilla", "GodzillaDisaster", "GodzillaDisaster.Game.GodzillaManager", "TryGetPosition");
        private static readonly MonsterModAdapter _alien = new MonsterModAdapter(
            "Alien", "AlienInvasion", "AlienInvasion.Game.InvasionManager", "TryGetAnyTripodPosition");

        // このブリッジが State.Threats 内で管理しているエントリのId（Godzilla/Alienそれぞれ最大1体。
        // 相手MOD自体がIsActive/TryGetPositionという単一体前提のAPIしか公開していないため、
        // CSWarfront側もそれぞれ1体までしか追跡しない）。0は「現在エントリ無し」を表す。
        private static uint _godzillaThreatId;
        private static uint _alienThreatId;
        private static uint _nextThreatId = 1;

        /// <summary>simスレッド・_stateLock内から毎tick呼ぶ。内部で間引くため、呼び出し側は間隔を
        /// 気にしなくてよい。</summary>
        /// <summary>Task59: GodzillaDisasterが導入されている（アセンブリ+想定メンバが解決できた）か。
        /// OptionsRelationsPageがKAIJU関係の行を表示するかどうかの判定に使う。初回アクセス時に
        /// リフレクション解決を1回だけ行い（未導入なら失敗のみキャッシュ）、以後はその結果を返す。</summary>
        public static bool IsGodzillaModPresent { get { return _godzilla.IsAvailable(); } }

        /// <summary>Task59: 上と同じくAlienInvasionの導入判定（Options画面のAlien関係行の表示用）。</summary>
        public static bool IsAlienModPresent { get { return _alien.IsAvailable(); } }

        public static void Advance(WarState state, float dt)
        {
            _accum += dt;
            if (_accum < ThreatSyncIntervalHours) return;
            _accum = 0f;

            try
            {
                SyncOne(state, _godzilla, "Godzilla", ThreatKind.Kaiju, GodzillaMaxHP, GodzillaRadius, ref _godzillaThreatId);
            }
            catch (Exception e)
            {
                ModConfig.LogError("ExternalThreatBridge: Godzilla 同期中に例外: " + e);
            }

            try
            {
                SyncOne(state, _alien, "Alien", ThreatKind.Alien, AlienMaxHP, AlienRadius, ref _alienThreatId);
            }
            catch (Exception e)
            {
                ModConfig.LogError("ExternalThreatBridge: Alien 同期中に例外: " + e);
            }
        }

        private static void SyncOne(WarState state, MonsterModAdapter adapter, string label, ThreatKind kind, float maxHp,
            float radius, ref uint threatId)
        {
            bool isActive;
            Vector3 pos;
            bool resolved = adapter.TryGetState(out isActive, out pos);

            if (!resolved || !isActive)
            {
                // 未導入/恒久エラー/現在非アクティブ：既存エントリがあれば「消えた」扱いで掃除する。
                RemoveThreat(state, ref threatId);
                return;
            }

            WorldPos worldPos = new WorldPos(pos.x, pos.y, pos.z);
            ExternalThreat threat = threatId != 0 ? FindThreat(state, threatId) : null;

            if (threat == null)
            {
                threat = new ExternalThreat
                {
                    Id = _nextThreatId++,
                    Kind = kind,
                    Position = worldPos,
                    Radius = radius,
                    MaxHP = maxHp,
                    CurrentHP = maxHp
                };
                state.Threats.Add(threat);
                threatId = threat.Id;
                ModConfig.Log("ExternalThreatBridge: " + label + " 出現（HP=" + maxHp.ToString("0") + "）。");
                return;
            }

            threat.Position = worldPos;

            if (threat.IsDefeated)
            {
                float totalDamage = threat.MaxHP; // MaxHPから0まで削られた＝与えた総ダメージ
                adapter.Despawn();
                ModConfig.Log("ExternalThreatBridge: " + label + " 撃退（総ダメージ" + totalDamage.ToString("0") + "）。");
                RemoveThreat(state, ref threatId);
            }
        }

        private static ExternalThreat FindThreat(WarState state, uint id)
        {
            for (int i = 0; i < state.Threats.Count; i++)
                if (state.Threats[i].Id == id) return state.Threats[i];
            return null;
        }

        private static void RemoveThreat(WarState state, ref uint threatId)
        {
            if (threatId == 0) return;
            uint id = threatId; // ref parameters can't be captured by the RemoveAll lambda below
            state.Threats.RemoveAll(t => t.Id == id);
            threatId = 0;
        }

        /// <summary>1つの他MOD（Godzilla or Alien）に対するリフレクション橋渡し。型・メンバの解決は
        /// 1回だけ試み、以後はキャッシュ結果を使い回す。</summary>
        private sealed class MonsterModAdapter
        {
            private readonly string _label;          // ログ用（"Godzilla" / "Alien"）
            private readonly string _assemblyName;    // 例: "GodzillaDisaster"
            private readonly string _typeName;        // 例: "GodzillaDisaster.Game.GodzillaManager"
            private readonly string _positionMethodName; // "TryGetPosition" / "TryGetAnyTripodPosition"

            private bool _resolveAttempted;
            private bool _available;
            private PropertyInfo _isActiveProp;
            private MethodInfo _positionMethod;
            private MethodInfo _despawnMethod;
            private bool _stateErrorLogged;

            public MonsterModAdapter(string label, string assemblyName, string typeName, string positionMethodName)
            {
                _label = label;
                _assemblyName = assemblyName;
                _typeName = typeName;
                _positionMethodName = positionMethodName;
            }

            /// <summary>Task59: このMODが導入されている（型・メンバの解決に成功した）かどうか。
            /// EnsureResolvedは既に「1回だけ試みて以後はキャッシュを返す」実装のため、そのまま公開する。</summary>
            public bool IsAvailable()
            {
                return EnsureResolved();
            }

            /// <summary>IsActive/位置を取得する。戻り値falseは「このMODとの橋渡しが使えない」
            /// （未導入、または解決/呼び出しで恒久的に失敗した）ことを意味し、この場合isActive/positionは
            /// 無視してよい。戻り値trueでもisActive=falseならそのMODは単に現在非アクティブ。</summary>
            public bool TryGetState(out bool isActive, out Vector3 position)
            {
                isActive = false;
                position = default(Vector3);

                if (!EnsureResolved()) return false;

                try
                {
                    isActive = (bool)_isActiveProp.GetValue(null, null);
                    if (!isActive) return true;

                    object[] args = { null };
                    bool found = (bool)_positionMethod.Invoke(null, args);
                    if (found)
                    {
                        position = (Vector3)args[0];
                    }
                    else
                    {
                        // IsActiveなのに位置が取れない：この一瞬は「対象なし」として扱う（次回再試行）。
                        isActive = false;
                    }
                    return true;
                }
                catch (Exception e)
                {
                    if (!_stateErrorLogged)
                    {
                        _stateErrorLogged = true;
                        ModConfig.LogError("ExternalThreatBridge: " + _label +
                            " の状態取得でエラー、以後このセッションでは橋渡しを無効化します: " + e);
                    }
                    _available = false; // 以後 EnsureResolved は再試行せずfalseを返す
                    return false;
                }
            }

            /// <summary>撃破時のdespawn呼び出し（Defeat/ForceDespawnがあればそれ、無ければ
            /// ResetForNewLevel）。橋渡しが無効なら何もしない。例外はログのみで飲み込む
            /// （撃破ログ自体は先に出ているため、despawn呼び出し失敗でゲームを止める理由が無い）。</summary>
            public void Despawn()
            {
                if (!_available || _despawnMethod == null) return;
                try
                {
                    _despawnMethod.Invoke(null, null);
                }
                catch (Exception e)
                {
                    ModConfig.LogError("ExternalThreatBridge: " + _label + " の despawn呼び出し(" +
                        _despawnMethod.Name + ")に失敗: " + e);
                }
            }

            private bool EnsureResolved()
            {
                if (_resolveAttempted) return _available;
                _resolveAttempted = true;

                try
                {
                    Assembly asm = FindAssembly(_assemblyName);
                    if (asm == null)
                    {
                        // 未導入：エラーではない。DIAG同様、常時ログしない（規約）。
                        _available = false;
                        return false;
                    }

                    Type type = asm.GetType(_typeName);
                    if (type == null)
                    {
                        ModConfig.LogError("ExternalThreatBridge: " + _label + " の型が見つかりません(" +
                            _typeName + ")。橋渡しを無効化します。");
                        _available = false;
                        return false;
                    }

                    PropertyInfo isActiveProp = type.GetProperty("IsActive", BindingFlags.Public | BindingFlags.Static);
                    MethodInfo positionMethod = type.GetMethod(_positionMethodName, BindingFlags.Public | BindingFlags.Static);
                    MethodInfo resetMethod = type.GetMethod("ResetForNewLevel", BindingFlags.Public | BindingFlags.Static);

                    if (isActiveProp == null || positionMethod == null || resetMethod == null)
                    {
                        ModConfig.LogError("ExternalThreatBridge: " + _label +
                            " の想定メンバ(IsActive/" + _positionMethodName + "/ResetForNewLevel)が見つかりません。橋渡しを無効化します。");
                        _available = false;
                        return false;
                    }

                    // Defeat/ForceDespawn（将来追加されるかもしれない専用API）を優先し、無ければ
                    // ResetForNewLevelへフォールバックする。ビルド時参照を持たないため、実際に
                    // 何が見つかったかはこの1回のリフレクション解決でしか分からない＝ここで報告する。
                    MethodInfo despawn = type.GetMethod("Defeat", BindingFlags.Public | BindingFlags.Static)
                        ?? type.GetMethod("ForceDespawn", BindingFlags.Public | BindingFlags.Static)
                        ?? resetMethod;

                    _isActiveProp = isActiveProp;
                    _positionMethod = positionMethod;
                    _despawnMethod = despawn;
                    _available = true;

                    ModConfig.Log("ExternalThreatBridge: " + _label + " を検出しました（despawn=" + despawn.Name + "）。");
                    return true;
                }
                catch (Exception e)
                {
                    ModConfig.LogError("ExternalThreatBridge: " + _label + " の解決に失敗: " + e);
                    _available = false;
                    return false;
                }
            }

            private static Assembly FindAssembly(string name)
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    if (assemblies[i].GetName().Name == name) return assemblies[i];
                }
                return null;
            }
        }
    }
}
