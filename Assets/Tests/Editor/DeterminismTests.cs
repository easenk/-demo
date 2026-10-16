using System.Collections.Generic;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    /// <summary>
    /// 批次6 · 确定性总验收（阶段1 完成标准）。
    /// 同种子百场事件流逐条一致 = 快照重演制式回放系统的生命线。
    /// 没有确定性，就没有回放——这三条测试就是那张"出生证"。
    /// </summary>
    [TestFixture]
    public class DeterminismTests
    {
        // ─────────── 配置：一场"全路径参与"的战斗 ───────────

        private static UnitStats Stats(int maxHp, int attack, int armor, int range,
            float atkInterval, float moveInterval, int crit,
            int maxMana = 0, int manaPerHit = 0, int regenPerSecond = 0)
        {
            return new UnitStats
            {
                MaxHp = maxHp,
                AttackPower = attack,
                Armor = armor,
                MagicResist = armor / 2, // 无魔法伤害源，仅占位
                AbilityPower = 100,
                AttackRange = range,
                AttackInterval = atkInterval,
                MoveInterval = moveInterval,
                CritChance = crit,
                CritDamage = 50,
                MaxMana = maxMana,
                ManaPerHit = manaPerHit,
                ManaRegenPerSecond = regenPerSecond,
            };
        }

        private static CombatSystem BuildBattle(EventBus bus, BoardData board, uint seed)
        {
            // 玩家侧（三职业齐全：坦克/射手/术士）
            var pTank = new Unit(1, 1, true, Stats(200, 20, 30, 1, 1.0f, 1.0f, 50), bus);
            var pArcher = new Unit(2, 1, true, Stats(80, 30, 10, 3, 1.2f, 1.1f, 30), bus);
            var pWarlock = new Unit(3, 1, true, Stats(100, 25, 20, 2, 1.5f, 1.0f, 5,
                maxMana: 50, manaPerHit: 7, regenPerSecond: 2), bus);

            // 敌方（稍弱镜像）
            var eTank = new Unit(4, 1, false, Stats(180, 18, 25, 1, 1.1f, 1.0f, 40), bus);
            var eArcher = new Unit(5, 1, false, Stats(70, 28, 8, 3, 1.3f, 1.2f, 25), bus);
            var eWarlock = new Unit(6, 1, false, Stats(90, 22, 15, 2, 1.6f, 1.1f, 5,
                maxMana: 40, manaPerHit: 7, regenPerSecond: 1), bus);

            // 摆位（玩家 row 0~2，敌方 row 3~5；棋盘 8×6）
            board.TryPlace(pTank, new Pos(0, 0));
            board.TryPlace(pArcher, new Pos(3, 1));
            board.TryPlace(pWarlock, new Pos(6, 2));
            board.TryPlace(eTank, new Pos(0, 5));
            board.TryPlace(eArcher, new Pos(3, 4));
            board.TryPlace(eWarlock, new Pos(6, 3));

            // 吸血规则让 CombatRules 的确定性记账也参与
            var rules = new CombatRules { PlayerLifestealPercent = 10f };
            return new CombatSystem(board, bus, seed, rules);
        }

        // ─────────── 工具：事件流捕获与跑场 ───────────

        private static void CaptureAllEvents(EventBus bus, List<string> flow)
        {
            bus.Subscribe<UnitPlacedEvent>(e => flow.Add($"Placed:{e.UnitId}:{e.Cell.X},{e.Cell.Y}:{e.IsPlayerSide}"));
            bus.Subscribe<UnitMovedEvent>(e => flow.Add($"Moved:{e.UnitId}:{e.FromCell.X},{e.FromCell.Y}->{e.ToCell.X},{e.ToCell.Y}"));
            bus.Subscribe<UnitAttackedEvent>(e => flow.Add($"Attacked:{e.AttackerId}:{e.TargetId}:{e.IsCrit}"));
            bus.Subscribe<UnitDamagedEvent>(e => flow.Add($"Damaged:{e.UnitId}:{e.SourceUnitId}:{e.FinalDamage}:{e.CurrentHp}"));
            bus.Subscribe<UnitDiedEvent>(e => flow.Add($"Died:{e.UnitId}:{e.Cell.X},{e.Cell.Y}"));
            bus.Subscribe<UnitRemovedEvent>(e => flow.Add($"Removed:{e.UnitId}:{e.Cell.X},{e.Cell.Y}"));
            bus.Subscribe<UnitHealedEvent>(e => flow.Add($"Healed:{e.UnitId}:{e.Amount}:{e.CurrentHp}"));
            bus.Subscribe<UnitMaxHpChangedEvent>(e => flow.Add($"MaxHpChanged:{e.UnitId}:{e.MaxHp}:{e.CurrentHp}"));
            bus.Subscribe<UnitRestoredEvent>(e => flow.Add($"Restored:{e.UnitId}:{e.CurrentHp}"));
            bus.Subscribe<ManaChangedEvent>(e => flow.Add($"Mana:{e.UnitId}:{e.CurrentMana}:{e.MaxMana}"));
            bus.Subscribe<BattleEndedEvent>(e => flow.Add($"Ended:{e.PlayerWon}:{e.TotalTicks}"));
        }

        private static List<string> RunOneBattle(uint seed)
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var flow = new List<string>();
            CaptureAllEvents(bus, flow);
            var combat = BuildBattle(bus, board, seed);
            for (int i = 0; i < GameConst.MaxTicks + 10 && !combat.IsOver; i++)
                combat.Tick();
            return flow;
        }

        // ─────────── 测试 1：同种子百场，事件流逐条一致 ───────────

        [Test]
        public void SameSeedHundredBattles_AllEventsIdentical()
        {
            const uint seed = 20261016u;
            const int battleCount = 100;

            List<string> baseline = null;
            int baselineTicks = -1;
            bool baselineWon = false;
            int baselineRolls = -1;

            for (int battle = 0; battle < battleCount; battle++)
            {
                var bus = new EventBus();
                var board = new BoardData(bus);
                var flow = new List<string>();
                CaptureAllEvents(bus, flow);
                var combat = BuildBattle(bus, board, seed);
                for (int i = 0; i < GameConst.MaxTicks + 10 && !combat.IsOver; i++)
                    combat.Tick();

                if (baseline == null)
                {
                    baseline = flow;
                    baselineTicks = combat.TickCount;
                    baselineWon = combat.PlayerWon;
                    baselineRolls = combat.RollCount;
                }
                else
                {
                    CollectionAssert.AreEqual(baseline, flow,
                        $"第 {battle + 1} 场与第 1 场事件流分歧（确定性被破坏）");
                    Assert.AreEqual(baselineTicks, combat.TickCount,
                        $"第 {battle + 1} 场步数不一致");
                    Assert.AreEqual(baselineWon, combat.PlayerWon,
                        $"第 {battle + 1} 场胜负不一致");
                    Assert.AreEqual(baselineRolls, combat.RollCount,
                        $"第 {battle + 1} 场掷骰次数不一致");
                }
            }

            // ── 确认所有确定性路径都真正参与了（否则测试空转）──

            // 暴击确实掷了骰（随机序列参与了战斗）
            Assert.Greater(baselineRolls, 0,
                "掷骰次数必须>0：暴击率5%~50%的六单位战斗，掷骰必然发生");

            // 暴击确实发生过至少一次（IsCrit=true 的攻击存在）
            bool anyCrit = baseline.Exists(
                line => line.StartsWith("Attacked:") && line.EndsWith(":True"));
            Assert.IsTrue(anyCrit, "事件流中应存在暴击攻击（IsCrit=true）——证明暴击真的发生过");

            // 回蓝路径参与了（整数点数账本）
            bool anyMana = baseline.Exists(line => line.StartsWith("Mana:"));
            Assert.IsTrue(anyMana, "事件流中应存在蓝量变化——回蓝整数账本路径参与");

            // 吸血路径参与了（CombatRules 的确定性记账）
            bool anyHeal = baseline.Exists(line => line.StartsWith("Healed:"));
            Assert.IsTrue(anyHeal, "事件流中应存在吸血回复——CombatRules 的确定性记账参与");

            // 移动路径参与了（近战从远走近）
            bool anyMove = baseline.Exists(line => line.StartsWith("Moved:"));
            Assert.IsTrue(anyMove, "事件流中应存在移动——近战从 5 格外走近的路程参与");

            // 伤害路径参与了（战斗真的打了）
            bool anyDamage = baseline.Exists(line => line.StartsWith("Damaged:"));
            Assert.IsTrue(anyDamage, "事件流中应存在伤害——战斗真的打了");
        }

        // ─────────── 测试 2：不同种子，事件流应当不同 ───────────

        [Test]
        public void DifferentSeed_BattleFlowDiffers()
        {
            var flow1 = RunOneBattle(20261016u);
            var flow2 = RunOneBattle(987654321u);

            // 逐行找分歧：至少有一行不同（长度不同也算）
            bool anyDiff = false;
            int min = System.Math.Min(flow1.Count, flow2.Count);
            for (int i = 0; i < min; i++)
            {
                if (flow1[i] != flow2[i]) { anyDiff = true; break; }
            }
            if (!anyDiff && flow1.Count != flow2.Count) anyDiff = true;

            Assert.IsTrue(anyDiff,
                "不同种子应产生不同事件流——同流意味着随机源没有真正参与战斗");
        }

        // ─────────── 测试 3：分批调用 ≠ 一口气，结果必须一致 ───────────

        [Test]
        public void BatchedVsContinuousTicking_IdenticalResults()
        {
            const uint seed = 20261016u;

            // 连续跑：一口气跑完
            var continuous = RunOneBattle(seed);

            // 分批跑：先 30 步、暂停（模拟帧间隙/保险丝追赶）、再一口气跑完
            var batched = new List<string>();
            {
                var bus = new EventBus();
                var board = new BoardData(bus);
                CaptureAllEvents(bus, batched);
                var combat = BuildBattle(bus, board, seed);

                for (int i = 0; i < 30 && !combat.IsOver; i++) combat.Tick();
                // "暂停"——什么都不做；下一帧继续
                for (int i = 0; i < GameConst.MaxTicks + 10 && !combat.IsOver; i++) combat.Tick();
            }

            CollectionAssert.AreEqual(continuous, batched,
                "分批调用 Tick 不应影响结果——表现层驱动器可任意帧率/倍速/保险丝追赶");
        }
    }
}
