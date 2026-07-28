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

        /// <summary>登録済み全UnitTypeを列挙する（Task28: 購入可能な選択肢を洗い出すために使う。
        /// Task46でAiProductionPolicy.ChooseHighestAffordableTierが主な呼び出し元になった）。</summary>
        public IEnumerable<UnitType> All() { return _byKey.Values; }
    }
}
