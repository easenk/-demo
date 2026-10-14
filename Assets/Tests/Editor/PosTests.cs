using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    [TestFixture]
    public class PosTests
    {
        [Test]
        public void Zero_EqualsNewZero()
        {
            Assert.AreEqual(new Pos(0, 0), Pos.Zero);
        }

        [Test]
        public void Equality_OperatorsMatchEquals()
        {
            var a = new Pos(3, 5);
            var b = new Pos(3, 5);
            var c = new Pos(5, 3);

            Assert.IsTrue(a == b);
            Assert.IsFalse(a != b);
            Assert.IsTrue(a != c);
            Assert.IsTrue(a.Equals(b));
            Assert.IsFalse(a.Equals(c));
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }

        [Test]
        public void EqualsObject_BoxedPosWorks()
        {
            var a = new Pos(1, 2);
            object boxed = new Pos(1, 2);
            Assert.IsTrue(a.Equals(boxed));
            Assert.IsFalse(a.Equals(null));
            Assert.IsFalse(a.Equals("not a pos"));
        }

        [Test]
        public void Chebyshev_DiagonalIsOne()
        {
            // 方格棋盘"距离1" = 周围一圈 8 格：斜对角 (1,1) 的距离是 1
            Assert.AreEqual(1, new Pos(0, 0).ChebyshevTo(new Pos(1, 1)));
            Assert.AreEqual(1, new Pos(2, 2).ChebyshevTo(new Pos(1, 3)));
        }

        [Test]
        public void Chebyshev_TakesMaxAxis()
        {
            Assert.AreEqual(0, new Pos(3, 3).ChebyshevTo(new Pos(3, 3)));
            Assert.AreEqual(2, new Pos(0, 0).ChebyshevTo(new Pos(2, 1)));
            Assert.AreEqual(5, new Pos(0, 0).ChebyshevTo(new Pos(3, 5)));
            Assert.AreEqual(7, new Pos(0, 0).ChebyshevTo(new Pos(7, 5))); // 8×6 棋盘最远角
        }

        [Test]
        public void Manhattan_SumsBothAxes()
        {
            Assert.AreEqual(0, new Pos(2, 2).ManhattanTo(new Pos(2, 2)));
            Assert.AreEqual(2, new Pos(0, 0).ManhattanTo(new Pos(1, 1)));
            Assert.AreEqual(5, new Pos(0, 0).ManhattanTo(new Pos(3, 2)));
        }

        [Test]
        public void OutOfBoundsCoords_AreJustValues()
        {
            // 越界不是 Pos 的责任：它只是个坐标值，合法性由 BoardData.IsInside 判断（批次4）
            var outside = new Pos(-1, 99);
            Assert.AreEqual(-1, outside.X);
            Assert.AreEqual(99, outside.Y);
            Assert.IsTrue(outside == new Pos(-1, 99));
        }
    }
}