using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    [TestFixture]
    public class GameRandomTests
    {
        [Test]
        public void SameSeed_SameSequence()
        {
            var a = new GameRandom(12345u);
            var b = new GameRandom(12345u);
            for (int i = 0; i < 1000; i++)
                Assert.AreEqual(a.NextInt(int.MaxValue), b.NextInt(int.MaxValue), $"第 {i} 次抽取出现分歧");
        }

        [Test]
        public void DifferentSeed_DifferentSequence()
        {
            var a = new GameRandom(1u);
            var b = new GameRandom(2u);
            bool differs = false;
            for (int i = 0; i < 100 && !differs; i++)
                differs = a.NextInt(int.MaxValue) != b.NextInt(int.MaxValue);
            Assert.IsTrue(differs, "两个不同种子的序列不应完全相同");
        }

        [Test]
        public void SeedZero_MapsToFixedNonZeroFallback()
        {
            var r = new GameRandom(0u);
            Assert.AreEqual(0u, r.Seed); // 原始种子保留为 0（复现记账用）

            // 与固定替代常量的实例完全同序列 —— 这条把"种子0 → 哪个替代值"焊死
            var fallback = new GameRandom(GameRandom.SeedZeroFallback);
            for (int i = 0; i < 100; i++)
                Assert.AreEqual(fallback.NextInt(int.MaxValue), r.NextInt(int.MaxValue));
        }

        [Test]
        public void NextInt_RangeRespected()
        {
            var r = new GameRandom(777u);
            for (int i = 0; i < 10000; i++)
            {
                int v = r.NextInt(5, 10);
                Assert.That(v, Is.InRange(5, 9), "NextInt(5,10) 只能产出 [5,10) 的整数");
            }
        }

        [Test]
        public void NextInt_SingleValueRangeAlwaysThatValue()
        {
            var r = new GameRandom(42u);
            for (int i = 0; i < 100; i++)
                Assert.AreEqual(7, r.NextInt(7, 8)); // [7,8) 只含 7
            Assert.AreEqual(0, r.NextInt(1));        // [0,1) 只含 0
        }

        [Test]
        public void NextInt_BothSidesOfCoinOccur()
        {
            var r = new GameRandom(2026u);
            int zeros = 0;
            for (int i = 0; i < 100000; i++)
                if (r.NextInt(2) == 0) zeros++;
            Assert.That(zeros, Is.InRange(45000, 55000), "10 万次掷硬币两面都该出现（约各半）");
        }

        [Test]
        public void RollPercent_ZeroNeverHits()
        {
            var r = new GameRandom(1u);
            for (int i = 0; i < 10000; i++)
                Assert.IsFalse(r.RollPercent(0f), "0% 概率一次都不许中");
        }

        [Test]
        public void RollPercent_HundredAlwaysHits()
        {
            var r = new GameRandom(1u);
            for (int i = 0; i < 10000; i++)
                Assert.IsTrue(r.RollPercent(100f), "100% 概率必须每次都中");
        }

        [Test]
        public void RollPercent_HalfIsAboutHalf()
        {
            var r = new GameRandom(12345u);
            int hits = 0;
            for (int i = 0; i < 100000; i++)
                if (r.RollPercent(50f)) hits++;
            Assert.That(hits, Is.InRange(48000, 52000), "50% 掷 10 万次应接近一半（同种子下本断言稳定复现）");
        }

        [Test]
        public void RollPercent_DeterministicPerSeed()
        {
            var a = new GameRandom(2024u);
            var b = new GameRandom(2024u);
            for (int i = 0; i < 1000; i++)
                Assert.AreEqual(a.RollPercent(30f), b.RollPercent(30f), $"第 {i} 次掷骰结果分歧");
        }
    }
}