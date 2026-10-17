using System;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    [TestFixture]
    public class EnemyTableTests
    {
        // ── 测试用合法面板/条目速记 ──

        private static UnitStats ValidStats => new UnitStats
        {
            MaxHp = 50, AttackPower = 10, Armor = 20, MagicResist = 20, AbilityPower = 0,
            AttackRange = 1, AttackInterval = 1.3f, MoveInterval = 1.0f,
            CritChance = 0, CritDamage = 0, MaxMana = 0, ManaPerHit = 0, ManaRegenPerSecond = 0,
        };

        private static EnemySpawn Spawn(Pos cell, int oreTier = 0, int powerLevel = 1)
            => new EnemySpawn("测试怪", ValidStats, cell, oreTier, powerLevel);

        private static EnemySpawn SpawnWithStats(UnitStats stats)
            => new EnemySpawn("测试怪", stats, new Pos(3, 4), 0, 1);

        /// <summary>把若干生成条目包成"回合 1"的编成表。</summary>
        private static EnemyTable Table(params EnemySpawn[] spawns)
            => new EnemyTable(new[] { new EnemyRoundEntry(1, spawns) });

        // ── Empty / Default ──

        [Test]
        public void Empty_EveryRoundIsEmpty()
        {
            for (int r = 1; r <= 14; r++)
                Assert.IsTrue(EnemyTable.Empty.Get(r).IsEmpty);   // 空表：敌人全靠测试自摆
        }

        [Test]
        public void Default_All14RoundsConfigured()
        {
            var t = EnemyTable.Default;
            for (int r = 1; r <= 14; r++)
            {
                Assert.IsFalse(t.Get(r).IsEmpty, $"回合 {r} 必须有编成");
                Assert.Greater(t.Get(r).Spawns.Length, 0);
            }
        }

        [Test]
        public void Default_OreTierZeroOutsideRewardRounds()
        {
            // 数据纪律（决策#17③ 的另一半）：掉落判断不查回合类型，
            // 所以"非奖励关不掉"必须由编成表自己写 0 保证——本测试焊死这张表不许违反
            int[] nonReward = { 2, 3, 4, 6, 7, 8, 9, 11, 12, 13, 14 };
            foreach (int r in nonReward)
                foreach (var s in EnemyTable.Default.Get(r).Spawns)
                    Assert.AreEqual(0, s.OreTier, $"回合 {r} 非奖励关，条目挡位必须写 0");
        }

        [Test]
        public void Default_RewardRoundSpawns_AllCarryOreTier()
        {
            // 奖励关敌方死亡 100% 掉：该关所有条目都必须带挡位
            foreach (int r in new[] { 1, 5, 10 })
                foreach (var s in EnemyTable.Default.Get(r).Spawns)
                    Assert.Greater(s.OreTier, 0, $"回合 {r} 奖励关，每只怪都要掉矿");
        }

        [Test]
        public void Default_RewardRoundMonsters_SpecialAndWeakPerRound20()
        {
            // #20①：奖励关怪物 = 特殊怪种（专用名 + 战力较低）——强度指标恒 1、血量不超过普通关第二梯段
            foreach (int r in new[] { 1, 5, 10 })
                foreach (var s in EnemyTable.Default.Get(r).Spawns)
                {
                    Assert.AreEqual(1, s.PowerLevel, $"回合 {r}「{s.DisplayName}」特殊怪强度指标恒 1（战力较低）");
                    Assert.LessOrEqual(s.Stats.MaxHp, 60, $"回合 {r}「{s.DisplayName}」特殊怪血量必须低于任何精英/Boss");
                }
        }

        [Test]
        public void Default_AllCellsInsideEnemyHalf()
        {
            // 敌方半场 = row 3~5（前后分，D1）；摆位出半场会走 Session 兜底，但默认表不该依赖兜底
            int enemyRowMin = BoardData.Height - BoardData.PlayerHalfRows;
            foreach (var entry in EnemyTable.Default.Entries)
                foreach (var s in entry.Spawns)
                {
                    Assert.GreaterOrEqual(s.Cell.Y, enemyRowMin, $"回合 {entry.Round}「{s.DisplayName}」摆位进了玩家半场");
                    Assert.Less(s.Cell.Y, BoardData.Height);
                    Assert.GreaterOrEqual(s.Cell.X, 0);
                    Assert.Less(s.Cell.X, BoardData.Width);
                }
        }

        [Test]
        public void Default_StatsRespectEnemyDataDiscipline()
        {
            // D2-7：敌方不暴击、无蓝条是"表数据纪律"不是代码硬编码——本测试盯住默认表遵守它
            foreach (var entry in EnemyTable.Default.Entries)
                foreach (var s in entry.Spawns)
                {
                    Assert.GreaterOrEqual(s.Stats.MaxHp, 1);
                    Assert.Greater(s.Stats.AttackInterval, 0f);
                    Assert.Greater(s.Stats.MoveInterval, 0f);
                    Assert.AreEqual(0, s.Stats.CritChance, $"回合 {entry.Round}「{s.DisplayName}」敌方默认不暴击（改行为 = 改表，先过这里）");
                    Assert.AreEqual(0, s.Stats.MaxMana);
                }
        }

        [Test]
        public void Default_NormalRoundPowerLevels_NonDecreasing()
        {
            // #20③ 填表纪律：普通关按回合顺序，强度指标（每关最大值）单调不降——
            // "回合越靠后指标越高、怪物越强"由测试焊死，写表的人不许违反
            int[] normalRounds = { 2, 3, 4, 7, 8, 9, 12, 13 };
            int lastMax = 0;
            foreach (int r in normalRounds)
            {
                int max = 0;
                foreach (var s in EnemyTable.Default.Get(r).Spawns)
                    max = Math.Max(max, s.PowerLevel);
                Assert.GreaterOrEqual(max, lastMax, $"回合 {r} 的强度指标不得低于更早的普通关（#20③ 填表纪律）");
                lastMax = max;
            }
            Assert.Greater(lastMax, 0);   // 排 ladder 全 0 的退化表
        }

        [Test]
        public void Default_PowerLevelsWithin1To6()
        {
            // 占位阶梯：普通怪 1 → 精英 4/5 → Boss 6（战绩页展示档位）
            foreach (var entry in EnemyTable.Default.Entries)
                foreach (var s in entry.Spawns)
                {
                    Assert.GreaterOrEqual(s.PowerLevel, 1);
                    Assert.LessOrEqual(s.PowerLevel, 6);
                }
        }

        [Test]
        public void Get_UnconfiguredRound_ReturnsEmpty()
        {
            var t = Table(Spawn(new Pos(3, 4)));
            Assert.IsFalse(t.Get(1).IsEmpty);
            Assert.IsTrue(t.Get(2).IsEmpty);     // 未配置回合：空编成不生成敌人（不抛异常）
            Assert.IsTrue(t.Get(15).IsEmpty);    // 表外回合同语义（回合边界由 GameSession 管住）
        }

        // ── 构造校验（fail fast：建表事故在构造期炸，比战斗期炸便宜）──

        [Test]
        public void Ctor_RoundNotPositive_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                new EnemyTable(new[] { new EnemyRoundEntry(0, new[] { Spawn(new Pos(3, 4)) }) }));
            Assert.Throws<ArgumentException>(() =>
                new EnemyTable(new[] { new EnemyRoundEntry(-1, new[] { Spawn(new Pos(3, 4)) }) }));
        }

        [Test]
        public void Ctor_DuplicateRound_Throws()
        {
            Assert.Throws<ArgumentException>(() => new EnemyTable(new[]
            {
                new EnemyRoundEntry(1, new[] { Spawn(new Pos(3, 4)) }),
                new EnemyRoundEntry(1, new[] { Spawn(new Pos(4, 4)) }),
            }));
        }

        [Test]
        public void Ctor_EmptySpawns_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                new EnemyTable(new[] { new EnemyRoundEntry(1, new EnemySpawn[0]) }));
            Assert.Throws<ArgumentException>(() =>
                new EnemyTable(new[] { new EnemyRoundEntry(1, null) }));   // "不想生成敌人 = 不登记该回合"
        }

        [Test]
        public void Ctor_InvalidStats_Throw()
        {
            var badHp = ValidStats; badHp.MaxHp = 0;
            Assert.Throws<ArgumentException>(() => Table(SpawnWithStats(badHp)));

            var badAttackIv = ValidStats; badAttackIv.AttackInterval = 0f;
            Assert.Throws<ArgumentException>(() => Table(SpawnWithStats(badAttackIv)));

            var badMoveIv = ValidStats; badMoveIv.MoveInterval = -1f;
            Assert.Throws<ArgumentException>(() => Table(SpawnWithStats(badMoveIv)));   // 间隔是 CombatSystem 的除数
        }

        [Test]
        public void Ctor_InvalidMetadata_Throw()
        {
            Assert.Throws<ArgumentException>(() => Table(Spawn(new Pos(3, 4), oreTier: 4)));    // 挡位只有 1~3
            Assert.Throws<ArgumentException>(() => Table(Spawn(new Pos(3, 4), oreTier: -1)));
            Assert.Throws<ArgumentException>(() => Table(Spawn(new Pos(3, 4), powerLevel: 0)));
            Assert.Throws<ArgumentException>(() => Table(Spawn(new Pos(8, 4))));   // X 越界（宽 8，合法 0~7）
            Assert.Throws<ArgumentException>(() => Table(Spawn(new Pos(3, 6))));   // Y 越界（高 6，合法 0~5）
        }

        [Test]
        public void Entries_AreDefensiveCopies()
        {
            var t = Table(Spawn(new Pos(3, 4)));
            var snapshot = t.Entries;
            snapshot[0] = new EnemyRoundEntry(99, new[] { Spawn(new Pos(5, 5)) });   // 改的是拷贝
            Assert.AreEqual(1, t.Entries.Length);
            Assert.AreEqual(1, t.Entries[0].Round);
            Assert.IsFalse(t.Get(1).IsEmpty);   // 表本体纹丝不动
        }
    }
}
