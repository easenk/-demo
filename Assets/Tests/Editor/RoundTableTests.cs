using System;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    [TestFixture]
    public class RoundTableTests
    {
        [Test]
        public void Default_Has14RoundsNumberedContinuously()
        {
            var t = RoundTable.Default;
            Assert.AreEqual(14, t.Count);            // #20⑤：当前一律按 14 回合制作（R15 隐藏 Boss 不进表）
            for (int r = 1; r <= 14; r++)
                Assert.AreEqual(r, t.Get(r).Number); // 回合号 = 下标 + 1：全表的前提
        }

        [Test]
        public void Default_TypeDistribution()
        {
            var t = RoundTable.Default;
            int[] reward = { 1, 5, 10 };
            int[] elite = { 6, 11 };
            for (int r = 1; r <= 14; r++)
            {
                var type = t.Get(r).Type;
                if (r == 14) Assert.AreEqual(RoundType.Boss, type);
                else if (Array.IndexOf(reward, r) >= 0) Assert.AreEqual(RoundType.Reward, type);
                else if (Array.IndexOf(elite, r) >= 0) Assert.AreEqual(RoundType.Elite, type);
                else Assert.AreEqual(RoundType.Normal, type);
            }
        }

        [Test]
        public void Default_RewardRounds_ZeroDamage()
        {
            foreach (int r in new[] { 1, 5, 10 })
            {
                var def = RoundTable.Default.Get(r);
                Assert.AreEqual(0, def.DamageBase, $"回合 {r} 奖励关战败不掉血（压力谷底的福利关）");
            }
        }

        [Test]
        public void Default_EliteRounds_FixedDamage8()
        {
            foreach (int r in new[] { 6, 11 })
            {
                var def = RoundTable.Default.Get(r);
                Assert.AreEqual(RoundType.Elite, def.Type);
                Assert.AreEqual(8, def.DamageBase, "精英败固定 -8 血（#21⑨）——不是参考工程的 6/8 双值");
            }
        }

        [Test]
        public void Default_NormalRounds_DamageLadder()
        {
            var t = RoundTable.Default;
            foreach (int r in new[] { 2, 3, 4 }) Assert.AreEqual(3, t.Get(r).DamageBase);
            foreach (int r in new[] { 7, 8, 9 }) Assert.AreEqual(5, t.Get(r).DamageBase);
            foreach (int r in new[] { 12, 13 }) Assert.AreEqual(7, t.Get(r).DamageBase);   // 越往后输越痛
        }

        [Test]
        public void Default_BossRound_NoDamage()
        {
            var def = RoundTable.Default.Get(14);
            Assert.AreEqual(RoundType.Boss, def.Type);
            Assert.AreEqual(0, def.DamageBase);      // Boss 败 = 判负，不走数值伤害
        }

        [Test]
        public void Ctor_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new RoundTable(null));
        }

        [Test]
        public void Ctor_Empty_Throws()
        {
            Assert.Throws<ArgumentException>(() => new RoundTable(new RoundDef[0]));
        }

        [Test]
        public void Ctor_NumberOutOfSequence_Throws()
        {
            // 首条就不是 1
            Assert.Throws<ArgumentException>(() => new RoundTable(new[]
            {
                new RoundDef(2, RoundType.Normal, 3),
            }));
            // 中间断号
            Assert.Throws<ArgumentException>(() => new RoundTable(new[]
            {
                new RoundDef(1, RoundType.Normal, 3),
                new RoundDef(3, RoundType.Normal, 3),
            }));
        }

        [Test]
        public void Ctor_CustomTable_CountAndGetWork()
        {
            // 短表合法：回合数不是 14 的铁律，加回合 = 扩表（GameSession 管住边界）
            var t = new RoundTable(new[]
            {
                new RoundDef(1, RoundType.Reward, 0),
                new RoundDef(2, RoundType.Boss, 0),
            });
            Assert.AreEqual(2, t.Count);
            Assert.AreEqual(RoundType.Reward, t.Get(1).Type);
            Assert.AreEqual(RoundType.Boss, t.Get(2).Type);
        }
    }
}
