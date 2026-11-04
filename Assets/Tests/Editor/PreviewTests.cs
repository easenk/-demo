using System;
using System.Collections.Generic;
using System.Linq;
using AutoChess.Game;
using AutoChess.Presentation;
using NUnit.Framework;

namespace AutoChess.Tests
{
    /// <summary>
    /// 阶段4 批次A：事件流基建验收（#23①②——预演器 / 独立总线 / 捕获器 / 关键帧快照）。
    ///
    /// 分节契约：
    ///  · 完整性：预演事件流与旧路径（null 总线 + 手推 tick）逐条一致——捕获不丢、不乱序、字段不错；
    ///  · 隔离性：预演期会话总线对白名单 11 类零接收；结算/备战事件归还会话总线（UI 订阅不受影响）；
    ///  · 确定性红线：同种子两次预演——事件流逐条一致 + 快照逐帧一致（阶段5 快照重演的地基）；
    ///  · 白名单：11 类全捕获（构造一场能打满全部类的战斗）；Seq 全流序号自洽；
    ///  · 超时局：打满 MaxTicks 4500 步、扩容路径走通且内容与默认容量一致、终局帧存在；
    ///  · 秒胜链：空场开战 → 流空/零帧/零步、结算已内联、总线归还探针通过；
    ///  · 晶矿记账：预演线程上的掉落记账照常生效，OreDropped 进流、备战期开矿全链路可用；
    ///  · 快照规则：tick0/每 30 tick/死亡 tick/终局帧齐备、帧序唯一、死者缺席终局帧；
    ///  · 释放：Release 清流与快照、容量保留；
    ///  · 后台线程：Task 路径与同步路径逐条一致；
    ///  · DoT 死亡镜像锁：DoT 致死只发 Died 不发 Removed、尸体占格（逻辑层现状，4B1 按此镜像）；
    ///  · 基准冒烟：千场基准工具的核心链路可跑、报告成形（全量跑菜单，见 PreviewBenchmark）。
    ///
    /// 场景表组（CoverageTables）：验收术士（带魔法技能 90）vs 验收傀儡（近战 + 会给自己上
    /// "MaxHp+100 且每 tick 回 1"的纯 buff 技能 91、带矿挡位 1）——一场战斗打满 11 类事件；
    /// 场外摆位（距离 4 > 傀儡射程 1）逼出战斗走位；对射 4 格内保证双方出手。
    /// </summary>
    [TestFixture]
    public class PreviewTests
    {
        private const uint SeedA = 20261102u;

        /// <summary>白名单 11 类的短名（隔离性断言的遍历集）。</summary>
        private static readonly string[] BattleKinds =
        {
            "UnitAttacked", "SkillCast", "UnitDamaged", "UnitDied", "UnitHealed",
            "ManaChanged", "UnitMoved", "UnitRemoved", "UnitMaxHpChanged", "BattleEnded", "OreDropped",
        };

        // ─────────────────────── 收集器与翻译器（完整性验收的两套独立实现——互为对照） ───────────────────────

        /// <summary>旧路径的会话总线收集器：11 类战斗事件 → 规范行。
        /// 订阅时机必须在会话构造【之前】——使 Died→OreDropped 的相对序与预演路径
        /// （捕获器先订、晶矿记账后订）一致，逐条对照才成立。</summary>
        private sealed class BattleCollector
        {
            public readonly List<string> Log = new List<string>(8192);
            public int CurrentTick;   // 旧路径驱动侧维护：每步先递增再 Tick()——与预演路径的 TickCount 恒等

            public void SubscribeAll(EventBus bus)
            {
                bus.Subscribe<UnitAttackedEvent>(e => Log.Add($"UnitAttacked|{CurrentTick}|{e.AttackerId}|{e.TargetId}|{(e.IsCrit ? 1 : 0)}"));
                bus.Subscribe<SkillCastEvent>(e => Log.Add($"SkillCast|{CurrentTick}|{e.CasterId}|{e.SkillId}|{e.TargetId}|{(e.IsCrit ? 1 : 0)}"));
                bus.Subscribe<UnitDamagedEvent>(e => Log.Add($"UnitDamaged|{CurrentTick}|{e.UnitId}|{e.SourceUnitId}|{e.FinalDamage}|{e.CurrentHp}"));
                bus.Subscribe<UnitDiedEvent>(e => Log.Add($"UnitDied|{CurrentTick}|{e.UnitId}|{e.Cell.X},{e.Cell.Y}"));
                bus.Subscribe<UnitHealedEvent>(e => Log.Add($"UnitHealed|{CurrentTick}|{e.UnitId}|{e.Amount}|{e.CurrentHp}"));
                bus.Subscribe<ManaChangedEvent>(e => Log.Add($"ManaChanged|{CurrentTick}|{e.UnitId}|{e.CurrentMana}|{e.MaxMana}"));
                bus.Subscribe<UnitMovedEvent>(e => Log.Add($"UnitMoved|{CurrentTick}|{e.UnitId}|{e.FromCell.X},{e.FromCell.Y}|{e.ToCell.X},{e.ToCell.Y}"));
                bus.Subscribe<UnitRemovedEvent>(e => Log.Add($"UnitRemoved|{CurrentTick}|{e.UnitId}|{e.Cell.X},{e.Cell.Y}"));
                bus.Subscribe<UnitMaxHpChangedEvent>(e => Log.Add($"UnitMaxHpChanged|{CurrentTick}|{e.UnitId}|{e.MaxHp}|{e.CurrentHp}"));
                bus.Subscribe<BattleEndedEvent>(e => Log.Add($"BattleEnded|{CurrentTick}|{(e.PlayerWon ? 1 : 0)}|{e.TotalTicks}"));
                bus.Subscribe<OreDroppedEvent>(e => Log.Add($"OreDropped|{CurrentTick}|{e.OreId}|{e.Tier}|{e.Cell.X},{e.Cell.Y}"));
            }
        }

        /// <summary>StreamItem → 规范行（独立于 StreamRecorder 的第二套解码——按 Events.cs 字段名直译，
        /// 捕获器若丢字段/错槽位在此红）。</summary>
        private static string DescribeItem(in StreamItem it)
        {
            switch (it.Kind)
            {
                case StreamEventKind.UnitAttacked: return $"UnitAttacked|{it.Tick}|{it.IdA}|{it.IdB}|{it.V0}";
                case StreamEventKind.SkillCast: return $"SkillCast|{it.Tick}|{it.IdA}|{it.IdB}|{it.IdC}|{it.V0}";
                case StreamEventKind.UnitDamaged: return $"UnitDamaged|{it.Tick}|{it.IdA}|{it.IdB}|{it.V0}|{it.V1}";
                case StreamEventKind.UnitDied: return $"UnitDied|{it.Tick}|{it.IdA}|{it.CellX},{it.CellY}";
                case StreamEventKind.UnitHealed: return $"UnitHealed|{it.Tick}|{it.IdA}|{it.V0}|{it.V1}";
                case StreamEventKind.ManaChanged: return $"ManaChanged|{it.Tick}|{it.IdA}|{it.V0}|{it.V1}";
                case StreamEventKind.UnitMoved: return $"UnitMoved|{it.Tick}|{it.IdA}|{it.CellX},{it.CellY}|{it.ToX},{it.ToY}";
                case StreamEventKind.UnitRemoved: return $"UnitRemoved|{it.Tick}|{it.IdA}|{it.CellX},{it.CellY}";
                case StreamEventKind.UnitMaxHpChanged: return $"UnitMaxHpChanged|{it.Tick}|{it.IdA}|{it.V0}|{it.V1}";
                case StreamEventKind.BattleEnded: return $"BattleEnded|{it.Tick}|{it.V0}|{it.V1}";
                case StreamEventKind.OreDropped: return $"OreDropped|{it.Tick}|{it.IdA}|{it.V0}|{it.CellX},{it.CellY}";
                default: return $"Unknown|{it.Kind}";
            }
        }

        /// <summary>会话总线计数器（隔离性 + 归还探针）：白名单 11 类 + 回合/归位/摆位探针。</summary>
        private sealed class BusCounter
        {
            private readonly Dictionary<string, int> _counts = new Dictionary<string, int>();

            private void Bump(string key)
            {
                _counts.TryGetValue(key, out int v);
                _counts[key] = v + 1;
            }

            public int Of(string key) { _counts.TryGetValue(key, out int v); return v; }

            public void SubscribeAll(EventBus bus)
            {
                bus.Subscribe<UnitAttackedEvent>(e => Bump("UnitAttacked"));
                bus.Subscribe<SkillCastEvent>(e => Bump("SkillCast"));
                bus.Subscribe<UnitDamagedEvent>(e => Bump("UnitDamaged"));
                bus.Subscribe<UnitDiedEvent>(e => Bump("UnitDied"));
                bus.Subscribe<UnitHealedEvent>(e => Bump("UnitHealed"));
                bus.Subscribe<ManaChangedEvent>(e => Bump("ManaChanged"));
                bus.Subscribe<UnitMovedEvent>(e => Bump("UnitMoved"));
                bus.Subscribe<UnitRemovedEvent>(e => Bump("UnitRemoved"));
                bus.Subscribe<UnitMaxHpChangedEvent>(e => Bump("UnitMaxHpChanged"));
                bus.Subscribe<BattleEndedEvent>(e => Bump("BattleEnded"));
                bus.Subscribe<OreDroppedEvent>(e => Bump("OreDropped"));
                bus.Subscribe<RoundEndedEvent>(e => Bump("RoundEnded"));
                bus.Subscribe<RoundStartedEvent>(e => Bump("RoundStarted"));
                bus.Subscribe<UnitRestoredEvent>(e => Bump("UnitRestored"));
                bus.Subscribe<UnitPlacedEvent>(e => Bump("UnitPlaced"));
            }
        }

        // ─────────────────────── 场景表组 ───────────────────────

        /// <summary>覆盖场景表：一场战斗打满 11 类白名单事件（术士带魔法技能；傀儡近战 + 纯 buff 技能 + 带矿）。</summary>
        private static GameTables CoverageTables()
        {
            var heroStats = new UnitStats
            {
                MaxHp = 500, AttackPower = 25, Armor = 0, MagicResist = 0, AbilityPower = 100,
                AttackRange = 4, AttackInterval = 0.8f, MoveInterval = 1f,
                CritChance = 0, CritDamage = 50,
                MaxMana = 30, ManaPerHit = 10, ManaRegenPerSecond = 0,
            };
            var chars = new CharacterTable(new[]
            {
                new CharacterDef(1, "验收术士", 1, heroStats, Array.Empty<SynergyId>(), RoleId.Mage, 90),
            });

            var foeStats = new UnitStats
            {
                MaxHp = 400, AttackPower = 30, Armor = 0, MagicResist = 0, AbilityPower = 0,
                AttackRange = 1, AttackInterval = 1f, MoveInterval = 0.5f,
                CritChance = 0, CritDamage = 0,
                MaxMana = 10, ManaPerHit = 0, ManaRegenPerSecond = 10,
            };
            var enemies = new EnemyTable(new[]
            {
                new EnemyRoundEntry(1, new[]
                {
                    new EnemySpawn("验收傀儡", foeStats, new Pos(4, 0), 1, 1, 91),   // 矿挡位 1、技能 91
                }),
            });

            var skills = new SkillTable(new SkillDef[]
            {
                // 90 验收奥术：单体魔法伤害（基础 40 + 法强系数 50 → 90 点）
                new SkillDef(90, "验收奥术", DamageType.Magic, 40, 0, 0, 50, 0, 4, SkillTargetRule.NearestEnemy, false),
                // 91 验收魔盾：纯 buff（MaxHp+100 且每 tick 回 1）——傀儡的生存技，打满 SkillCast/MaxHpChanged/Healed
                new SkillDef(91, "验收魔盾", DamageType.Physical, 0, 0, 0, 0, 0, 1, SkillTargetRule.Self, false,
                    buffId: 92, buffTarget: SkillBuffTarget.Targets),
            });

            var buffs = new BuffTable(new BuffDef[]
            {
                new BuffDef(92, "验收魔盾体", 300, 1,
                    new[] { new StatModifier(StatKind.MaxHp, StatOp.Flat, 100f) }, 0, 1),
            });

            var ores = new OreTable(new[]
            {
                new OreDef(11, OreRewardType.Gold, "验收金", 5, 0),
                new OreDef(21, OreRewardType.Gold, "验收金·中", 8, 0),
                new OreDef(31, OreRewardType.Gold, "验收金·高", 10, 0),
            }, new[]
            {
                new OreTierDef(1, new[] { 11 }),
                new OreTierDef(2, new[] { 21 }),
                new OreTierDef(3, new[] { 31 }),
            });

            return new GameTables
            {
                Characters = chars,
                Odds = AllCostOne(),
                Rounds = RoundTable.Default,
                Enemies = enemies,
                Synergies = SynergyTable.Default,
                Ores = ores,
                Policies = new PolicyTable(new PolicyDef[0]),   // 空表：奖励关胜后 0 候选静默（批次7 坑②）
                Skills = skills,
                Equipment = EquipmentTable.Default,
                Buffs = buffs,
            };
        }

        /// <summary>超时场景表：双肉盾互拍不死（3000 血 vs 保底 1 伤害/秒 → 3000 秒远超 150 秒限时）。</summary>
        private static GameTables TimeoutTables()
        {
            var tankStats = new UnitStats
            {
                MaxHp = 3000, AttackPower = 1, Armor = 0, MagicResist = 0, AbilityPower = 0,
                AttackRange = 1, AttackInterval = 1f, MoveInterval = 1f,
                CritChance = 0, CritDamage = 0, MaxMana = 0, ManaPerHit = 0, ManaRegenPerSecond = 0,
            };
            var chars = new CharacterTable(new[]
            {
                new CharacterDef(1, "验收肉盾", 1, tankStats, Array.Empty<SynergyId>(), RoleId.Mage, 0),
            });
            var enemies = new EnemyTable(new[]
            {
                new EnemyRoundEntry(1, new[] { new EnemySpawn("验收石像", tankStats, new Pos(4, 2), 0, 1) }),
            });
            return new GameTables
            {
                Characters = chars,
                Odds = AllCostOne(),
                Rounds = RoundTable.Default,
                Enemies = enemies,
                Synergies = SynergyTable.Default,
                Ores = OreTable.Default,
                Policies = new PolicyTable(new PolicyDef[0]),
                Skills = SkillTable.Default,
                Equipment = EquipmentTable.Default,
                Buffs = BuffTable.Default,
            };
        }

        /// <summary>DoT 击杀场景表：术士普攻 10 秒一次（几乎不出手）、靠回蓝 2 秒满蓝施放毒咒
        /// （伤害 1 + 淬毒 DoT 2/步）；傀儡 5 血必死于毒——锁"DoT 致死无 Removed、尸体占格"镜像契约。</summary>
        private static GameTables DotKillTables()
        {
            var heroStats = new UnitStats
            {
                MaxHp = 500, AttackPower = 1, Armor = 0, MagicResist = 0, AbilityPower = 0,
                AttackRange = 4, AttackInterval = 10f, MoveInterval = 1f,
                CritChance = 0, CritDamage = 0,
                MaxMana = 30, ManaPerHit = 0, ManaRegenPerSecond = 15,   // 2 秒满蓝 → tick 60 首次施法
            };
            var chars = new CharacterTable(new[]
            {
                new CharacterDef(1, "验收毒师", 1, heroStats, Array.Empty<SynergyId>(), RoleId.Mage, 80),
            });

            var foeStats = new UnitStats
            {
                MaxHp = 5, AttackPower = 0, Armor = 0, MagicResist = 0, AbilityPower = 0,
                AttackRange = 1, AttackInterval = 1f, MoveInterval = 0.5f,
                CritChance = 0, CritDamage = 0, MaxMana = 0, ManaPerHit = 0, ManaRegenPerSecond = 0,
            };
            var enemies = new EnemyTable(new[]
            {
                new EnemyRoundEntry(1, new[] { new EnemySpawn("验收病鼠", foeStats, new Pos(4, 0), 1, 1) }),
            });

            var skills = new SkillTable(new SkillDef[]
            {
                new SkillDef(80, "验收毒咒", DamageType.Magic, 1, 0, 0, 0, 0, 4, SkillTargetRule.NearestEnemy, false,
                    buffId: 93, buffTarget: SkillBuffTarget.Targets),
            });
            var buffs = new BuffTable(new BuffDef[]
            {
                new BuffDef(93, "验收毒", 300, 1, null, 2, 0),
            });

            return new GameTables
            {
                Characters = chars,
                Odds = AllCostOne(),
                Rounds = RoundTable.Default,
                Enemies = enemies,
                Synergies = SynergyTable.Default,
                Ores = OreTable.Default,
                Policies = new PolicyTable(new PolicyDef[0]),
                Skills = skills,
                Equipment = EquipmentTable.Default,
                Buffs = buffs,
            };
        }

        private static ShopOdds AllCostOne()
        {
            var t = new int[9, 6];
            for (int level = 2; level <= 8; level++) t[level, 1] = 100;
            return new ShopOdds(t);
        }

        // ─────────────────────── 场景脚本 ───────────────────────

        /// <summary>覆盖场景备战脚本：买术士上场（0,0）——对面 (4,0) 的傀儡距离 4 > 其射程 1，逼出战斗走位。</summary>
        private static void SetupCoverage(GameSession s)
        {
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0), "1 费棋：开局 3 金买得起（单角色全 1 费表 → 槽 0 恒有货）");
            Assert.IsTrue(s.PlaceOnBoard(s.Bench[0].Id, new Pos(0, 0)));
        }

        /// <summary>覆盖场景整场预演（同种子 = 同战斗）。</summary>
        private static (GameSession s, BattlePreviewer p, BattlePreviewResult r) RunCoveragePreview(uint seed)
        {
            var s = new GameSession(new EventBus(), seed, CoverageTables());
            SetupCoverage(s);
            var p = new BattlePreviewer();
            Assert.IsTrue(p.Start(s), "预演开战（门禁已消费干净）");
            return (s, p, p.RunToEnd());
        }

        // ─────────────────────── ① 完整性：与旧路径逐条一致 ───────────────────────

        [Test]
        public void Preview_StreamMatchesOldBusPath_ItemByItem()
        {
            // A：旧路径（StartBattle 不传总线）——收集器在会话构造【之前】订阅（顺序前提见 BattleCollector 注释）
            var busA = new EventBus();
            var recA = new BattleCollector();
            recA.SubscribeAll(busA);
            var a = new GameSession(busA, SeedA, CoverageTables());
            SetupCoverage(a);
            Assert.IsTrue(a.StartBattle(), "旧路径开战");
            int tick = 0;
            while (!a.Combat.IsOver)
            {
                recA.CurrentTick = ++tick;   // 先递增再 Tick：事件发生在 TickCount 递增之后，两侧刻度恒等
                a.Combat.Tick();
            }

            // B：预演路径（同种子同操作 = 同一场战斗）
            var b = new GameSession(new EventBus(), SeedA, CoverageTables());
            SetupCoverage(b);
            var p = new BattlePreviewer();
            Assert.IsTrue(p.Start(b));
            BattlePreviewResult rb = p.RunToEnd();

            Assert.AreEqual(a.Combat.PlayerWon, rb.PlayerWon, "胜负一致");
            Assert.AreEqual(a.Combat.TickCount, rb.TickCount, "步数一致");
            Assert.AreEqual(recA.Log.Count, rb.Stream.Count,
                $"条数不一致（旧路径 {recA.Log.Count} vs 预演 {rb.Stream.Count}）——捕获丢/多了事件");

            var streamLines = new List<string>(rb.Stream.Count);
            for (int i = 0; i < rb.Stream.Count; i++) streamLines.Add(DescribeItem(rb.Stream[i]));
            for (int i = 0; i < recA.Log.Count; i++)
                Assert.AreEqual(recA.Log[i], streamLines[i],
                    $"第 {i} 条不一致：旧路径[{recA.Log[i]}] vs 预演[{streamLines[i]}]");

            // 事件规模下限（防空转假绿：出手/走位/施法/掉矿都要发生）
            Assert.Greater(recA.Log.Count, 60, $"事件流规模下限（实际 {recA.Log.Count} 条）");
        }

        // ─────────────────────── ② 隔离性 + 总线归还 ───────────────────────

        [Test]
        public void Preview_SessionBusSilentDuringBattle_SettleEventsReturn()
        {
            var bus = new EventBus();
            var counter = new BusCounter();
            counter.SubscribeAll(bus);
            var s = new GameSession(bus, SeedA, CoverageTables());
            SetupCoverage(s);

            var p = new BattlePreviewer();
            Assert.IsTrue(p.Start(s), "预演模式开战");
            var r = p.RunToEnd();
            Assert.IsTrue(r.PlayerWon, "覆盖场景必胜（术士 DPS 远超傀儡有效血池）");

            // 隔离性：战斗期会话总线对白名单 11 类零接收（防泄露 + 防跨线程的对象面）
            foreach (string kind in BattleKinds)
                Assert.AreEqual(0, counter.Of(kind), $"战斗期 {kind} 漏进会话总线——隔离被击穿");

            // 晶矿记账已发生在预演线程（结算之前矿已在场——记账改听战斗总线没有丢事件）
            Assert.AreEqual(1, s.OresOnGround.Count, "预演完成、结算之前：掉落记账已生效");

            // 结算与下一回合：事件回到会话总线（UI 正常订阅）
            Assert.IsTrue(s.SettleBattle(), "排空闸门回调结算");
            Assert.AreEqual(1, counter.Of("RoundEnded"), "结算事件回会话总线");
            Assert.IsTrue(s.StartPrep());
            Assert.AreEqual(2, counter.Of("RoundStarted"), "R1+R2 两回合开始事件");
            Assert.AreEqual(1, counter.Of("UnitRestored"), "幸存者归位——单位总线已归还的探针");

            // 棋盘总线归还探针：备战期挪位（UnitMoved 是白名单战斗事件——备战期必须回到会话总线）。
            // 不能用"敌方退场清格"当探针：覆盖场景的傀儡死在战斗里、战斗期已被 HandleKill 清格，
            // 进备战时它已不在棋盘上——那格的 Removed 事件正确地发生在战斗期（走了战斗总线，见覆盖测试）。
            int heroId = s.PlayerBoard[0].Id;
            Assert.IsTrue(s.MoveOnBoard(heroId, new Pos(1, 2)), "备战期挪位");
            Assert.AreEqual(1, counter.Of("UnitMoved"), "备战期走位事件到达会话总线——棋盘总线已归还");
        }

        // ─────────────────────── ③ 确定性红线：同种子两次预演逐条一致 ───────────────────────

        [Test]
        public void Preview_SameSeedTwice_IdenticalStreamAndSnapshots()
        {
            var run1 = RunCoveragePreview(SeedA);
            var run2 = RunCoveragePreview(SeedA);

            Assert.AreEqual(run1.r.PlayerWon, run2.r.PlayerWon);
            Assert.AreEqual(run1.r.TickCount, run2.r.TickCount, "步数一致");
            Assert.AreEqual(run1.r.RollCount, run2.r.RollCount, "掷骰数一致");

            var s1 = run1.r.Stream;
            var s2 = run2.r.Stream;
            Assert.AreEqual(s1.Count, s2.Count, "事件条数一致");
            for (int i = 0; i < s1.Count; i++)
                Assert.IsTrue(s1[i].Equals(s2[i]),
                    $"第 {i} 条不一致：A[{DescribeItem(s1[i])}] vs B[{DescribeItem(s2[i])}]——确定性红线被破");

            var f1 = run1.r.Snapshots;
            var f2 = run2.r.Snapshots;
            Assert.AreEqual(f1.Count, f2.Count, "快照帧数一致");
            for (int i = 0; i < f1.Count; i++)
            {
                Assert.AreEqual(f1[i].Tick, f2[i].Tick, $"帧 {i} tick 不一致");
                Assert.AreEqual(f1[i].Units.Length, f2[i].Units.Length, $"帧 {i}（tick {f1[i].Tick}）单位数不一致");
                for (int u = 0; u < f1[i].Units.Length; u++)
                    Assert.IsTrue(f1[i].Units[u].Equals(f2[i].Units[u]), $"帧 {i} 单位 {u} 条目不一致");
            }
        }

        // ─────────────────────── ④ 白名单覆盖 + Seq 自洽 ───────────────────────

        [Test]
        public void Preview_WhiteList_AllElevenKindsCaptured()
        {
            var (_, _, r) = RunCoveragePreview(SeedA);
            var stream = r.Stream;

            var seen = new HashSet<StreamEventKind>();
            for (int i = 0; i < stream.Count; i++)
            {
                seen.Add(stream[i].Kind);
                Assert.AreEqual(i, stream[i].Seq, $"Seq 必须与捕获顺序恒等（第 {i} 条 Seq={stream[i].Seq}）");
            }

            foreach (StreamEventKind kind in Enum.GetValues(typeof(StreamEventKind)))
                Assert.IsTrue(seen.Contains(kind), $"{kind} 未捕获——白名单覆盖不全或场景退化");

            Assert.AreEqual(StreamEventKind.BattleEnded, stream[stream.Count - 1].Kind,
                "BattleEnded 恒为末条（本 tick 最后一条事件的时间线契约）");
        }

        // ─────────────────────── ⑤ 超时局：MaxTicks + 扩容 + 终局帧 ───────────────────────

        [Test]
        public void Preview_TimeoutBattle_MaxTicksGrowAndEndFrame()
        {
            var s = new GameSession(new EventBus(), SeedA, TimeoutTables());
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            Assert.IsTrue(s.PlaceOnBoard(s.Bench[0].Id, new Pos(3, 2)));   // 与 (4,2) 石像相邻对拍

            var p = new BattlePreviewer(64);   // 小初始容量：顺路验证扩容路径
            Assert.IsTrue(p.Start(s));
            var r = p.RunToEnd();

            Assert.AreEqual(GameConst.MaxTicks, r.TickCount, "超时局打满 4500 步");
            Assert.IsFalse(r.PlayerWon, "超时判负");

            StreamItem last = r.Stream[r.Stream.Count - 1];
            Assert.AreEqual(StreamEventKind.BattleEnded, last.Kind);
            Assert.AreEqual(0, last.V0, "BattleEnded.V0 = 是否获胜（0 = 负）");
            Assert.AreEqual(GameConst.MaxTicks, last.V1, "BattleEnded.V1 = 总步数");

            Assert.GreaterOrEqual(r.Stream.GrowCount, 4, "64 起步至少 4 次倍增才装得下整场（约 600 条）");

            // 帧数 = 开战帧 + 每 30 tick 一帧（4500/30 = 150；终局帧与 4500%30==0 帧去重合并）
            Assert.AreEqual(1 + 150, r.Snapshots.Count, "超时局帧数：tick0 + 150 个整秒帧");
            Assert.AreEqual(GameConst.MaxTicks, r.Snapshots[r.Snapshots.Count - 1].Tick, "终局帧存在");

            // 扩容不改内容：与默认容量同种子对照逐条一致
            var s2 = new GameSession(new EventBus(), SeedA, TimeoutTables());
            Assert.IsTrue(s2.StartRun());
            Assert.IsTrue(s2.Buy(0));
            Assert.IsTrue(s2.PlaceOnBoard(s2.Bench[0].Id, new Pos(3, 2)));
            var p2 = new BattlePreviewer();
            Assert.IsTrue(p2.Start(s2));
            var r2 = p2.RunToEnd();
            Assert.AreEqual(r2.Stream.Count, r.Stream.Count, "两种容量条数一致");
            for (int i = 0; i < r.Stream.Count; i++)
                Assert.IsTrue(r.Stream[i].Equals(r2.Stream[i]), $"扩容后第 {i} 条内容漂移");
            Assert.AreEqual(0, r2.Stream.GrowCount, "默认容量装得下超时局（约 600 条 < 8192）——不扩容");
        }

        // ─────────────────────── ⑥ 秒胜链：流空 + 内联结算 + 归还 ───────────────────────

        [Test]
        public void Preview_InstantSettle_EmptyStreamAndBusRestored()
        {
            var bus = new EventBus();
            var counter = new BusCounter();
            counter.SubscribeAll(bus);
            var s = new GameSession(bus, SeedA, CoverageTables());
            Assert.IsTrue(s.StartRun());
            // 不买不上场 → 空场 vs 傀儡 → 构造即终局（判负），SettleBattle 已内联

            var p = new BattlePreviewer();
            Assert.IsTrue(p.Start(s), "空场开战合法（门禁只挡矿/策略/相位）");
            Assert.AreEqual(GamePhase.Settlement, s.Phase, "秒胜链：StartBattle 尾部内联结算");

            var r = p.RunToEnd();
            Assert.AreEqual(0, r.Stream.Count, "战斗根本没发生——流为空");
            Assert.AreEqual(0, r.Snapshots.Count, "无关键帧");
            Assert.AreEqual(0, r.TickCount);
            Assert.IsFalse(r.PlayerWon, "空场判负（PlayerWon = 敌清空且我方有人）");
            Assert.IsFalse(s.SettleBattle(), "已内联结算过——再调纯拒绝");

            // 总线归还探针：进 R2 备战，敌方退场清格与回合事件都到达会话总线
            Assert.IsTrue(s.StartPrep());
            Assert.AreEqual(2, counter.Of("RoundStarted"), "R1+R2 回合开始");
            Assert.AreEqual(1, counter.Of("UnitRemoved"), "敌方退场清格到达会话总线——棋盘/敌方单位总线已归还");
        }

        // ─────────────────────── ⑦ 晶矿记账在预演线程照常生效 ───────────────────────

        [Test]
        public void Preview_OreAccounting_SurvivesPreviewIsolation()
        {
            var (s, p, r) = RunCoveragePreview(SeedA);

            Assert.AreEqual(1, s.OresOnGround.Count, "预演完成、结算之前：矿已记账在场");
            bool hasDrop = false;
            for (int i = 0; i < r.Stream.Count; i++)
                if (r.Stream[i].Kind == StreamEventKind.OreDropped) { hasDrop = true; break; }
            Assert.IsTrue(hasDrop, "OreDropped 进事件流（4U3 掉落动画的数据源）");

            Assert.IsTrue(s.SettleBattle());
            Assert.IsTrue(s.StartPrep());
            int goldBefore = s.Eco.Gold;
            Assert.IsTrue(s.PickOre(s.OresOnGround[0].Id), "备战期开矿全链路");
            Assert.Greater(s.Eco.Gold, goldBefore, "T1 池 = 金币 5 到账");
        }

        // ─────────────────────── ⑧ 快照规则：拍点齐备、镜像如实 ───────────────────────

        [Test]
        public void Preview_Snapshots_AtBoundariesMirroringBoard()
        {
            var (s, p, r) = RunCoveragePreview(SeedA);
            var snaps = r.Snapshots;
            int heroId = s.PlayerBoard[0].Id;
            int enemyId = s.EnemyRoster[0].Id;

            Assert.GreaterOrEqual(snaps.Count, 3, "覆盖场景至少 3 帧（tick0 + 整秒帧 + 终局）");
            Assert.AreEqual(0, snaps[0].Tick, "首帧 = 开战时刻（tick 0）");

            // 开战帧：双方满血、位置如实、先玩家后敌方
            Assert.AreEqual(2, snaps[0].Units.Length);
            Assert.AreEqual(heroId, snaps[0].Units[0].UnitId);
            Assert.AreEqual(0, snaps[0].Units[0].X);
            Assert.AreEqual(0, snaps[0].Units[0].Y);
            Assert.AreEqual(500, snaps[0].Units[0].Hp, "开战满血");
            Assert.AreEqual(0, snaps[0].Units[0].Mana);
            Assert.IsTrue(snaps[0].Units[0].IsPlayerSide);
            Assert.AreEqual(enemyId, snaps[0].Units[1].UnitId);
            Assert.AreEqual(4, snaps[0].Units[1].X);
            Assert.AreEqual(0, snaps[0].Units[1].Y);
            Assert.AreEqual(400, snaps[0].Units[1].Hp);
            Assert.IsFalse(snaps[0].Units[1].IsPlayerSide);

            // 帧序：tick 严格递增且唯一
            for (int i = 1; i < snaps.Count; i++)
                Assert.Less(snaps[i - 1].Tick, snaps[i].Tick, "帧 tick 严格递增（同 tick 去重的验收）");

            // 每 30 tick 必有帧
            for (int t = GameConst.TicksPerSecond; t <= r.TickCount; t += GameConst.TicksPerSecond)
                Assert.IsTrue(HasFrame(snaps, t), $"整秒 tick {t} 缺帧");

            // 死亡 tick 必有帧（结构性时刻）
            for (int i = 0; i < r.Stream.Count; i++)
                if (r.Stream[i].Kind == StreamEventKind.UnitDied)
                    Assert.IsTrue(HasFrame(snaps, r.Stream[i].Tick), $"死亡 tick {r.Stream[i].Tick} 缺帧");

            // 终局帧：tick = 总步数；死者缺席（缺席即可判死）；条目自洽
            var finalFrame = snaps[snaps.Count - 1];
            Assert.AreEqual(r.TickCount, finalFrame.Tick, "终局帧 tick = 总步数");
            bool heroIn = false;
            for (int i = 0; i < finalFrame.Units.Length; i++)
            {
                Assert.IsTrue(finalFrame.Units[i].Alive, "扫描口径只收存活者");
                if (finalFrame.Units[i].UnitId == heroId) heroIn = true;
                Assert.IsFalse(finalFrame.Units[i].UnitId == enemyId, "死者缺席终局帧");
            }
            Assert.IsTrue(heroIn, "幸存者在终局帧中");
        }

        private static bool HasFrame(BattleSnapshotSequence snaps, int tick)
        {
            for (int i = 0; i < snaps.Count; i++)
                if (snaps[i].Tick == tick) return true;
            return false;
        }

        // ─────────────────────── ⑨ 释放语义 ───────────────────────

        [Test]
        public void Preview_Release_ClearsStreamAndSnapshots()
        {
            var (s, p, r) = RunCoveragePreview(SeedA);
            Assert.Greater(r.Stream.Count, 0);
            int capacity = r.Stream.Capacity;

            p.Release();
            Assert.AreEqual(0, r.Stream.Count, "释放后流计数归零（旧视图作废）");
            Assert.AreEqual(0, r.Snapshots.Count, "释放后快照清空");
            Assert.AreEqual(capacity, r.Stream.Capacity, "容量保留（复用语义）");
        }

        // ─────────────────────── ⑩ 后台线程路径 ───────────────────────

        [Test]
        public void Preview_BackgroundThread_IdenticalToSyncRun()
        {
            var sync = RunCoveragePreview(SeedA);

            var s2 = new GameSession(new EventBus(), SeedA, CoverageTables());
            SetupCoverage(s2);
            var p2 = new BattlePreviewer();
            Assert.IsTrue(p2.Start(s2));
            BattlePreviewResult bg = p2.RunToEndInBackground().Result;   // 阻塞等预演线程（纯 C#，无主线程亲和）

            Assert.AreEqual(sync.r.Stream.Count, bg.Stream.Count, "条数一致");
            for (int i = 0; i < sync.r.Stream.Count; i++)
                Assert.IsTrue(sync.r.Stream[i].Equals(bg.Stream[i]),
                    $"后台线程第 {i} 条漂移：[{DescribeItem(sync.r.Stream[i])}] vs [{DescribeItem(bg.Stream[i])}]");
            Assert.AreEqual(sync.r.TickCount, bg.TickCount);
            Assert.AreEqual(sync.r.PlayerWon, bg.PlayerWon);
        }

        // ─────────────────────── ⑪ DoT 死亡镜像锁（4B1 规划器的镜像依据） ───────────────────────

        [Test]
        public void Preview_DotDeath_NoRemovedAndCorpseKeepsCell()
        {
            var s = new GameSession(new EventBus(), SeedA, DotKillTables());
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            Assert.IsTrue(s.PlaceOnBoard(s.Bench[0].Id, new Pos(0, 0)));   // 距 (4,0) 病鼠 4 格——普攻够得着但 10 秒才出手
            var p = new BattlePreviewer();
            Assert.IsTrue(p.Start(s));
            var r = p.RunToEnd();

            Assert.IsTrue(r.PlayerWon, "病鼠死于毒——尸体不计存活，判胜");
            int enemyId = s.EnemyRoster[0].Id;

            StreamItem died = FindItem(r.Stream, StreamEventKind.UnitDied, enemyId);
            Assert.AreEqual(StreamEventKind.UnitDied, died.Kind, "毒杀必须留下 Died 条目（FindItem 未命中 = Kind=0）");

            bool hasRemoved = false;
            for (int i = 0; i < r.Stream.Count; i++)
                if (r.Stream[i].Kind == StreamEventKind.UnitRemoved && r.Stream[i].IdA == enemyId) { hasRemoved = true; break; }
            Assert.IsFalse(hasRemoved, "DoT 致死不走 HandleKill——无 Removed 条目（逻辑层现状，4B1 按此镜像）");

            // 尸体占格：死亡格仍被占（镜像契约——表现层该格不释放）
            Assert.IsNotNull(s.Board.GetOccupant(new Pos(died.CellX, died.CellY)),
                "DoT 尸体留在棋盘占格（逻辑层现状的如实镜像）");

            // 掉落记账对 DoT 死亡同样生效
            Assert.AreEqual(1, s.OresOnGround.Count, "带矿单位死于 DoT 照样掉矿");
        }

        private static StreamItem FindItem(BattleEventStream stream, StreamEventKind kind, int idA)
        {
            for (int i = 0; i < stream.Count; i++)
                if (stream[i].Kind == kind && stream[i].IdA == idA) return stream[i];
            return default;
        }

        // ─────────────────────── ⑫ 基准冒烟（全量走菜单，见 PreviewBenchmark） ───────────────────────

        [Test]
        public void Preview_BenchmarkSmoke_ReportProduced()
        {
            string report = PreviewBenchmark.RunBenchmark(2, 20261102u);
            Assert.IsNotNull(report);
            StringAssert.Contains("P50", report, "报告含分位数");
            StringAssert.Contains("预演", report, "报告含口径说明");
        }
    }
}
