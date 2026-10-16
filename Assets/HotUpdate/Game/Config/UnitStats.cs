namespace AutoChess.Game
{
    /// <summary>
    /// 面板数值快照（十三维，决策#14：6→10 维；批次5.5 蓝条扩容：10→13 维）。
    /// 与 StatKind 十三对十三硬对应（ModifierTests 反射逐项核对）。
    /// 三处语义约定（各由消费者落成测试：暴击/抗性→DamageFormulaTests，钳制→ModifierTests）：
    ///  · 暴击伤害 = 基础伤害 × (1 + CritDamage/100)——"额外百分比"口径，50 = ×1.5
    ///  · 法强基准 100 = 技能基准威力；技能伤害 = 技能基础值 + 系数 × 法强/100（技能在阶段3）
    ///  · 抗性可为负：承伤系数 = 100/(100+抗性)；Armor/MagicResist 永不钳下限
    /// 蓝条三维（批次5.5，2026-10-16 用户定案）：
    ///  · MaxMana = 蓝条上限 = 施法阈值（同一字段，满蓝即施法）；0 = 无蓝条单位（永不施法）
    ///  · "3费以上才有技能"是数据录入纪律（3费+配 MaxMana>0），不写 if(cost) 进战斗循环
    ///  · ManaPerHit 普攻命中回蓝（职业口径：坦5/战刺射10/术7，职业表阶段3 落数据）
    ///  · ManaRegenPerSecond 每秒固定回蓝（术士 2；每 tick = RoundToInt(本值 × 0.5)）
    /// </summary>
    public struct UnitStats
    {
        public int   MaxHp;
        public int   AttackPower;    // 物理攻击力：普攻伤害的唯一来源
        public int   Armor;          // 护甲：减免物理伤害（原名 Defense，双抗并存后改名）
        public int   MagicResist;    // 法术抗性：减免魔法伤害
        public int   AbilityPower;   // 法术强度：基准 100（装备/羁绊按百分点增减）
        public int   AttackRange;    // 射程（格）：1 = 近战
        public float AttackInterval; // 攻击间隔（秒/次）：CombatSystem 的除数，必须 > 0
        public float MoveInterval;   // 移动间隔（秒/格）：CombatSystem 的除数，必须 > 0
        public int   CritChance;     // 暴击率百分数（0~100，出厂基准 5）
        public int   CritDamage;     // 暴击额外伤害百分数（50 = 暴击 ×1.5，出厂基准 50）
        public int   MaxMana;            // 蓝条上限 = 施法阈值（0 = 无蓝条，永不施法）
        public int   ManaPerHit;         // 普攻命中回蓝
        public int   ManaRegenPerSecond; // 每秒固定回蓝（0 = 无被动回蓝）
    }
}
