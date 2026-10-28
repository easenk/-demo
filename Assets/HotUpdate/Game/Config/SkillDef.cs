using System;

namespace AutoChess.Game
{
    /// <summary>
    /// 技能目标选择规则（v1 只有一种；其余枚举位留空，等内容需要时再加——枚举加值会改序列化，
    /// 所以"以后可能用"的规则不预先占位，避免占位值变成历史包袱）。
    /// </summary>
    public enum SkillTargetRule
    {
        /// <summary>最近的存活敌人（切比雪夫距离最小，与 CombatSystem 的索敌同套规则；并列取扫描序先到）。</summary>
        NearestEnemy = 0,
    }

    /// <summary>
    /// 技能定义（表的每一行）。readonly struct——定义一旦入库永不修改。
    ///
    /// 伤害口径（决策 #15）：
    ///   技能伤害 = 基础值(按星级) + 法强系数 × 法强 / 100 + 攻击力系数 × 攻击力 / 100。
    ///   法强基准 100（= 技能基准威力；装备/羁绊按百分点增减）。
    ///   系数 150 = 150% 法强。法强系数与攻击力系数都为 0 的技能 = 纯固定值技能。
    ///
    /// 施法消耗口径（2026-10-16 拍板）：
    ///   消耗 = 该单位的蓝条上限（MaxMana），施法后清零——所以"消耗"不进技能表：
    ///   每个单位的施法阈值就是自己的上限（同一个字段），技能表另设一份消耗=两套真相。
    ///   KeepManaAfterCast 是"保留型"预留位（施法后不清零），v1 全为 false。
    ///
    /// 暴击口径（决策 #10）：只有物理伤害可暴击——由 DamageInput.CanCrit 表达，
    ///   技能表不另设"可暴击"开关（那会和伤害类型打架）。
    /// </summary>
    public readonly struct SkillDef
    {
        /// <summary>技能 id（>0；0 = 无技能。开放内容表 → int，封闭集合才用 enum，见 RoleId）。</summary>
        public readonly int Id;

        /// <summary>仅显示用（禁止当键）。</summary>
        public readonly string DisplayName;

        /// <summary>伤害类型（物理走护甲 / 魔法走法抗 / 真实不打折）。</summary>
        public readonly DamageType DamageType;

        /// <summary>1 星基础值（固定加值，不含系数段）。</summary>
        public readonly int BaseDamage1;

        /// <summary>2 星基础值。</summary>
        public readonly int BaseDamage2;

        /// <summary>3 星基础值。</summary>
        public readonly int BaseDamage3;

        /// <summary>法强系数（150 = 150% 法强；0 = 不吃法强）。</summary>
        public readonly int AbilityPowerCoefficient;

        /// <summary>攻击力系数（150 = 150% 攻击力；0 = 不吃攻击力）。</summary>
        public readonly int AttackPowerCoefficient;

        /// <summary>施法距离（格）。够不着的目标 → 不施法、蓝不扣（走原普攻/移动逻辑）。</summary>
        public readonly int CastRange;

        /// <summary>目标选择规则（v1 只有最近敌人）。</summary>
        public readonly SkillTargetRule TargetRule;

        /// <summary>施法后是否保留当前蓝量（保留型技能；v1 全 false = 施法后清零）。</summary>
        public readonly bool KeepManaAfterCast;

        public SkillDef(int id, string displayName, DamageType damageType,
            int baseDamage1, int baseDamage2, int baseDamage3,
            int abilityPowerCoefficient, int attackPowerCoefficient,
            int castRange, SkillTargetRule targetRule, bool keepManaAfterCast)
        {
            Id = id;
            DisplayName = displayName ?? "";
            DamageType = damageType;
            BaseDamage1 = baseDamage1;
            BaseDamage2 = baseDamage2;
            BaseDamage3 = baseDamage3;
            AbilityPowerCoefficient = abilityPowerCoefficient;
            AttackPowerCoefficient = attackPowerCoefficient;
            CastRange = castRange;
            TargetRule = targetRule;
            KeepManaAfterCast = keepManaAfterCast;
        }

        /// <summary>按星级取基础值。星级越界抛异常——星级是受控输入（合成链产出），不是外部数据。</summary>
        public int BaseDamageForStar(int star)
        {
            switch (star)
            {
                case 1: return BaseDamage1;
                case 2: return BaseDamage2;
                case 3: return BaseDamage3;
                default: throw new ArgumentOutOfRangeException(nameof(star), "星级必须为 1~3");
            }
        }
    }
}
