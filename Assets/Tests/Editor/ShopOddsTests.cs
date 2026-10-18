using System;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    [TestFixture]
    public class ShopOddsTests
    {
        [Test]
        public void Default_RowSumsAre100()
        {
            var odds = ShopOdds.Default;
            for (int level = 2; level <= 8; level++)
            {
                int sum = 0;
                for (int cost = 1; cost <= 5; cost++) sum += odds.Get(level, cost);
                Assert.AreEqual(100, sum, $"等级 {level} 概率行合计必须为 100（#21⑥）");
            }
        }

        [Test]
        public void Default_AnchorValues()
        {
            var odds = ShopOdds.Default;
            Assert.AreEqual(100, odds.Get(2, 1));   // L2 一费 100%：开局纯 1 费
            Assert.AreEqual(30, odds.Get(6, 3));    // 三费率峰值 30 钉在 6 级
            Assert.AreEqual(8, odds.Get(8, 5));     // 五费 8% 摸得到
            Assert.AreEqual(0, odds.Get(2, 5));     // L2 没有五费
        }

        [Test]
        public void Get_OutOfRange_ReturnsZero()
        {
            var odds = ShopOdds.Default;
            Assert.AreEqual(0, odds.Get(1, 1));     // 等级低于开局 2 级
            Assert.AreEqual(0, odds.Get(9, 1));     // 超过满级 8
            Assert.AreEqual(0, odds.Get(2, 0));     // 费用 0 不存在
            Assert.AreEqual(0, odds.Get(2, 6));     // 费用超过 5
        }

        [Test]
        public void OpenCosts_ListsPositiveTiersAscending()
        {
            var odds = ShopOdds.Default;
            CollectionAssert.AreEqual(new[] { 1 }, odds.OpenCosts(2));
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, odds.OpenCosts(4));
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, odds.OpenCosts(8));
        }

        [Test]
        public void Ctor_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new ShopOdds(null));
        }

        [Test]
        public void Ctor_RowSumNot100_Throws()
        {
            var bad = new int[9, 6];
            bad[2, 1] = 100;
            bad[3, 1] = 74; bad[3, 2] = 25;         // L3 合计 99：少 1 个百分点
            Assert.Throws<ArgumentException>(() => new ShopOdds(bad));   // 出价会偶尔落空，构造期炸
        }

        [Test]
        public void Ctor_CustomTable_AcceptsAndReads()
        {
            // 实例 + 可注入：正式内容阶段4 由 SO 资产搬运工重建后注入——注入路径必须可用
            var flat = new int[9, 6];
            for (int level = 2; level <= 8; level++)
                for (int cost = 1; cost <= 5; cost++)
                    flat[level, cost] = 20;         // 每行 5×20 = 100
            var odds = new ShopOdds(flat);
            Assert.AreEqual(20, odds.Get(5, 3));
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, odds.OpenCosts(5));
        }
    }
}
