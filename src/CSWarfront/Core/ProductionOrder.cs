namespace CSWarfront.Core
{
    public class ProductionOrder
    {
        public string TypeKey;
        public float Cost;
        public float BuildTime;
        public float Progress; // 0..1
        public ProductionOrder(string typeKey, float cost, float buildTime)
        { TypeKey = typeKey; Cost = cost; BuildTime = buildTime; Progress = 0f; }
    }
}
