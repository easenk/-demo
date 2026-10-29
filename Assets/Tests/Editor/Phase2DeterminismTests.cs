using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    /// <summary>
    /// 阶段2 批次8：确定性总验收（收官）——把"同种子同操作=同结果"从单系统抽查升级为全玩法贯通的端到端验收。
    /// 这是阶段5 快照重演（回放）的地基验收：整局（买/刷/卖/摆位/上下场/捡矿/选策略/合成）双 Session 对比。
    ///
    /// 结构：
    ///  · 全 Default 真实对局（角色 18 棋 / 编成 14 回合真怪 / 六族羁绊 / 矿池 / 策略两方向）——不注入小表
    ///  · 决策器（独立 GameRandom，与被测会话随机流分家）驱动脚本随机分支；同局双 Session 拿同一决策器种子
    ///    → 操作序列必然相同；尝试性操作被拒也是序列的一部分
    ///  · 正确性论证（归纳）：状态同步 → 脚本读到的状态相同 → 决策器消耗节奏相同 → 操作相同 → 状态继续同步
    ///
    /// 分节契约：
    ///  · 主验收（20 局）：事件流逐条一致（30 种事件全捕获、每条拼全字段；先整体快速比对、失败定位首处差异）
    ///    + 终局五元组一致（血/金/回合/评级/已选策略清单）+ 每局 RunOver（防"卡 Settlement 的假完整"）
    ///    + 事件流规模下限（防"零事件假绿"）
    ///  · 操作覆盖（20 局累计）：买/刷/卖/摆位/上下场/捡矿/选策略/合成/买经验/羁绊激活/晶矿掉落/策略弹选
    ///    至少各一次——防决策器 20 局集体绕过某类操作使验收覆盖面作废
    /// </summary>
    [TestFixture]
    public class Phase2DeterminismTests
    {
        // ─────────────────────── 测试基建：事件全捕获 ───────────────────────

        /// <summary>30 种事件全订阅，每条拼全字段成一行（int/bool/枚举全可字符串化——无浮点，逐字节可比）。</summary>
        private sealed class EventRecorder
        {
            public readonly List<string> Log = new List<string>(8192);
            public bool SynergyActivated;   // 覆盖统计：任一族激活档出现过（TierIndex >= 0）

            private void Add(string line) => Log.Add(line);

            public void SubscribeAll(EventBus bus)
            {
                bus.Subscribe<UnitPlacedEvent>(e => Add($"Placed|{e.UnitId}|{e.Cell.X},{e.Cell.Y}|{e.IsPlayerSide}"));
                bus.Subscribe<UnitMovedEvent>(e => Add($"Moved|{e.UnitId}|{e.FromCell.X},{e.FromCell.Y}|{e.ToCell.X},{e.ToCell.Y}"));
                bus.Subscribe<UnitRemovedEvent>(e => Add($"Removed|{e.UnitId}|{e.Cell.X},{e.Cell.Y}"));
                bus.Subscribe<UnitAttackedEvent>(e => Add($"Attacked|{e.AttackerId}|{e.TargetId}|{e.IsCrit}"));
                bus.Subscribe<SkillCastEvent>(e => Add($"SkillCast|{e.CasterId}|{e.SkillId}|{e.TargetId}|{e.IsCrit}"));
                bus.Subscribe<UnitDamagedEvent>(e => Add($"Damaged|{e.UnitId}|{e.SourceUnitId}|{e.FinalDamage}|{e.CurrentHp}"));
                bus.Subscribe<UnitDiedEvent>(e => Add($"Died|{e.UnitId}|{e.Cell.X},{e.Cell.Y}"));
                bus.Subscribe<UnitHealedEvent>(e => Add($"Healed|{e.UnitId}|{e.Amount}|{e.CurrentHp}"));
                bus.Subscribe<UnitMaxHpChangedEvent>(e => Add($"MaxHp|{e.UnitId}|{e.MaxHp}|{e.CurrentHp}"));
                bus.Subscribe<UnitRestoredEvent>(e => Add($"Restored|{e.UnitId}|{e.CurrentHp}"));
                bus.Subscribe<BattleEndedEvent>(e => Add($"BattleEnded|{e.PlayerWon}|{e.TotalTicks}"));
                bus.Subscribe<ManaChangedEvent>(e => Add($"Mana|{e.UnitId}|{e.CurrentMana}|{e.MaxMana}"));
                bus.Subscribe<GoldChangedEvent>(e => Add($"Gold|{e.Gold}|{e.Delta}"));
                bus.Subscribe<XpChangedEvent>(e => Add($"Xp|{e.Level}|{e.XpProgress}|{e.XpToNextLevel}"));
                bus.Subscribe<LevelUpEvent>(e => Add($"LevelUp|{e.Level}"));
                bus.Subscribe<IncomeSettledEvent>(e => Add(
                    $"Income|{e.Bill.FixedGold}|{e.Bill.InterestGold}|{e.Bill.StreakGold}|{e.Bill.TotalGold}|{e.Bill.WinStreak}"));
                bus.Subscribe<ShopRolledEvent>(e => Add("Rolled|" + string.Join(",", e.CharIds)));
                bus.Subscribe<ShopBoughtEvent>(e => Add($"Bought|{e.SlotIndex}|{e.CharId}|{e.GoldLeft}"));
                bus.Subscribe<SynergiesChangedEvent>(e =>
                {
                    var sb = new StringBuilder("Synergies");
                    foreach (var info in e.Snapshot) sb.Append($"|{info.SynergyId}:{info.Count}:{info.TierIndex}");
                    Add(sb.ToString());
                    if (e.Snapshot.Any(x => x.TierIndex >= 0)) SynergyActivated = true;
                });
                bus.Subscribe<HpChangedEvent>(e => Add($"Hp|{e.Hp}|{e.Delta}"));
                bus.Subscribe<UnitAcquiredEvent>(e => Add($"Acquired|{e.UnitId}|{e.CharId}|{e.Star}"));
                bus.Subscribe<UnitSoldEvent>(e => Add($"Sold|{e.UnitId}|{e.CharId}|{e.Star}|{e.Gold}"));
                bus.Subscribe<UnitMergedEvent>(e => Add(
                    $"Merged|{e.ResultUnitId}|{e.CharId}|{e.NewStar}|" + string.Join(",", e.ConsumedUnitIds)));
                bus.Subscribe<RoundStartedEvent>(e => Add($"RoundStarted|{e.RoundNumber}|{e.Type}"));
                bus.Subscribe<RoundEndedEvent>(e => Add($"RoundEnded|{e.Round}|{e.PlayerWon}|{e.DamageTaken}|{e.GoldGained}"));
                bus.Subscribe<RunFinishedEvent>(e => Add($"RunFinished|{e.Rating}|{e.RoundsCompleted}|{e.FinalHp}"));
                bus.Subscribe<OreDroppedEvent>(e => Add($"OreDropped|{e.OreId}|{e.Tier}|{e.Cell.X},{e.Cell.Y}"));
                bus.Subscribe<OreOpenedEvent>(e => Add(
                    $"OreOpened|{e.OreId}|{e.Tier}|{e.Cell.X},{e.Cell.Y}|{e.OreDefId}|{e.RewardType}|{e.Amount}|{e.EquipmentId}"));
                bus.Subscribe<PolicyOfferedEvent>(e => Add("PolicyOffered|" + string.Join(",", e.CandidatePolicyIds)));
                bus.Subscribe<PolicyPickedEvent>(e => Add($"PolicyPicked|{e.Index}|{e.PolicyId}|{e.InstanceSourceId}"));
            }

            /// <summary>首处差异定位（整体比对失败后调用，报行号与两行内容）。</summary>
            public static string FirstDifference(List<string> a, List<string> b)
            {
                int n = Math.Min(a.Count, b.Count);
                for (int i = 0; i < n; i++)
                    if (a[i] != b[i]) return $"第 {i} 行：A[{a[i]}] vs B[{b[i]}]";
                return a.Count != b.Count ? $"条数 {a.Count} vs {b.Count}（公共前缀全同）" : "无差异";
            }
        }

        // ─────────────────────── 测试基建：操作覆盖统计 ───────────────────────

        private sealed class Coverage
        {
            public int Buys, Refreshes, Sells, Moves, PlaceUps, TakeDowns, PickOres, PickPolicies, BuyXps;
            public int Merges, OreDrops, PolicyOffers;
            public bool SynergyActivated;

            public void Add(Coverage o)
            {
                Buys += o.Buys; Refreshes += o.Refreshes; Sells += o.Sells; Moves += o.Moves;
                PlaceUps += o.PlaceUps; TakeDowns += o.TakeDowns; PickOres += o.PickOres;
                PickPolicies += o.PickPolicies; BuyXps += o.BuyXps;
                Merges += o.Merges; OreDrops += o.OreDrops; PolicyOffers += o.PolicyOffers;
                SynergyActivated |= o.SynergyActivated;
            }

            public string Missing()
            {
                var missing = new List<string>();
                if (Buys < 1) missing.Add("买入");
                if (Refreshes < 1) missing.Add("刷新");
                if (Sells < 1) missing.Add("卖出");
                if (Moves < 1) missing.Add("挪位");
                if (PlaceUps < 1) missing.Add("上场");
                if (TakeDowns < 1) missing.Add("下场");
                if (PickOres < 1) missing.Add("捡矿");
                if (PickPolicies < 1) missing.Add("选策略");
                if (BuyXps < 1) missing.Add("买经验");
                if (Merges < 1) missing.Add("合成");
                if (OreDrops < 1) missing.Add("晶矿掉落");
                if (PolicyOffers < 1) missing.Add("策略弹选");
                if (!SynergyActivated) missing.Add("羁绊激活");
                return string.Join("、", missing);
            }
        }

        // ─────────────────────── 脚本化一局 ───────────────────────

        /// <summary>
        /// 跑一整局到 RunOver（胜/负终局都算完整）。操作 = 规则骨架 + 决策器随机分支
        /// （决策器种子独立于会话种子——被测的是会话随机流，决策器是"玩家之手"）。
        /// 返回 (会话, 事件记录器, 覆盖统计)。
        /// </summary>
        private static (GameSession s, EventRecorder rec, Coverage cov) PlayFullRun(uint sessionSeed, uint deciderSeed)
        {
            var bus = new EventBus();
            var rec = new EventRecorder();
            rec.SubscribeAll(bus);
            var s = new GameSession(bus, sessionSeed, GameTables.Default);   // 全 Default 真实对局
            var decider = new GameRandom(deciderSeed);
            var cov = new Coverage();

            // 覆盖统计从事件流数（事件层覆盖=玩法层发生过的证据；脚本层操作数另行累计）
            Assert.IsTrue(s.StartRun());

            int guard = 0;
            while (!s.RunOver)
            {
                Assert.Less(guard++, 200, "回合循环异常长");
                if (s.Phase == GamePhase.Settlement) { Assert.IsTrue(s.StartPrep()); continue; }
                Assert.AreEqual(GamePhase.Prep, s.Phase, "脚本只在备战期操作");

                // 1) 捡光矿（晶矿门禁）
                while (s.OresOnGround.Count > 0)
                {
                    Assert.IsTrue(s.PickOre(s.OresOnGround[0].Id), "备战期捡矿");
                    cov.PickOres++;
                }

                // 2) 选掉待选策略（策略门禁；决策器选格——有 1 条候选时 NextInt(1)=0 恒定）
                if (s.HasPolicyChoice)
                {
                    Assert.IsTrue(s.PickPolicy(decider.NextInt(s.PolicyCandidates.Count)), "备战期选策略");
                    cov.PickPolicies++;
                }

                // 3) 买经验（低概率 1/4——升级带动板位与概率表变化）
                if (decider.NextInt(4) == 0 && s.BuyXp()) cov.BuyXps++;

                // 4) 刷新 0~2 次
                int refreshes = decider.NextInt(3);
                for (int i = 0; i < refreshes; i++)
                    if (s.RefreshShop()) cov.Refreshes++;

                // 5) 买入 0~3 张（决策器选格；被拒也是序列的一部分——两 Session 同样被拒）
                int buys = decider.NextInt(4);
                for (int i = 0; i < buys; i++)
                    if (s.Buy(decider.NextInt(ShopSystem.Slots))) cov.Buys++;

                // 6) 上场尝试 0~2 次（选备战棋 + 随机己方半场格）
                int ups = decider.NextInt(3);
                for (int i = 0; i < ups; i++)
                {
                    if (s.Bench.Count == 0) break;   // 状态同步 → 两 Session 同时 break → 决策器节奏仍同步
                    int idx = decider.NextInt(s.Bench.Count);
                    if (s.PlaceOnBoard(s.Bench[idx].Id, new Pos(decider.NextInt(8), decider.NextInt(3)))) cov.PlaceUps++;
                }

                // 7) 挪位尝试（1/2 概率）
                if (s.PlayerBoard.Count > 0 && decider.NextInt(2) == 0)
                {
                    int idx = decider.NextInt(s.PlayerBoard.Count);
                    if (s.MoveOnBoard(s.PlayerBoard[idx].Id, new Pos(decider.NextInt(8), decider.NextInt(3)))) cov.Moves++;
                }

                // 8) 下场尝试（1/6 概率）
                if (s.PlayerBoard.Count > 0 && decider.NextInt(6) == 0)
                {
                    int idx = decider.NextInt(s.PlayerBoard.Count);
                    if (s.TakeOffBoard(s.PlayerBoard[idx].Id)) cov.TakeDowns++;
                }

                // 9) 卖出尝试（1/6 概率；随机卖备战或场上）
                int roster = s.Bench.Count + s.PlayerBoard.Count;
                if (roster > 0 && decider.NextInt(6) == 0)
                {
                    int idx = decider.NextInt(roster);
                    Unit u = idx < s.Bench.Count ? s.Bench[idx] : s.PlayerBoard[idx - s.Bench.Count];
                    if (s.Sell(u.Id)) cov.Sells++;
                }

                // 10) 开战（双门禁已被脚本消费）→ 驱动到分晓 → 排空回调结算
                Assert.IsTrue(s.StartBattle(), $"R{s.Round} 开战应成功（脚本已消费双门禁）");
                if (s.Phase == GamePhase.Battle)
                {
                    int tg = 0;
                    while (!s.Combat.IsOver && tg++ < 6000) s.Combat.Tick();
                    Assert.IsTrue(s.Combat.IsOver, "MaxTicks 兜底必然分晓");
                    Assert.IsTrue(s.SettleBattle(), "排空回调结算");
                }
            }

            // 事件层覆盖统计
            cov.Merges = rec.Log.Count(l => l.StartsWith("Merged|", StringComparison.Ordinal));
            cov.OreDrops = rec.Log.Count(l => l.StartsWith("OreDropped|", StringComparison.Ordinal));
            cov.PolicyOffers = rec.Log.Count(l => l.StartsWith("PolicyOffered|", StringComparison.Ordinal));
            cov.SynergyActivated = rec.SynergyActivated;
            return (s, rec, cov);
        }

        // ─────────────────────── 主验收：20 局双 Session 逐条一致 ───────────────────────

        [Test]
        public void FullRun_SameSeedSameScript_IdenticalStreamsAndFinalState()
        {
            for (int run = 0; run < 20; run++)
            {
                uint sessionSeed = (uint)(1000 + run * 7);
                uint deciderSeed = (uint)(77 + run * 13);
                var a = PlayFullRun(sessionSeed, deciderSeed);
                var b = PlayFullRun(sessionSeed, deciderSeed);

                // 完整性：每局必须跑到终局（胜/负皆可——提前血尽也是完整局）
                Assert.AreEqual(GamePhase.RunOver, a.s.Phase, $"第 {run} 局 A 必须跑到终局");
                Assert.AreEqual(GamePhase.RunOver, b.s.Phase, $"第 {run} 局 B 必须跑到终局");

                // 事件流逐条一致（先整体快速比对，失败再定位首处差异）
                bool same = a.rec.Log.Count == b.rec.Log.Count;
                if (same)
                    for (int i = 0; i < a.rec.Log.Count && same; i++)
                        same = string.Equals(a.rec.Log[i], b.rec.Log[i], StringComparison.Ordinal);
                Assert.IsTrue(same, $"第 {run} 局事件流不一致——{EventRecorder.FirstDifference(a.rec.Log, b.rec.Log)}");

                // 事件流规模下限（防"零事件假绿"：真实对局必然有回合/战斗/结算事件）
                Assert.Greater(a.rec.Log.Count, 100, $"第 {run} 局事件流规模下限（实际 {a.rec.Log.Count} 条）");

                // 终局五元组一致（+名册规模）
                Assert.AreEqual(a.s.PlayerHp, b.s.PlayerHp, $"第 {run} 局终局血量一致");
                Assert.AreEqual(a.s.Eco.Gold, b.s.Eco.Gold, $"第 {run} 局终局金币一致");
                Assert.AreEqual(a.s.Round, b.s.Round, $"第 {run} 局终局回合数一致");
                Assert.AreEqual(a.s.FinalRating, b.s.FinalRating, $"第 {run} 局终局评级一致");
                CollectionAssert.AreEqual(a.s.OwnedPolicyIds, b.s.OwnedPolicyIds, $"第 {run} 局已选策略清单一致");
                Assert.AreEqual(a.s.Bench.Count + a.s.PlayerBoard.Count,
                    b.s.Bench.Count + b.s.PlayerBoard.Count, $"第 {run} 局终局名册规模一致");
            }
        }

        // ─────────────────────── 操作覆盖：20 局累计防退化 ───────────────────────

        [Test]
        public void OperationCoverage_AllOpsExercisedAcrossRuns()
        {
            var total = new Coverage();
            for (int run = 0; run < 20; run++)
            {
                uint sessionSeed = (uint)(1000 + run * 7);
                uint deciderSeed = (uint)(77 + run * 13);
                var a = PlayFullRun(sessionSeed, deciderSeed);
                total.Add(a.cov);
            }

            string missing = total.Missing();
            Assert.IsEmpty(missing,
                $"20 局累计操作覆盖不足——缺：{missing}（若红：调决策器概率分布后重跑，覆盖面作废=验收作废）");
        }
    }
}
