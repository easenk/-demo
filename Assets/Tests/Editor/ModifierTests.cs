using System;
using System.Collections.Generic;
using System.Reflection;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    /// <summary>
    /// 修饰器契约：结构（批次3 的 4 条）+ 账本/EffectiveStats 结算（批次4 补入 8 条）。
    /// </summary>
    [TestFixture]
    public class ModifierTests
    {
        // ─────────────── 批次3：结构 ───────────────

        [Test]
        public void StatKind_And_UnitStats_AreOneToOne()
        {
            var kinds = new HashSet<string>(Enum.GetNames(typeof(StatKind)));

            var fields = new HashSet<string>();
            foreach (var f in typeof(UnitStats).GetFields(BindingFlags.Public | BindingFlags.Instance))
                fields.Add(f.Name);

            // 数量相等 + kind 侧全部命中 → 双向一一对应（当前为十对十）
            Assert.AreEqual(fields.Count, kinds.Count,
                $"StatKind {kinds.Count} 项 vs UnitStats {fields.Count} 维——两侧必须同步增减");
            foreach (var name in kinds)
                Assert.IsTrue(fields.Contains(name),
                    $"StatKind.{name} 在 UnitStats 里没有同名维度：挂了修饰器也会静默失效，这是最贵的 bug");
        }

        [Test]
        public void StatModifier_DefaultSourceId_IsNone()
        {
            var m = new StatModifier(StatKind.Armor, StatOp.Flat, 30f);
            Assert.AreEqual(StatKind.Armor, m.Kind);
            Assert.AreEqual(StatOp.Flat, m.Op);
            Assert.AreEqual(30f, m.Value);
            Assert.AreEqual(ModSourceId.None, m.SourceId); // 不填来源默认 0
        }

        [Test]
        public void StatModifier_ValueEquality_RemovesFirstMatch()
        {
            // 账本对称的前哨：同四联单视为等价 → List.Remove 按值删第一条依赖这一点
            var a = new StatModifier(StatKind.AttackPower, StatOp.Percent, 50f, 1000);
            var b = new StatModifier(StatKind.AttackPower, StatOp.Percent, 50f, 1000);
            var c = new StatModifier(StatKind.AttackPower, StatOp.Percent, 51f, 1000);

            Assert.IsTrue(a.Equals(b));
            Assert.IsFalse(a.Equals(c));

            var ledger = new List<StatModifier> { a, b };
            Assert.IsTrue(ledger.Remove(b)); // b 与 a 等价 → 被删掉的其实是第一条 a
            Assert.AreEqual(1, ledger.Count);
        }

        [Test]
        public void ModSourceId_Bands_DoNotOverlap()
        {
            // 号段防撞车：相邻段必须不重叠，0 = 无来源不占任何段
            Assert.Less(ModSourceId.SystemMax, ModSourceId.EquipmentBase);
            Assert.Less(ModSourceId.EquipmentMax, ModSourceId.TreasureBase);
            Assert.Less(ModSourceId.TreasureMax, ModSourceId.TestBase);
            Assert.AreEqual(0, ModSourceId.None);
        }

        // ─────────── 批次4：账本与 EffectiveStats 结算 ───────────

        private static Unit NewUnit()
        {
            return new Unit(1, 1, true, DefFactory.SampleStats, new EventBus());
        }

        [Test]
        public void EffectiveStats_NoModifiers_EqualsBase()
        {
            var unit = NewUnit();
            Assert.AreEqual(unit.Stats, unit.EffectiveStats); // 零修饰器：读出来就是出厂面板
        }

        [Test]
        public void EffectiveStats_FlatAndPercent_Formula()
        {
            var unit = NewUnit();
            unit.AddModifier(new StatModifier(StatKind.MaxHp, StatOp.Flat, 30f, 3000));
            unit.AddModifier(new StatModifier(StatKind.MaxHp, StatOp.Percent, 50f, 3000));

            // (50 + 30) × (1 + 0.5) = 120
            Assert.AreEqual(120, unit.EffectiveStats.MaxHp);
        }

        [Test]
        public void EffectiveStats_PercentSums_DoesNotStackMultiply()
        {
            var unit = NewUnit();
            unit.AddModifier(new StatModifier(StatKind.AttackPower, StatOp.Percent, 50f, 3000));
            unit.AddModifier(new StatModifier(StatKind.AttackPower, StatOp.Percent, 50f, 3000));

            // 14 × (1 + 0.5 + 0.5) = 28，不是 14×1.5×1.5=31.5（可预期性优先）
            Assert.AreEqual(28, unit.EffectiveStats.AttackPower);
        }

        [Test]
        public void EffectiveStats_AllTenKinds_TakeEffectWithClamps()
        {
            var unit = NewUnit();
            unit.AddModifier(new StatModifier(StatKind.MaxHp, StatOp.Flat, 10f, 3000));
            unit.AddModifier(new StatModifier(StatKind.AttackPower, StatOp.Flat, 6f, 3000));
            unit.AddModifier(new StatModifier(StatKind.Armor, StatOp.Flat, -500f, 3000));
            unit.AddModifier(new StatModifier(StatKind.MagicResist, StatOp.Flat, 70f, 3000));
            unit.AddModifier(new StatModifier(StatKind.AbilityPower, StatOp.Flat, -999f, 3000));
            unit.AddModifier(new StatModifier(StatKind.AttackRange, StatOp.Flat, 2f, 3000));
            unit.AddModifier(new StatModifier(StatKind.AttackInterval, StatOp.Flat, 0.3f, 3000));
            unit.AddModifier(new StatModifier(StatKind.MoveInterval, StatOp.Flat, -0.25f, 3000));
            unit.AddModifier(new StatModifier(StatKind.CritChance, StatOp.Flat, 200f, 3000));
            unit.AddModifier(new StatModifier(StatKind.CritDamage, StatOp.Flat, -10f, 3000));

            var s = unit.EffectiveStats;
            Assert.AreEqual(60, s.MaxHp);
            Assert.AreEqual(20, s.AttackPower);
            Assert.AreEqual(-470, s.Armor);          // 抗性不钳下限：合法负值（击碎/削抗走这条路）
            Assert.AreEqual(100, s.MagicResist);
            Assert.AreEqual(0, s.AbilityPower);      // 法强 ≥ 0
            Assert.AreEqual(3, s.AttackRange);
            Assert.AreEqual(1.3f, s.AttackInterval, 0.0001f);
            Assert.AreEqual(0.75f, s.MoveInterval, 0.0001f);
            Assert.AreEqual(100, s.CritChance);     // 暴击率 ∈ [0,100]：205 → 100
            Assert.AreEqual(40, s.CritDamage);
        }

        [Test]
        public void EffectiveStats_IntervalsFlooredAtPointOne()
        {
            var unit = NewUnit();
            unit.AddModifier(new StatModifier(StatKind.AttackInterval, StatOp.Flat, -5f, 3000));
            unit.AddModifier(new StatModifier(StatKind.MoveInterval, StatOp.Flat, -5f, 3000));

            // 间隔是 CombatSystem 的除数：1.0−5 = −4 → 钳 0.1，永不为 0
            Assert.AreEqual(0.1f, unit.EffectiveStats.AttackInterval);
            Assert.AreEqual(0.1f, unit.EffectiveStats.MoveInterval);
        }

        [Test]
        public void EffectiveStats_IsRecomputedLive_NotCached()
        {
            var unit = NewUnit();
            var m = new StatModifier(StatKind.AttackPower, StatOp.Flat, 100f, 3000);

            unit.AddModifier(m);
            Assert.AreEqual(114, unit.EffectiveStats.AttackPower);

            unit.RemoveModifier(m);
            Assert.AreEqual(14, unit.EffectiveStats.AttackPower); // 摘了立刻生效：现算不缓存
        }

        [Test]
        public void RemoveModifiersBySource_BulkClearsAndCounts()
        {
            var unit = NewUnit();
            unit.AddModifier(new StatModifier(StatKind.MaxHp, StatOp.Flat, 20f, 1000));
            unit.AddModifier(new StatModifier(StatKind.AttackPower, StatOp.Flat, 5f, 1000));
            unit.AddModifier(new StatModifier(StatKind.Armor, StatOp.Flat, 10f, 1000));
            unit.AddModifier(new StatModifier(StatKind.AbilityPower, StatOp.Flat, 50f, 2000));

            Assert.AreEqual(3, unit.RemoveModifiersBySource(1000)); // 整批卸载装备段

            var s = unit.EffectiveStats;
            Assert.AreEqual(50, s.MaxHp);        // 回到出厂
            Assert.AreEqual(14, s.AttackPower);
            Assert.AreEqual(30, s.Armor);
            Assert.AreEqual(150, s.AbilityPower); // 宝物段的安然无恙（号段防撞车的意义）

            Assert.AreEqual(0, unit.RemoveModifiersBySource(1000)); // 再卸 = 0 条
            Assert.Throws<ArgumentException>(() => unit.RemoveModifiersBySource(0)); // 0 不是来源
        }
    }
}