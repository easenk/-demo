namespace AutoChess.Game
{
    /// <summary>
    /// 面板维度的枚举索引：与 UnitStats 十对十硬对应（ModifierTests 用反射逐项核对）。
    /// 为什么一一对应是硬约束：EffectiveStats（批次4）按 Kind 结算时直接映射到 UnitStats 字段，
    /// 少一个 = 修饰器挂上却静默失效——最贵的一类 bug，用测试焊死。
    /// </summary>
    public enum StatKind
    {
        MaxHp,
        AttackPower,
        Armor,
        MagicResist,
        AbilityPower,
        AttackRange,
        AttackInterval,
        MoveInterval,
        CritChance,
        CritDamage,
    }

    /// <summary>修饰器的两种算法：Flat = 直接加 Value；Percent = 乘 (1 + Value/100)。</summary>
    public enum StatOp
    {
        Flat,
        Percent,
    }

    /// <summary>
    /// 修饰器：Buff/装备/羁绊三个老板共用的最小载体。
    /// 为什么是 struct：同 Kind/Op/Value/SourceId 的两条视为等价 →
    /// "按值删第一条"成为一条朴素可解释的规则（账本对称：加得上的才摘得掉）。
    /// SourceId 是整个系统的枢纽：来源还在修饰器就在，
    /// 来源没了 RemoveModifiersBySource 一次清干净（批次4 在 Unit 账本上实现）。
    /// </summary>
    public struct StatModifier
    {
        public StatKind Kind;
        public StatOp Op;
        public float Value;
        public int SourceId;   // 载体标记：0 = 无来源（测试/临时）

        public StatModifier(StatKind kind, StatOp op, float value, int sourceId = 0)
        {
            Kind = kind;
            Op = op;
            Value = value;
            SourceId = sourceId;
        }
    }
}