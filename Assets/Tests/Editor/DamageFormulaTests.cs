using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    /// <summary>
    /// 公式的精确账目断言（设计文档第七节：三类伤害路径与负抗性的逐点核对）。
    /// 每个期望值都手算过，锁的是"同配置同伤害"。
    /// </summary>
    [TestFixture]
    public class DamageFormulaTests
    {
        // 标准物理普攻输入：可暴击、无穿透无乘区
        private static DamageInput Phys(int baseDamage, int armor)
        {
            return new DamageInput
            {
                Type = DamageType.Physical,
                BaseDamage = baseDamage,
                CanCrit = true,
                Defender = new UnitStats { Armor = armor },
            };
        }

        [Test]
        public void ResistanceCurve_NonNegative()
        {
            Assert.AreEqual(1f, DamageFormula.ResistanceCoefficient(0), 0.0001f);
            Assert.AreEqual(0.5f, DamageFormula.ResistanceCoefficient(100), 0.0001f);
            Assert.AreEqual(1f / 3f, DamageFormula.ResistanceCoefficient(200), 0.0001f);
            Assert.AreEqual(0.25f, DamageFormula.ResistanceCoefficient(300), 0.0001f);
        }

        [Test]
        public void ResistanceCurve_Negative_ApproachesButNeverReaches200()
        {
            Assert.AreEqual(4f / 3f, DamageFormula.ResistanceCoefficient(-50), 0.0001f);  // 133%
            Assert.AreEqual(3f / 2f, DamageFormula.ResistanceCoefficient(-100), 0.0001f); // 150%
            Assert.AreEqual(5f / 3f, DamageFormula.ResistanceCoefficient(-200), 0.0001f); // 167%，趋近 200% 永不到
        }

        [Test]
        public void ThreeDamagePaths_PickTheRightResistance()
        {
            // 物理→护甲：14 打 100 甲 = 7；300 法抗不掺和
            var phys = Phys(14, armor: 100);
            phys.Defender.MagicResist = 300;
            Assert.AreEqual(7, DamageFormula.Compute(phys));

            // 魔法→法抗：14 打 100 法抗 = 7；1000 护甲不掺和
            var magic = new DamageInput { Type = DamageType.Magic, BaseDamage = 14, Defender = new UnitStats { MagicResist = 100, Armor = 1000 } };
            Assert.AreEqual(7, DamageFormula.Compute(magic));

            // 真实→跳过抗性层：双抗再高也不打折
            var pure = new DamageInput { Type = DamageType.True, BaseDamage = 14, Defender = new UnitStats { Armor = 1000, MagicResist = 1000 } };
            Assert.AreEqual(14, DamageFormula.Compute(pure));
        }

        [Test]
        public void Crit_MultipliesByCritDamagePercent()
        {
            var input = Phys(14, armor: 100);
            input.Attacker = new UnitStats { CritDamage = 50 };

            input.IsCrit = false;
            Assert.AreEqual(7, DamageFormula.Compute(input)); // 无暴击：14×0.5=7

            input.IsCrit = true;
            Assert.AreEqual(11, DamageFormula.Compute(input)); // 21×0.5=10.5 → 远离零取整 11
        }

        [Test]
        public void CanCritFalse_IgnoresIsCrit()
        {
            var input = new DamageInput
            {
                Type = DamageType.Magic,
                BaseDamage = 14,
                CanCrit = false,      // 仅物理可暴击（D11）：调用方用这个开关表达
                IsCrit = true,        // 即使掷出暴击也不许生效
                Defender = new UnitStats { MagicResist = 0 },
            };
            Assert.AreEqual(14, DamageFormula.Compute(input));
        }

        [Test]
        public void FlatPen_SubtractsBeforeCoefficient()
        {
            var input = Phys(14, armor: 100);
            input.FlatPen = 40;  // 有效抗性 60 → 14×100/160 = 8.75 → 9
            Assert.AreEqual(9, DamageFormula.Compute(input));
        }

        [Test]
        public void PercentPen_HalvesEffectiveResistance()
        {
            var input = Phys(14, armor: 100);
            input.PercentPen = 50;  // 有效抗性 50 → 14×(100/150) = 9.33 → 9
            Assert.AreEqual(9, DamageFormula.Compute(input));
        }

        [Test]
        public void Pens_ComposeFlatThenPercent()
        {
            var input = Phys(14, armor: 100);
            input.FlatPen = 40;
            input.PercentPen = 50;  // (100−40)×0.5 = 30 → 14×(100/130) = 10.77 → 11
            Assert.AreEqual(11, DamageFormula.Compute(input));
        }

        [Test]
        public void DamageAmp_IsIndependentMultiplier()
        {
            var input = Phys(14, armor: 0);
            input.DamageAmpPercent = 30f;  // 14×1.3 = 18.2 → 18
            Assert.AreEqual(18, DamageFormula.Compute(input));
        }

        [Test]
        public void DamageReduction_ClampedAt90Percent()
        {
            var input = Phys(100, armor: 0);
            input.DamageReductionPercent = 40f;
            Assert.AreEqual(60, DamageFormula.Compute(input)); // 100×0.6

            input.DamageReductionPercent = 95f; // 钳到 90：100×0.1=10（不钳会是 5）
            Assert.AreEqual(10, DamageFormula.Compute(input));
        }

        [Test]
        public void AmpAndReduction_ComposeAsSeparateZones()
        {
            var input = Phys(14, armor: 0);
            input.DamageAmpPercent = 30f;
            input.DamageReductionPercent = 40f;
            Assert.AreEqual(11, DamageFormula.Compute(input)); // 14×1.3×0.6 = 10.92 → 11
        }

        [Test]
        public void RoundsOnceAtTheEnd_AndFloorsAtOne()
        {
            // 取整一次的判别用例：10×1.125 = 11.25，×(100/150) = 7.5 → 8
            // （若中途取整：11.25→11，11×0.667=7.33→7——两条路结果不同，锁 8）
            var input = Phys(10, armor: 50);
            input.DamageAmpPercent = 12.5f;
            Assert.AreEqual(8, DamageFormula.Compute(input));

            // 保底 1：抗性再高也不许打出 0
            Assert.AreEqual(1, DamageFormula.Compute(Phys(1, armor: 1000)));
            Assert.AreEqual(1, DamageFormula.Compute(Phys(0, armor: 0))); // 基础 0 也保底 1
        }
    }
}