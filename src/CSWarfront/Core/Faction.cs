namespace CSWarfront.Core
{
    public class Faction
    {
        /// <summary>Task95: 外部襲来イベント専用勢力「Invader」の固定Id。プレイヤー勢力（0..4）の外側に
        /// 置かれた第6の勢力で、(1)RelationMatrix/ThreatRelationsがこのIdを常時Hostile扱いにハードコード
        /// する（Options等でどう操作しても敵対のまま）、(2)FactionStatus.RefreshのEliminated導出対象外
        /// （基地を1つも持たないのが正常状態のため。従来はここでEliminated化→AI進軍対象外→侵攻部隊が
        /// スポーン地点で固まる、が実機バグの根本原因だった）、(3)建設先勢力ドロップダウン・関係設定UI
        /// には登場しない、という特別扱いを受ける。</summary>
        public const byte InvaderFactionId = 5;

        public byte Id { get; private set; }
        public string Name { get; set; }
        public float Treasury { get; private set; }
        public ushort? HomeBaseId { get; set; }
        public bool IsPlayer { get; set; }
        public bool Eliminated { get; set; }

        /// <summary>研究点。撃破報酬（Research.KillReward）や資金投資（Research.TryInvest）で加算され、
        /// Research.TryUnlockNext がTier解禁のコストとして消費する（Task35）。</summary>
        public float ResearchPoints;

        /// <summary>解禁済みの最大生産Tier（1..5）。既定は1（陸上ロスターの最低Tier）。
        /// AiProductionPolicy.Decide（Task46で旧ProductionPlanning.ChooseUnitKeyを置き換え） /
        /// ManualProduction.TryEnqueue はこれを超えるTierのユニットを選択/発注できない（Task35）。</summary>
        public byte UnlockedTier = 1;

        public Faction(byte id, string name) { Id = id; Name = name; UnlockedTier = 1; }

        // --- Task99: 3資源経済（人的資源/生産力。資金=Treasuryは既存プールを流用） ---
        // 産出: 経済tickで基地1km圏のゾーン別発展度から（住宅→Manpower、商業/オフィス→Treasury、
        // 工業→Production、TerritoryIncome.ZonedForBase）。消費: ユニット/補給トラック生産と
        // 補給物資（UnitCosts/ResupplyStep参照。研究・ミサイルは従来どおりTreasury）。

        /// <summary>人的資源（住宅地区の発展度から産出。ユニット生産の人員コスト）。</summary>
        public float Manpower { get; private set; }

        /// <summary>生産力（工業地区の発展度から産出。ユニットの装備コスト・補給物資の原資）。</summary>
        public float Production { get; private set; }

        public void AddTreasury(float amount) { if (amount > 0f) Treasury += amount; }

        public void AddManpower(float amount) { if (amount > 0f) Manpower += amount; }

        public void AddProduction(float amount) { if (amount > 0f) Production += amount; }

        public bool TrySpendManpower(float amount)
        {
            if (amount < 0f || Manpower < amount) return false;
            Manpower -= amount;
            return true;
        }

        public bool TrySpendProduction(float amount)
        {
            if (amount < 0f || Production < amount) return false;
            Production -= amount;
            return true;
        }

        /// <summary>研究点を加算する。非正の値は無視する（AddTreasuryと同じ規約、Task35）。</summary>
        public void AddResearchPoints(float amount) { if (amount > 0f) ResearchPoints += amount; }

        public bool TrySpend(float amount)
        {
            if (amount < 0f || Treasury < amount) return false;
            Treasury -= amount;
            return true;
        }
    }
}
