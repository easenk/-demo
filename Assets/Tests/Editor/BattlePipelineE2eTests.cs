using System;
using System.Collections.Generic;
using AutoChess.Game;
using AutoChess.Presentation;
using NUnit.Framework;
using UnityEditor;

namespace AutoChess.Tests
{
    /// <summary>
    /// 批次4F e2e 语义不变式（战斗管线整场验收的自动化一半）。
    /// 与 PlannerTests 的分工：那边锁「合成流单案的构造规则」，本文件锁「真数据整场无一处越界」——
    /// 真表（GameTables.Default）+ 真棋子采购（远程弹道与技能大户优先，BattleRoot 调试驱动同款）+
    /// 真烘焙 SO 截窗时刻表（BattleRoot.BuildTimingTable 生产链路同款），整场预演→规划→
    /// 演出计划全链跑通后，对时间轴与计划程序化断言五语义（文档第一节完成标准）：
    ///  · 移动播完才攻击 —— I1 动作区间不重叠（四检查器，真时刻表下重跑）；
    ///  · 箭落地才见血 —— 弹体出弹在攻击窗内、到达=配对伤害交付时刻、远程单位逐攻击一弹；
    ///  · 同目标无倒置 —— I2 交付链严格递增；
    ///  · 无打尸体 —— 死后无新交付（Death 必为该单位交付链最后一条）；
    ///  · 倒地前格不让位 —— I3/I4 占位互斥。
    /// 排空才进结算=闸门运行时语义，归 DrainGateTests 与真台整场录像验收，不在本文件面。
    /// 检查器与布阵格表按「各文件自持」惯例拷贝自 PlannerTests / BattleRoot。
    /// </summary>
    public class BattlePipelineE2eTests
    {
        private const uint SeedA = 20261104u;   // 与 BattleRoot 调试驱动同种子（真台验收口径）

        // ── 布阵格表（BattleRoot 同款拷贝：近战贴中线 X3 列、远程纵深——半场左右分 2026-11-04 拍板） ──
        private static readonly Pos[] FrontCells =
        {
            new Pos(3, 2), new Pos(3, 3), new Pos(3, 1), new Pos(3, 4), new Pos(3, 0), new Pos(3, 5),
            new Pos(2, 0), new Pos(2, 5),
        };
        private static readonly Pos[] BackCells =
        {
            new Pos(2, 2), new Pos(2, 3), new Pos(1, 2), new Pos(1, 3),
            new Pos(2, 1), new Pos(2, 4), new Pos(1, 1), new Pos(1, 4),
            new Pos(0, 2), new Pos(0, 3),
        };

        // ─────────────────────── 配置锁（4F 共款弹体分色与缩放） ───────────────────────

        [Test]
        public void E2E_SharedProjectileConfig_ScaleAndTintLocked()
        {
            // 缩放：闪电黑洞普攻档 0.65（技能档不在本表）；其余普攻 1
            Assert.AreEqual(0.65f, EffectVisualConfig.ProjectileScaleForChar(4));
            Assert.AreEqual(0.65f, EffectVisualConfig.ProjectileScaleForChar(16));
            Assert.AreEqual(1f, EffectVisualConfig.ProjectileScaleForChar(2));
            Assert.AreEqual(1f, EffectVisualConfig.ProjectileScaleForChar(8));
            Assert.AreEqual(1f, EffectVisualConfig.ProjectileScaleForChar(11));   // 近战棋子走本表也不放大

            // 分色：共款位有色、独占位黑（Color.black=不染）
            Assert.AreEqual(UnityEngine.Color.black, EffectVisualConfig.ProjectileTintForChar(2));
            Assert.AreEqual(new UnityEngine.Color(0.35f, 0.75f, 1f), EffectVisualConfig.ProjectileTintForChar(9));
            Assert.AreEqual(new UnityEngine.Color(1f, 0.85f, 0.3f), EffectVisualConfig.ProjectileTintForChar(12));
            Assert.AreEqual(UnityEngine.Color.black, EffectVisualConfig.ProjectileTintForChar(5));
            Assert.AreEqual(new UnityEngine.Color(1f, 0.5f, 0.1f), EffectVisualConfig.ProjectileTintForChar(13));
            Assert.AreEqual(UnityEngine.Color.black, EffectVisualConfig.ProjectileTintForChar(16));
        }

        // ─────────────────────── R1：真表整场全链断言 ───────────────────────

        [Test]
        public void E2E_R1_RealTables_AllInvariantsHold()
        {
            RoundData rd = RunRealRound(SeedA);
            AssertRoundInvariants(rd, minNodes: 30);
        }

        [Test]
        public void E2E_R2_RealTables_AllInvariantsHold()
        {
            RoundData first = RunRealRound(SeedA);
            AssertRoundInvariants(first, minNodes: 30);

            // R1 结算 → 备战 → 消费门禁（捡矿+选策略，BattleRoot.ConsumeGates 同款）→ R2 再战
            Assert.IsTrue(first.Session.SettleBattle(), "R1 结算");
            Assert.IsTrue(first.Session.StartPrep(), "R2 备战");
            ConsumeGates(first.Session);
            RoundData second = PreviewRound(first.Session);
            AssertRoundInvariants(second, minNodes: 30);
        }

        // ─────────────────────── 整场断言（四检查器 + 死后无交付 + 弹道 + 掉矿） ───────────────────────

        private struct RoundData
        {
            public GameSession Session;
            public BattlePreviewResult Result;
            public PlannedTimeline Timeline;
            public ProjectileEffectPlan Plan;
            public Dictionary<int, int> CharIds;
            public Dictionary<int, ResolvedUnitVisual> Visuals;
            public BattleSnapshot SeedFrame;   // Release 前捕获（#23② 释放会清快照序列——事后取 [0] 必越界）
        }

        private static void AssertRoundInvariants(RoundData rd, int minNodes)
        {
            Assert.Greater(rd.Timeline.Count, minNodes, "事件规模下限（真台 R1 口径 74 节点）");
            CheckActionSerial(rd.Timeline);
            CheckDeliveryChain(rd.Timeline);
            CheckAttackPairing(rd.Timeline, BuildRealTable(rd.Result, rd.Visuals));
            CheckCellExclusivity(rd.Timeline, rd.SeedFrame);
            CheckNoDeliveryAfterDeath(rd.Timeline);
            CheckProjectiles(rd.Timeline, rd.Plan, rd.CharIds);

            // R1/R2 敌方必掉矿（宝箱怪/野狼场按表出怪——掉矿与否由编成表，这里只锁「时间轴里
            // 出现过 OreDrop 节点」的真战镜像面；无矿场由编成表保证，不在断言面）
            bool hasOre = false;
            for (int i = 0; i < rd.Timeline.Count; i++)
                if (rd.Timeline[i].Kind == PlannedNodeKind.OreDrop) { hasOre = true; break; }
            if (rd.Session.OresOnGround != null && rd.Session.OresOnGround.Count > 0)
                Assert.IsTrue(hasOre, "会话账面有矿而时间轴无 OreDrop 节点——事件流漏矿");
        }

        /// <summary>无打尸体：交付链（Damage/Heal/Mana/MaxHp）中 Death 必为该单位最后一条。</summary>
        private static void CheckNoDeliveryAfterDeath(PlannedTimeline tl)
        {
            var deathSeq = new Dictionary<int, int>();
            for (int i = 0; i < tl.Count; i++)
                if (tl[i].Kind == PlannedNodeKind.Death) deathSeq[tl[i].UnitId] = tl[i].Seq;

            for (int i = 0; i < tl.Count; i++)
            {
                PlannedNode n = tl[i];
                if (n.Kind != PlannedNodeKind.Damage && n.Kind != PlannedNodeKind.Heal
                    && n.Kind != PlannedNodeKind.Mana && n.Kind != PlannedNodeKind.MaxHp) continue;
                if (deathSeq.TryGetValue(n.UnitId, out int dSeq))
                    Assert.Less(n.Seq, dSeq,
                        $"打尸体：单位 {n.UnitId} 在死亡（Seq {dSeq}）后仍有交付（Seq {n.Seq} {n.Kind}）");
            }
        }

        /// <summary>箭落地才见血（4F 弹道三断言）：
        /// ① 远程单位每个 Attack 恰有一条弹体（数量 1:1）；
        /// ② 出弹时刻落在对应攻击的挥击窗内；
        /// ③ 到达时刻=配对伤害交付时刻（±1e-6，普攻 Seq 邻接契约）。加 FireAt ≤ ArriveAt。</summary>
        private static void CheckProjectiles(PlannedTimeline tl, ProjectileEffectPlan plan,
            Dictionary<int, int> charIds)
        {
            // 远程在场单位（charId 查弹道表非空）
            var ranged = new HashSet<int>();
            foreach (var kv in charIds)
                if (EffectVisualConfig.ProjectilePathForChar(kv.Value) != null) ranged.Add(kv.Key);

            var attacks = new Dictionary<int, List<PlannedNode>>();
            var bySeq = new Dictionary<int, PlannedNode>();
            for (int i = 0; i < tl.Count; i++)
            {
                bySeq[tl[i].Seq] = tl[i];
                if (tl[i].Kind != PlannedNodeKind.Attack) continue;
                if (!ranged.Contains(tl[i].UnitId)) continue;
                if (!attacks.TryGetValue(tl[i].UnitId, out var list)) attacks[tl[i].UnitId] = list = new List<PlannedNode>();
                list.Add(tl[i]);
            }

            var projectiles = new Dictionary<int, List<PlannedVisual>>();
            int projectileTotal = 0;
            for (int i = 0; i < plan.Count; i++)
            {
                PlannedVisual v = plan.Items[i];
                if (v.Kind != PlannedVisualKind.Projectile) continue;
                projectileTotal++;
                if (!projectiles.TryGetValue(v.UnitId, out var list)) projectiles[v.UnitId] = list = new List<PlannedVisual>();
                list.Add(v);
            }

            Assert.Greater(projectileTotal, 0, "远程单位在场必有普攻弹体");
            foreach (var kv in attacks)
            {
                Assert.IsTrue(projectiles.TryGetValue(kv.Key, out var list) && list.Count == kv.Value.Count,
                    $"远程单位 {kv.Key}：攻击 {kv.Value.Count} 次 vs 弹体 {(list?.Count ?? 0)} 条——数量 1:1 破坏");
                foreach (PlannedVisual pv in list)
                {
                    Assert.LessOrEqual(pv.FireAt, pv.ArriveAt, "弹体出弹晚于命中（序破）");
                    // 出弹必落在该单位某个攻击窗内
                    bool inWindow = false;
                    foreach (PlannedNode atk in kv.Value)
                        if (pv.FireAt >= atk.Start - 1e-6f && pv.FireAt <= atk.Start + atk.Duration + 1e-6f)
                        { inWindow = true; break; }
                    Assert.IsTrue(inWindow, $"弹体 FireAt {pv.FireAt:F3} 不在任何攻击窗内（单位 {kv.Key}）");
                    // 到达=配对伤害交付时刻；唯一例外=出弹与命中同刻的零窗口飞行被保底钳到 0.05s
                    //（4D 设计内让步——「序不破只是迟到」，血早于箭落地的上限就是这条保底）
                    foreach (PlannedNode atk in kv.Value)
                    {
                        if (pv.FireAt < atk.Start - 1e-6f || pv.FireAt > atk.Start + atk.Duration + 1e-6f) continue;
                        Assert.IsTrue(bySeq.TryGetValue(atk.Seq + 1, out PlannedNode dmg)
                            && dmg.Kind == PlannedNodeKind.Damage,
                            $"攻击 Seq {atk.Seq} 的紧邻下一条必须是配对伤害");
                        float expectedArrive = Math.Max(dmg.Start, pv.FireAt + ProjectileEffectPlan.MinFlight);
                        Assert.AreEqual(expectedArrive, pv.ArriveAt, 1e-6f,
                            $"弹体到达 {pv.ArriveAt:F3} != max(配对交付 {dmg.Start:F3}, 出弹+保底飞行)——箭落地才见血破坏");
                        Assert.LessOrEqual(pv.ArriveAt - dmg.Start, ProjectileEffectPlan.MinFlight + 1e-6f,
                            "血早于箭落地超出保底让步上限（4D 挂账 0.05s）");
                        break;
                    }
                }
            }
        }

        // ─────────────────────── I1/I2/配对/I3I4 检查器（PlannerTests 同款拷贝，各文件自持） ───────────────────────

        /// <summary>I1：按流序逐单位检查动作串行；死亡例外（可打断自身动作）且死后无动作。</summary>
        private static void CheckActionSerial(PlannedTimeline tl)
        {
            var perUnit = new Dictionary<int, List<PlannedNode>>();
            for (int i = 0; i < tl.Count; i++)
            {
                PlannedNode n = tl[i];
                if (n.Kind != PlannedNodeKind.Move && n.Kind != PlannedNodeKind.Attack
                    && n.Kind != PlannedNodeKind.Cast && n.Kind != PlannedNodeKind.Death) continue;
                if (!perUnit.TryGetValue(n.UnitId, out var list)) perUnit[n.UnitId] = list = new List<PlannedNode>();
                list.Add(n);
            }
            foreach (var kv in perUnit)
            {
                kv.Value.Sort((a, b) => a.Seq.CompareTo(b.Seq));
                bool dead = false, hasPrev = false;
                PlannedNode prev = default;
                foreach (var n in kv.Value)
                {
                    if (n.Kind == PlannedNodeKind.Death) { dead = true; continue; }
                    Assert.IsFalse(dead, $"I1 破：单位 {kv.Key} 死后仍有动作（Seq {n.Seq}）");
                    if (hasPrev)
                        Assert.GreaterOrEqual(n.Start, prev.Start + prev.Duration,
                            $"I1 破：单位 {kv.Key} 的 Seq {n.Seq} 与上一动作重叠（移动播完才攻击被破坏）");
                    prev = n; hasPrev = true;
                }
            }
        }

        /// <summary>I2：按流序逐单位检查交付链——数值交付严格递增（≥+Gap）、死亡=致死交付同刻。</summary>
        private static void CheckDeliveryChain(PlannedTimeline tl)
        {
            var perUnit = new Dictionary<int, List<PlannedNode>>();
            for (int i = 0; i < tl.Count; i++)
            {
                PlannedNode n = tl[i];
                if (n.Kind != PlannedNodeKind.Damage && n.Kind != PlannedNodeKind.Heal
                    && n.Kind != PlannedNodeKind.Mana && n.Kind != PlannedNodeKind.MaxHp
                    && n.Kind != PlannedNodeKind.Death) continue;
                if (!perUnit.TryGetValue(n.UnitId, out var list)) perUnit[n.UnitId] = list = new List<PlannedNode>();
                list.Add(n);
            }
            foreach (var kv in perUnit)
            {
                var list = kv.Value;
                list.Sort((a, b) => a.Seq.CompareTo(b.Seq));
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Kind == PlannedNodeKind.Death)
                    {
                        Assert.Greater(i, 0, $"单位 {kv.Key} 的死亡前必须有致死伤害");
                        Assert.AreEqual(PlannedNodeKind.Damage, list[i - 1].Kind,
                            $"单位 {kv.Key} 的死亡必紧跟致死伤害（流序）");
                        Assert.AreEqual(list[i - 1].Start, list[i].Start, "倒地=致死交付同刻");
                        continue;
                    }
                    if (i > 0 && list[i - 1].Kind != PlannedNodeKind.Death)
                        Assert.GreaterOrEqual(list[i].Start, list[i - 1].Start + BattlePlanner.DeliveryGap,
                            $"I2 破：单位 {kv.Key} 的 Seq {list[i].Seq} 交付未按链递增（同目标倒置）");
                }
            }
        }

        /// <summary>普攻配对契约（e2e 口径）：Seq+1 必为配对伤害（源码序）；落点=出手预扣的命中帧
        /// 与目标链尾的 max——真战多人围殴同目标时 I2 链把命中按序推后（#23 设计内行为），
        /// 故断言区间 [命中帧, 挥击窗尾] 而非精确等于（合成流单案的精确断言归 PlannerTests）。</summary>
        private static void CheckAttackPairing(PlannedTimeline tl, PlannerTimingTable timings)
        {
            var bySeq = new Dictionary<int, PlannedNode>();
            for (int i = 0; i < tl.Count; i++) bySeq[tl[i].Seq] = tl[i];
            for (int i = 0; i < tl.Count; i++)
            {
                PlannedNode n = tl[i];
                if (n.Kind != PlannedNodeKind.Attack) continue;
                Assert.IsTrue(bySeq.TryGetValue(n.Seq + 1, out PlannedNode dmg)
                    && dmg.Kind == PlannedNodeKind.Damage && dmg.UnitId == n.TargetId,
                    $"普攻 Seq {n.Seq} 的紧邻下一条必须是配对伤害（源码序契约）");
                UnitTiming t = timings.Get(n.UnitId);
                float offset = t.AttackDuration * t.AttackHitPercent;   // 运行时算术（浮点坑：禁常量折叠字面量）
                Assert.GreaterOrEqual(dmg.Start, n.Start + offset - 1e-6f,
                    $"普攻 Seq {n.Seq} 的命中早于命中帧（预扣构造破坏）");
                Assert.LessOrEqual(dmg.Start, n.Start + n.Duration + 1e-6f,
                    $"普攻 Seq {n.Seq} 的命中越出挥击窗（守约被破坏）");
            }
        }

        /// <summary>I3/I4 验收口径代码化："任一格任一时刻只有一个呈现单位"（倒地前格不让位）。
        /// 从 tick0 快照播种占用，按时间轴重放占位区间；死亡=持有到让格时刻（无让格节点=
        /// DoT 尸体持到 Makespan——批次A 挂账镜像）。区间两两不重叠（端点相接合法）。</summary>
        private static void CheckCellExclusivity(PlannedTimeline tl, BattleSnapshot seedFrame)
        {
            var intervals = new Dictionary<int, List<float[]>>();
            var openCell = new Dictionary<int, int>();
            var openSince = new Dictionary<int, float>();

            void Open(int unit, int key, float since)
            {
                openCell[unit] = key;
                openSince[unit] = since;
            }
            void Close(int unit, float until)
            {
                if (!openCell.ContainsKey(unit)) return;
                int key = openCell[unit];
                float since = openSince[unit];
                if (!intervals.TryGetValue(key, out var list)) intervals[key] = list = new List<float[]>();
                list.Add(new[] { since, until });
                openCell.Remove(unit);
                openSince.Remove(unit);
            }

            for (int i = 0; i < seedFrame.Units.Length; i++)
                Open(seedFrame.Units[i].UnitId, seedFrame.Units[i].Y * GameConst.BoardWidth + seedFrame.Units[i].X, 0f);

            var removeAt = new Dictionary<int, float>();
            for (int i = 0; i < tl.Count; i++)
                if (tl[i].Kind == PlannedNodeKind.Remove) removeAt[tl[i].UnitId] = tl[i].Start;

            for (int i = 0; i < tl.Count; i++)
            {
                PlannedNode n = tl[i];
                if (n.Kind == PlannedNodeKind.Move)
                {
                    Close(n.UnitId, n.Start + n.Duration);
                    Open(n.UnitId, n.ToY * GameConst.BoardWidth + n.ToX, n.Start + n.Duration);
                }
                else if (n.Kind == PlannedNodeKind.Death)
                {
                    float end = removeAt.TryGetValue(n.UnitId, out float rel) ? rel : tl.Makespan;
                    Close(n.UnitId, end);
                }
            }
            var leftovers = new List<int>(openCell.Keys);
            foreach (int unit in leftovers) Close(unit, tl.Makespan);

            foreach (var kv in intervals)
            {
                var list = kv.Value;
                list.Sort((a, b) => a[0].CompareTo(b[0]));
                for (int i = 1; i < list.Count; i++)
                    Assert.LessOrEqual(list[i - 1][1], list[i][0] + 1e-6f,
                        $"I3/I4 破：格 {kv.Key} 占用区间重叠（踩格/踩尸体——倒地前格不让位被破坏）");
            }
        }

        // ─────────────────────── 真链路底座（BattleRoot 生产链路同款） ───────────────────────

        /// <summary>真表开局到 R1 开战：注资采购远程/技能大户 → 摆阵 → 视觉撮合解析 → 整场预演 →
        /// 规划（真烘焙 SO 截窗时刻表）→ 演出计划。</summary>
        private static RoundData RunRealRound(uint seed)
        {
            var s = new GameSession(new EventBus(), seed, GameTables.Default);
            Assert.IsTrue(s.StartRun());
            s.Eco.AddGold(300);
            while (s.Eco.Level < 8)
                if (!s.BuyXp()) break;

            // 采购（BattleRoot.NewRun 同款：优先 10/12/13/16/9/11/14/18——弹道与技能大户在前）
            int[] prefer = { 10, 12, 13, 16, 9, 11, 14, 18, 15, 6, 2, 5, 8, 4, 1, 3, 7, 17 };
            int guard = 0, refreshGuard = 0;
            while (s.Bench.Count < GameSession.BenchCapacity && guard++ < 300)
            {
                int slot = -1, bestRank = int.MaxValue;
                for (int i = 0; i < ShopSystem.Slots; i++)
                {
                    int id = s.Shop.Offers[i].Id;
                    if (id == 0) continue;
                    int rank = Array.IndexOf(prefer, id);
                    if (rank >= 0 && rank < bestRank) { bestRank = rank; slot = i; }
                }
                if (slot < 0)
                {
                    if (refreshGuard++ >= 60 || !s.RefreshShop()) break;
                    continue;
                }
                if (!s.Buy(slot))
                {
                    if (refreshGuard++ >= 60 || !s.RefreshShop()) break;
                    continue;
                }
            }

            // 摆阵（BattleRoot.FillBoard 同款：远程纵深、近战贴中线）
            while (s.PlayerBoard.Count < s.Eco.Level && s.Bench.Count > 0)
            {
                Unit u = s.Bench[0];
                bool ranged = GameTables.Default.Characters.Get(u.CharId).BaseStats.AttackRange > 1;
                bool placed = false;
                foreach (Pos cell in (ranged ? BackCells : FrontCells))
                    if (s.PlaceOnBoard(u.Id, cell)) { placed = true; break; }
                if (!placed)
                    foreach (Pos cell in (ranged ? FrontCells : BackCells))
                        if (s.PlaceOnBoard(u.Id, cell)) { placed = true; break; }
                if (!placed) break;
            }
            Assert.GreaterOrEqual(s.PlayerBoard.Count, 4, "上场阵容规模下限（采购必须成军）");

            return PreviewRound(s);
        }

        /// <summary>已备战的会话直接整场预演→规划→演出计划（视觉撮合与真时刻表=BattleRoot 同款）。</summary>
        private static RoundData PreviewRound(GameSession s)
        {
            var visuals = new Dictionary<int, ResolvedUnitVisual>(24);
            var charIds = new Dictionary<int, int>(24);
            foreach (Unit u in s.PlayerBoard)
            {
                charIds[u.Id] = u.CharId;
                ResolveReal(visuals, u.Id, u.CharId,
                    GameTables.Default.Characters.Get(u.CharId).BaseStats.AttackInterval,
                    GameTables.Default.Characters.Get(u.CharId).BaseStats.MaxMana);
            }
            Encounter enc = GameTables.Default.Enemies.Get(s.Round);
            if (!enc.IsEmpty)
            {
                var roster = s.EnemyRoster;
                int count = Math.Min(roster.Count, enc.Spawns.Length);
                for (int i = 0; i < count; i++)
                {
                    int key = UnitActorConfig.EnemyKeyFromName(enc.Spawns[i].DisplayName);
                    Assert.AreNotEqual(0, key, $"敌方怪「{enc.Spawns[i].DisplayName}」无视觉键映射");
                    ResolveReal(visuals, roster[i].Id, key, enc.Spawns[i].Stats.AttackInterval, 0);
                }
            }

            var previewer = new BattlePreviewer();
            Assert.IsTrue(previewer.Start(s), "预演开战（Prep 门禁须过：矿/策略门禁须先消费）");
            BattlePreviewResult r = previewer.RunToEnd();

            // 先消费后释放（#23②「播完即释放」会清空流与快照容量——规划与演出计划构建完才能 Release）
            var tl = new BattlePlanner().Plan(r.Stream, BuildRealTable(r, visuals));
            var plan = ProjectileEffectPlan.Build(tl, visuals, charIds, GameTables.Default.Skills);
            BattleSnapshot seedFrame = r.Snapshots.Count > 0 ? r.Snapshots[0] : default;   // struct 拷贝=Release 后安全
            previewer.Release();
            return new RoundData
            {
                Session = s, Result = r, Timeline = tl, Plan = plan,
                CharIds = charIds, Visuals = visuals, SeedFrame = seedFrame,
            };
        }

        private static void ResolveReal(Dictionary<int, ResolvedUnitVisual> visuals, int unitId,
            int visualKey, float attackInterval, int maxMana)
        {
            UnitVisualDef def = UnitActorConfig.Get(visualKey);
            Assert.NotNull(def, $"视觉键 {visualKey} 无配置");
            var set = AssetDatabase.LoadAssetAtPath<UnitActorClipSet>(def.ClipSetPath + ".asset");
            Assert.NotNull(set, $"烘焙 SO 缺失：{def.ClipSetPath}（先跑菜单「工具/表现层序列帧烘焙」）");
            visuals[unitId] = UnitVisualResolver.Resolve(set, def, attackInterval, maxMana);
        }

        /// <summary>真数据时刻表（BattleRoot.BuildTimingTable 同款：逐单位截窗时长、施法复用攻击窗）。</summary>
        private static PlannerTimingTable BuildRealTable(BattlePreviewResult r,
            Dictionary<int, ResolvedUnitVisual> visuals)
        {
            var fallback = new UnitTiming
            {
                AttackDuration = 0.45f, AttackHitPercent = 0.5f,
                CastDuration = 0.60f, CastHitPercent = 0.5f,
                MoveDuration = 0.30f, DeathDuration = 0.40f,
            };
            var table = new PlannerTimingTable(fallback);
            if (r.Snapshots == null || r.Snapshots.Count == 0) return table;
            foreach (SnapshotUnit u in r.Snapshots[0].Units)
                if (visuals.TryGetValue(u.UnitId, out var v))
                    table.Set(u.UnitId, new UnitTiming
                    {
                        AttackDuration = v.AttackDuration,
                        AttackHitPercent = v.AttackHitPercent,
                        CastDuration = v.AttackDuration,        // 4C 口径：施法复用攻击窗
                        CastHitPercent = v.AttackHitPercent,
                        MoveDuration = v.MoveDuration,
                        DeathDuration = v.DeathDuration,
                    });
            return table;
        }

        /// <summary>备战门禁消费（BattleRoot.ConsumeGates 同款：捡完全部矿+选第一格策略）。</summary>
        private static void ConsumeGates(GameSession s)
        {
            int guard = 0;
            while (s.OresOnGround.Count > 0)
            {
                if (++guard > 50) { Assert.Fail("捡矿循环异常长"); break; }
                Assert.IsTrue(s.PickOre(s.OresOnGround[0].Id), "捡矿被拒");
            }
            if (s.HasPolicyChoice)
                s.PickPolicy(0);
        }
    }
}
