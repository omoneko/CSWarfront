using System.Collections.Generic;

namespace CSWarfront.Core
{
    public enum UnitState { Idle, Moving, Engaging, Dead }

    /// <summary>実行時の1体。表現(車両ID)はGame層が別に保持し、ここには論理状態のみ。</summary>
    public class UnitInstance
    {
        public uint InstanceId;
        public string TypeKey;
        public byte FactionId;
        public float CurrentHP;
        public WorldPos Position;
        public UnitState State;
        public uint? TargetId;
        public WorldPos? OrderTargetPos;

        /// <summary>道路経路の残り（実行時のみ・非永続化）。null/空なら直線移動へフォールバック。</summary>
        public List<WorldPos> Path;
        /// <summary>次に目指す Path の要素番号。</summary>
        public int PathIndex;
        /// <summary>この経路が向かう最終目的地。OrderTargetPosが変わったら経路を捨てるための記録。</summary>
        public WorldPos? PathTarget;

        /// <summary>次の経路探索(A*)試行までの残り時間（ゲーム内時間・実行時のみ・非永続化）。
        /// FindPath失敗時に立て、失敗し続けるユニットが毎tick予算を消費するのを防ぐ（Task23レビュー）。</summary>
        public float PathRetryCooldown;

        public UnitInstance(uint id, string typeKey, byte factionId, float hp, WorldPos pos)
        {
            InstanceId = id; TypeKey = typeKey; FactionId = factionId;
            CurrentHP = hp; Position = pos; State = UnitState.Idle;
        }

        public bool IsAlive => State != UnitState.Dead && CurrentHP > 0f;

        public void ClearPath()
        {
            Path = null;
            PathIndex = 0;
            PathTarget = null;
            PathRetryCooldown = 0f;
        }
    }
}
