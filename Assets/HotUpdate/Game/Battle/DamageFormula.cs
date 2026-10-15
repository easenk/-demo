namespace AutoChess.Game
{
    /// <summary>伤害三轨（决策#14）：物理→护甲、魔法→法抗、真实→不打折。</summary>
    public enum DamageType
    {
        Physical,
        Magic,
        True,
    }

    /// <summary>
    /// 一次伤害的全部输入。穿透不是单位属性，是"这一次伤害"的属性（D13）——
    /// 所以它站在这里而不是面板上；阶段3 由"装备/Buff → 伤害参数"聚合层填充。
    /// </summary>
    public struct DamageInput
    {
        public DamageType Type;
        public int BaseDamage;             // 普攻=攻方 AttackPower；技能=技能表算好的值（阶段3）
        public bool CanCrit;                // 普攻恒 true；技能按"仅物理可暴击"取 Type==Physical
        public UnitStats Attacker;            // 已含全部修饰器的有效面板
        public UnitStats Defender;
        public bool IsCrit;                 // 由 CombatSystem 掷骰后传入（D9）
        public int FlatPen;                // 固定穿透（阶段1 恒 0）
        public int PercentPen;             // 百分比穿透 0~100（阶段1 恒 0）
        public float DamageAmpPercent;       // 攻方增伤（独立乘区，阶段1 恒 0）
        public float DamageReductionPercent; // 守方减伤（独立乘区，钳 90%，阶段1 恒 0）
    }

    /// <summary>
    /// 伤害公式（独立纯函数，D7）。金铲铲式乘区管线，顺序即契约（设计文档 5.5）：
    /// 基础值 → 暴击 → 增伤 → 抗性 → 减伤 → 取整一次 → 保底 1。
    /// 取整只做一次、放在最后：中途取整会累积误差，毁掉"同配置同伤害"的精确账目断言。
    /// </summary>
    public static class DamageFormula
    {
        public static int Compute(in DamageInput input)
        {
            // 1. 基础值
            float dmg = input.BaseDamage;

            // 2. 暴击乘区（仅物理可暴击由调用方用 CanCrit 表达；D11）
            if (input.CanCrit && input.IsCrit)
                dmg *= 1f + input.Attacker.CritDamage / 100f;

            // 3. 攻方增伤乘区（阶段1 值恒 0，结构先行）
            dmg *= 1f + input.DamageAmpPercent / 100f;

            // 4+5. 有效抗性与抗性乘区（真实伤害整层跳过）
            if (input.Type != DamageType.True)
            {
                float r = input.Type == DamageType.Physical ? input.Defender.Armor : input.Defender.MagicResist;
                r = (r - input.FlatPen) * (1f - input.PercentPen / 100f);
                dmg *= Coefficient(r);
            }

            // 6. 守方减伤乘区（钳 90% 上限——永不全免）
            dmg *= 1f - GameMath.Clamp(input.DamageReductionPercent, 0f, 90f) / 100f;

            // 7. 取整一次（D2）+ 保底 1
            return GameMath.Max(1, GameMath.RoundToInt(dmg));
        }

        /// <summary>
        /// 承伤系数（D6）：100/(100+抗性)，护甲/法抗通用。
        /// 抗性可为负：r&lt;0 走 2−100/(100−r)，趋近上限 200%（−100→150%、−200→167%）。
        /// </summary>
        public static float ResistanceCoefficient(int resistance)
        {
            return Coefficient(resistance);
        }

        // 管线内部版本收 float：穿透削过的有效抗性是小数，公开口只收 int 供曲线查询
        private static float Coefficient(float r)
        {
            if (r >= 0f) return 100f / (100f + r);
            return 2f - 100f / (100f - r);
        }
    }
}