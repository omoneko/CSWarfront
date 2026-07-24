namespace CSWarfront.Core
{
    public class Faction
    {
        public byte Id { get; private set; }
        public string Name { get; set; }
        public float Treasury { get; private set; }
        public ushort? HomeBaseId { get; set; }
        public bool IsPlayer { get; set; }
        public bool Eliminated { get; set; }

        public Faction(byte id, string name) { Id = id; Name = name; }

        public void AddTreasury(float amount) { if (amount > 0f) Treasury += amount; }

        public bool TrySpend(float amount)
        {
            if (amount < 0f || Treasury < amount) return false;
            Treasury -= amount;
            return true;
        }
    }
}
