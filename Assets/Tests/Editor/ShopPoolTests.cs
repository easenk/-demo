using System.Collections.Generic;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    [TestFixture]
    public class ShopPoolTests
    {
        // 测试用角色表：两个 1 费、一个 2 费、一个 3 费——
        // 池按表派生（#21⑦ 份数表）：1费 2×18=36、2费 1×12=12、3费 1×15=15，全池 63 份
        private static CharacterTable TestCharacters => new CharacterTable(new[]
        {
            Def(1, "一费甲", 1),
            Def(2, "一费乙", 1),
            Def(3, "二费甲", 2),
            Def(4, "三费甲", 3),
        });

        private static CharacterDef Def(int id, string name, int cost)
            => new CharacterDef(id, name, cost, ValidStats(), null);

        private static UnitStats ValidStats() => new UnitStats
        {
            MaxHp = 50, AttackPower = 10, Armor = 20, MagicResist = 20, AbilityPower = 100,
            AttackRange = 1, AttackInterval = 1.0f, MoveInterval = 1.0f, CritChance = 5, CritDamage = 50,
        };

        private static ShopPool NewPool(uint seed) => new ShopPool(new GameRandom(seed), TestCharacters, ShopOdds.Default);

        [Test]
        public void Ctor_InitialCopies_ByCostTable()
        {
            var pool = NewPool(1);
            Assert.AreEqual(18, pool.CopiesLeft(1));   // #21⑦：1 费每角色 18 份
            Assert.AreEqual(18, pool.CopiesLeft(2));
            Assert.AreEqual(12, pool.CopiesLeft(3));   // 2 费 12 份
            Assert.AreEqual(15, pool.CopiesLeft(4));   // 3 费 15 份
            Assert.AreEqual(63, pool.TotalLeft);       // 2×18 + 12 + 15
        }

        [Test]
        public void CopiesLeft_UnknownId_Zero()
        {
            var pool = NewPool(1);
            Assert.AreEqual(0, pool.CopiesLeft(99));   // 未知 id：0 = 没有，不抛
        }

        [Test]
        public void Roll_SameSeed_SameSequence()
        {
            var a = NewPool(7);
            var b = NewPool(7);
            for (int i = 0; i < 20; i++)
            {
                var da = a.Roll(8);   // L8：五档全开，两段抽取全路径
                var db = b.Roll(8);
                Assert.AreEqual(da.Id, db.Id, $"第 {i + 1} 次出价不同——同种子必须同序列");
            }
        }

        [Test]
        public void Roll_Level2_AlwaysTier1()
        {
            var pool = NewPool(3);
            for (int i = 0; i < 20; i++)
            {
                var def = pool.Roll(2);   // #21⑥ L2 一费 100%
                Assert.AreEqual(1, def.Cost);
            }
        }

        [Test]
        public void Roll_DoesNotTouchCopies()
        {
            var pool = NewPool(5);
            int totalBefore = pool.TotalLeft;
            int[] before = { pool.CopiesLeft(1), pool.CopiesLeft(2), pool.CopiesLeft(3), pool.CopiesLeft(4) };
            for (int i = 0; i < 20; i++) pool.Roll(8);
            Assert.AreEqual(totalBefore, pool.TotalLeft);   // 出价不扣份数——"未购买卡回池"因此免费成立
            Assert.AreEqual(before[0], pool.CopiesLeft(1));
            Assert.AreEqual(before[1], pool.CopiesLeft(2));
            Assert.AreEqual(before[2], pool.CopiesLeft(3));
            Assert.AreEqual(before[3], pool.CopiesLeft(4));
        }

        [Test]
        public void TakeOut_Decrements_Exhausts_PureRejects()
        {
            var pool = NewPool(1);
            Assert.IsTrue(pool.TakeOut(1));
            Assert.AreEqual(17, pool.CopiesLeft(1));
            Assert.AreEqual(62, pool.TotalLeft);
            for (int i = 0; i < 17; i++) Assert.IsTrue(pool.TakeOut(1));   // 抽干
            Assert.AreEqual(0, pool.CopiesLeft(1));
            Assert.IsFalse(pool.TakeOut(1));   // 已 0：纯拒绝
            Assert.IsFalse(pool.TakeOut(99));  // 未知 id：纯拒绝
        }

        [Test]
        public void Return_Restores_UntilCap()
        {
            var pool = NewPool(1);
            for (int i = 0; i < 18; i++) Assert.IsTrue(pool.TakeOut(1));   // 抽干 18 份
            Assert.AreEqual(0, pool.CopiesLeft(1));
            for (int i = 0; i < 18; i++) Assert.IsTrue(pool.Return(1));    // 卖回至出厂封顶
            Assert.AreEqual(18, pool.CopiesLeft(1));
            Assert.IsFalse(pool.Return(1));   // 第 19 份：往满额池塞卡 = 凭空造卡，纯拒绝
        }

        [Test]
        public void Return_UnknownId_PureReject()
        {
            var pool = NewPool(1);
            Assert.IsFalse(pool.Return(99));
        }

        [Test]
        public void Roll_DrainedTier_NeverChosen()
        {
            var pool = NewPool(11);
            for (int i = 0; i < 18; i++)
            {
                Assert.IsTrue(pool.TakeOut(1));
                Assert.IsTrue(pool.TakeOut(2));   // 两个 1 费角色全抽干：1 费档死亡
            }
            for (int i = 0; i < 20; i++)
            {
                var def = pool.Roll(8);
                Assert.AreNotEqual(1, def.Cost, "抽干的档位不许再出（死亡档位概率归 0，摊还给存活档）");
            }
        }

        [Test]
        public void Roll_EmptyTierLevel_ReturnsNone()
        {
            var pool = NewPool(11);
            for (int i = 0; i < 18; i++) { pool.TakeOut(1); pool.TakeOut(2); }
            Assert.AreEqual(0, pool.Roll(2).Id);   // L2 只开 1 费档且档已全空：Id 0 哨兵，不掷骰不死磕
            Assert.GreaterOrEqual(pool.Roll(8).Cost, 2);   // L8 还有 2/3 费可开
        }

        [Test]
        public void Accounting_Invariant_ReservationFlow()
        {
            var pool = NewPool(13);
            int factory = pool.TotalLeft;                           // 63
            var rolled = new List<int>();
            for (int i = 0; i < 5; i++) rolled.Add(pool.Roll(2).Id);   // 模拟一次 5 格上架的出价
            Assert.AreEqual(factory, pool.TotalLeft);               // 出价不动
            foreach (var id in rolled) Assert.IsTrue(pool.TakeOut(id));   // 预订
            Assert.AreEqual(factory - 5, pool.TotalLeft);           // 买定各减一
            foreach (var id in rolled) Assert.IsTrue(pool.Return(id));    // 全部卖回
            Assert.AreEqual(factory, pool.TotalLeft);               // 账目归位：永不漂移
        }
    }
}
