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

        public UnitInstance(uint id, string typeKey, byte factionId, float hp, WorldPos pos)
        {
            InstanceId = id; TypeKey = typeKey; FactionId = factionId;
            CurrentHP = hp; Position = pos; State = UnitState.Idle;
        }

        public bool IsAlive => State != UnitState.Dead && CurrentHP > 0f;
    }
}
