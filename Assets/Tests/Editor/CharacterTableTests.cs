using System;
using System.Collections.Generic;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    // 测试工具：批量造 CharacterDef（表级测试只关心 id/cost，面板统一用同一份样例）
    internal static class DefFactory
    {
        public static UnitStats SampleStats => new UnitStats
        {
            MaxHp = 50,
            AttackPower = 14,
            Armor = 30,
            MagicResist = 30,
            AbilityPower = 100,
            AttackRange = 1,
            AttackInterval = 1.0f,
            MoveInterval = 1.0f,
            CritChance = 5,
            CritDamage = 50,
        };

        public static CharacterDef Make(int id, int cost = 1)
        {
            return new CharacterDef(id, $"棋子{id}", cost, SampleStats, null);
        }
    }

    // ─────────────────────────────────────────────
    // CharacterDef：星级规则
    // ─────────────────────────────────────────────
    [TestFixture]
    public class CharacterDefTests
    {
        private static CharacterDef MakeDef()
        {
            return new CharacterDef(1, "见习剑士", 1, DefFactory.SampleStats, null);
        }

        [Test]
        public void StatsForStar_Star1_IsBaseUnchanged()
        {
            var def = MakeDef();
            Assert.AreEqual(def.BaseStats, def.StatsForStar(1)); // 1 星 = 基准原样
        }

        [Test]
        public void StatsForStar_ScalesOnlyHpAndAttack()
        {
            var def = MakeDef();

            // 量：血/攻复利逐点核对（50×1.8=90；50×1.8²=162；14×1.5=21；14×1.5²=31.5）
            var s2 = def.StatsForStar(2);
            Assert.AreEqual(90, s2.MaxHp);
            Assert.AreEqual(21, s2.AttackPower);

            var s3 = def.StatsForStar(3);
            Assert.AreEqual(162, s3.MaxHp);
            Assert.AreEqual(32, s3.AttackPower); // 31.5 → 远离零取整 = 32（D2 在真实数据上落地）

            // 质：其余八维原样
            var b = def.BaseStats;
            Assert.AreEqual(b.Armor, s3.Armor);
            Assert.AreEqual(b.MagicResist, s3.MagicResist);
            Assert.AreEqual(b.AbilityPower, s3.AbilityPower);
            Assert.AreEqual(b.AttackRange, s3.AttackRange);
            Assert.AreEqual(b.AttackInterval, s3.AttackInterval);
            Assert.AreEqual(b.MoveInterval, s3.MoveInterval);
            Assert.AreEqual(b.CritChance, s3.CritChance);
            Assert.AreEqual(b.CritDamage, s3.CritDamage);
        }

        [Test]
        public void StatsForStar_InvalidStar_Throws()
        {
            var def = MakeDef();
            Assert.Throws<ArgumentOutOfRangeException>(() => def.StatsForStar(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => def.StatsForStar(4));
        }

        [Test]
        public void CopiesForStar_Is1_3_9_AndValidates()
        {
            Assert.AreEqual(1, CharacterDef.CopiesForStar(1));
            Assert.AreEqual(3, CharacterDef.CopiesForStar(2));
            Assert.AreEqual(9, CharacterDef.CopiesForStar(3));
            Assert.Throws<ArgumentOutOfRangeException>(() => CharacterDef.CopiesForStar(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => CharacterDef.CopiesForStar(4));
        }

        [Test]
        public void SellPrice_TotalInvestmentMinusMerges()
        {
            var def = new CharacterDef(7, "测试棋子", 2, DefFactory.SampleStats, null);

            Assert.AreEqual(2, def.SellPrice(1));  // 1星：卖价=买入价 → 试错零损耗
            Assert.AreEqual(5, def.SellPrice(2));  // 2星：3×2 − 1
            Assert.AreEqual(16, def.SellPrice(3)); // 3星：9×2 − 2
        }

        [Test]
        public void Synergies_AreDefensivelyCopied()
        {
            var external = new int[] { 11, 22 };
            var def = new CharacterDef(1, "甲", 1, DefFactory.SampleStats, external);

            external[0] = 99; // 外部数组事后怎么改，都不许污染表内定义

            Assert.AreEqual(11, def.Synergies[0]);
            Assert.AreEqual(22, def.Synergies[1]);
        }

        [Test]
        public void None_HasIdZero()
        {
            Assert.AreEqual(0, CharacterDef.None.Id); // 哨兵：0 = 无/非法
        }
    }

    // ─────────────────────────────────────────────
    // CharacterTable：查表纪律
    // ─────────────────────────────────────────────
    [TestFixture]
    public class CharacterTableTests
    {
        [Test]
        public void Constructor_RejectsDuplicateIds()
        {
            var defs = new[] { DefFactory.Make(1), DefFactory.Make(1) };
            Assert.Throws<ArgumentException>(() => new CharacterTable(defs));
        }

        [Test]
        public void Constructor_RejectsNonPositiveIds()
        {
            Assert.Throws<ArgumentException>(() => new CharacterTable(new[] { DefFactory.Make(0) }));
            Assert.Throws<ArgumentException>(() => new CharacterTable(new[] { DefFactory.Make(-3) }));
        }

        [Test]
        public void Constructor_RejectsNullList()
        {
            Assert.Throws<ArgumentNullException>(() => new CharacterTable(null));
        }

        [Test]
        public void ConsecutiveIds_AllResolveToThemselves()
        {
            var defs = new[]
            {
                DefFactory.Make(1), DefFactory.Make(2), DefFactory.Make(3),
                DefFactory.Make(4), DefFactory.Make(5),
            };
            var table = new CharacterTable(defs);

            Assert.AreEqual(5, table.Count);
            for (int id = 1; id <= 5; id++)
            {
                var def = table.Get(id);
                Assert.AreEqual(id, def.Id, $"id={id} 应能查到自己");
                Assert.AreEqual($"棋子{id}", def.DisplayName);
            }
        }

        [Test]
        public void UnknownId_ReturnsNoneAndTryGetFalse()
        {
            var table = new CharacterTable(new[] { DefFactory.Make(1) });

            Assert.AreEqual(0, table.Get(999).Id); // 未知 id → None（Id=0）

            Assert.IsFalse(table.TryGet(999, out var def));
            Assert.AreEqual(0, def.Id); // out 参数也给 None，不留 default 陷阱
        }

        [Test]
        public void All_PreservesInsertionOrder()
        {
            var defs = new[] { DefFactory.Make(3), DefFactory.Make(1), DefFactory.Make(2) };
            var table = new CharacterTable(defs);

            Assert.AreEqual(3, table.All.Count);
            Assert.AreEqual(3, table.All[0].Id);
            Assert.AreEqual(1, table.All[1].Id);
            Assert.AreEqual(2, table.All[2].Id);
        }

        [Test]
        public void GetByCost_FiltersByCostBucket()
        {
            var defs = new[]
            {
                DefFactory.Make(1, cost: 1), DefFactory.Make(2, cost: 2),
                DefFactory.Make(3, cost: 1), DefFactory.Make(4, cost: 3),
            };
            var table = new CharacterTable(defs);

            var ones = table.GetByCost(1);
            Assert.AreEqual(2, ones.Count);
            Assert.AreEqual(1, ones[0].Id);
            Assert.AreEqual(3, ones[1].Id);

            Assert.AreEqual(0, table.GetByCost(99).Count); // 无该费档 → 空表而非 null
        }

        [Test]
        public void Default_IsStructurallySound()
        {
            var table = CharacterTable.Default;
            Assert.GreaterOrEqual(table.Count, 1);

            var seen = new HashSet<int>();
            foreach (var def in table.All)
            {
                Assert.Greater(def.Id, 0, "基线 id 必须为正");
                Assert.IsTrue(seen.Add(def.Id), $"基线 id 重复：{def.Id}");
                Assert.AreEqual(def.Id, table.Get(def.Id).Id, "基线条目必须能查到自己");
                Assert.That(def.Cost, Is.InRange(1, 5), "商店费用约定 1~5");

                // 间隔是批次5 的除数，为 0 就是除零炸弹——基线数据不许埋雷
                Assert.Greater(def.BaseStats.MaxHp, 0);
                Assert.Greater(def.BaseStats.AttackInterval, 0f);
                Assert.Greater(def.BaseStats.MoveInterval, 0f);
                Assert.That(def.BaseStats.CritChance, Is.InRange(0, 100));
                Assert.GreaterOrEqual(def.BaseStats.CritDamage, 0);

                for (int star = 1; star <= 3; star++)
                    Assert.DoesNotThrow(() => def.StatsForStar(star));
            }
        }
    }
}