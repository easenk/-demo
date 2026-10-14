using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    [TestFixture]
    public class GameMathTests
    {
        // ── D2：四舍五入远离零（对照：银行家舍入会给 0.5→0、1.5→2、2.5→2）──

        [Test]
        public void RoundToInt_HalfAlwaysAwayFromZero()
        {
            Assert.AreEqual(1, GameMath.RoundToInt(0.5f));
            Assert.AreEqual(2, GameMath.RoundToInt(1.5f));
            Assert.AreEqual(3, GameMath.RoundToInt(2.5f));
        }

        [Test]
        public void RoundToInt_NegativeHalfAwayFromZero()
        {
            Assert.AreEqual(-1, GameMath.RoundToInt(-0.5f));
            Assert.AreEqual(-2, GameMath.RoundToInt(-1.5f));
            Assert.AreEqual(-3, GameMath.RoundToInt(-2.5f));
        }

        [Test]
        public void RoundToInt_OrdinaryValuesRoundNearest()
        {
            Assert.AreEqual(0, GameMath.RoundToInt(0.4f));
            Assert.AreEqual(0, GameMath.RoundToInt(-0.4f));
            Assert.AreEqual(1, GameMath.RoundToInt(0.6f));
            Assert.AreEqual(12, GameMath.RoundToInt(11.9f));
        }

        [Test]
        public void MaxMin_IntAndFloat()
        {
            Assert.AreEqual(5, GameMath.Max(3, 5));
            Assert.AreEqual(3, GameMath.Min(3, 5));
            Assert.AreEqual(1.5f, GameMath.Max(1.5f, -2f));
            Assert.AreEqual(-2f, GameMath.Min(1.5f, -2f));
        }

        [Test]
        public void Abs_NegativeFlips()
        {
            Assert.AreEqual(7, GameMath.Abs(-7));
            Assert.AreEqual(7, GameMath.Abs(7));
            Assert.AreEqual(0, GameMath.Abs(0));
        }

        [Test]
        public void Clamp_IntBoundaries()
        {
            Assert.AreEqual(3, GameMath.Clamp(0, 3, 10));   // 低于下限 → 钳到下限
            Assert.AreEqual(10, GameMath.Clamp(99, 3, 10)); // 高于上限 → 钳到上限
            Assert.AreEqual(7, GameMath.Clamp(7, 3, 10));    // 区间内 → 原样返回
        }

        [Test]
        public void Clamp_FloatBoundaries()
        {
            Assert.AreEqual(0.1f, GameMath.Clamp(0.05f, 0.1f, 100f));
            Assert.AreEqual(100f, GameMath.Clamp(300f, 0.1f, 100f));
            Assert.AreEqual(50f, GameMath.Clamp(50f, 0.1f, 100f));
        }
    }
}