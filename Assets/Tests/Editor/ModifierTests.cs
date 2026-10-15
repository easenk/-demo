using System;
using System.Collections.Generic;
using System.Reflection;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    /// <summary>
    /// 批次3：修饰器结构性契约。
    /// 账本与结算类测试（Flat/Percent 叠加、两件+50%=×2、十维全生效、钳制、
    /// 按来源批量清除、账本对称）等批次4 的 Unit 就位后补入本文件。
    /// </summary>
    [TestFixture]
    public class ModifierTests
    {
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
    }
}