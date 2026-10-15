namespace AutoChess.Game
{
    /// <summary>
    /// 面板数值快照（十维，决策#14：6→10 维）。与 StatKind 十对十硬对应（批次3）。
    /// 三处语义约定（各由消费者落成测试：暴击/抗性→DamageFormulaTests 批次5，钳制→ModifierTests 批次4）：
    ///  · 暴击伤害 = 基础伤害 × (1 + CritDamage/100)——"额外百分比"口径，50 = ×1.5
    ///  · 法强基准 100 = 技能基准威力；技能伤害 = 技能基础值 + 系数 × 法强/100（技能在阶段3）
    ///  · 抗性可为负：承伤系数 = 100/(100+抗性)；Armor/MagicResist 永不钳下限
    /// </summary>
    public struct UnitStats
    {
        public int MaxHp;
        public int AttackPower;    // 物理攻击力：普攻伤害的唯一来源
        public int Armor;          // 护甲：减免物理伤害（原名 Defense，双抗并存后改名）
        public int MagicResist;    // 法术抗性：减免魔法伤害
        public int AbilityPower;   // 法术强度：基准 100（装备/羁绊按百分点增减）
        public int AttackRange;    // 射程（格）：1 = 近战
        public float AttackInterval; // 攻击间隔（秒/次）：CombatSystem 的除数，必须 > 0
        public float MoveInterval;   // 移动间隔（秒/格）：CombatSystem 的除数，必须 > 0
        public int CritChance;     // 暴击率百分数（0~100，出厂基准 5）
        public int CritDamage;     // 暴击额外伤害百分数（50 = 暴击 ×1.5，出厂基准 50）
    }
}