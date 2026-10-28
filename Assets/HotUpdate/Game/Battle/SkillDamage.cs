namespace AutoChess.Game
{
    /// <summary>
    /// 技能伤害纯函数（决策 #15 口径）。
    ///
    /// 分工（两段，别混）：
    ///   本函数只算"技能自身的基础值 + 系数段"——还没过抗性、暴击、增伤、减伤；
    ///   算出的整数作为 DamageInput.BaseDamage，交给 DamageFormula 走与普攻完全相同的乘区管线。
    ///   所以技能与普攻共享暴击/增伤/抗性/减伤/穿透的全部结算，技能不会变成第二套伤害体系。
    ///
    /// 取整纪律：本函数末尾取整一次（乘区里的中间值不取整），
    ///   DamageFormula 拿到的是整数基础值（和普攻的 AttackPower 同级），后续乘区再在它自己末尾取整一次。
    ///
    /// 纯函数：同技能 + 同星级 + 同面板 → 同伤害，无随机、无状态。
    /// </summary>
    public static class SkillDamage
    {
        /// <summary>
        /// 技能伤害 = 基础值(按星级) + 法强系数 × 法强/100 + 攻击力系数 × 攻击力/100。
        /// 保底 1（与 DamageFormula 的保底纪律一致：技能打不出 0 伤害）。
        /// </summary>
        public static int Compute(in SkillDef skill, int star, in UnitStats attacker)
        {
            float v = skill.BaseDamageForStar(star)
                    + skill.AbilityPowerCoefficient * attacker.AbilityPower / 100f
                    + skill.AttackPowerCoefficient * attacker.AttackPower / 100f;
            return GameMath.Max(1, GameMath.RoundToInt(v));
        }

        /// <summary>
        /// 技能是否可暴击（决策 #10：只有物理伤害可暴击）。
        /// 施法侧据此填 DamageInput.CanCrit——技能表不另设"可暴击"开关，
        /// 否则"物理技能"和"可暴击"两个真相会打架。
        /// </summary>
        public static bool CanCrit(in SkillDef skill) => skill.DamageType == DamageType.Physical;
    }
}
