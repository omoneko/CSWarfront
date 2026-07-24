using System.Collections.Generic;
namespace CSWarfront.Core
{
    public class UnitTypeRegistry
    {
        private readonly Dictionary<string, UnitType> _byKey = new Dictionary<string, UnitType>();
        public void Register(UnitType t) { _byKey[t.TypeKey] = t; }
        public bool Contains(string typeKey) { return _byKey.ContainsKey(typeKey); }
        public UnitType Get(string typeKey)
        {
            UnitType t; return _byKey.TryGetValue(typeKey, out t) ? t : null;
        }
    }
}
