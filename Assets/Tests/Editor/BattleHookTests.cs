using System;
using System.Collections.Generic;
using System.Linq;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    [TestFixture]
    public class BattleHookTests
    {
        // ── 基建 ──

        private static UnitStats Stats(int hp, int atk, int range = 1, float atkIv = 1.0f, float moveIv = 1.0f,
            int crit = 0, int critDmg = 50, int maxMana = 0, int manaPerHit = 0, int manaRegen = 0)
            => new UnitStats
            {
                MaxHp = hp, AttackPower = atk, Armor = 0, MagicResist = 0, AbilityPower = 100,
                AttackRange = range, AttackInterval = atkIv, MoveInterval = moveIv,
                CritChance = crit, CritDamage = critDmg,
                MaxMana = maxMana, ManaPerHit = manaPerHit, ManaRegenPerSecond = manaRegen,
            };

        private static UnitStats WithArmor(UnitStats s, int armor)
        {
            s.Armor = armor;
            s.MagicResist = armor;
            return s;
        }

        private static Unit Place(EventBus bus, BoardData board, int id, bool playerSide, Pos pos, UnitStats stats)
        {
            var u = new Unit(id, 0, playerSide, stats, bus);
            Assert.IsTrue(board.TryPlace(u, pos), $"摆位失败：{(playerSide ? "玩家" : "敌方")} {pos}");
            return u;
        }

        private static void RunToEnd(CombatSystem combat, int maxTicks = 3000)
        {
            int guard = 0;
            while (!combat.IsOver && guard++ < maxTicks) combat.Tick();
        }

        /// <summary>OnBeforeAttack 的 ref 委托——修改必须回传到真正的 draft（Action 拷贝会丢改动）。</summary>
        private delegate void DraftAct(ref AttackDraft draft);

        /// <summary>
        /// 录制钩子：六挂点默认只录"名:挂点"进共享时间线；可经回调注入行为/捕获载荷。
        /// 没有任何回调时它就是"空钩子"——零副作用，用于回归对照。
        /// </summary>
        private sealed class RecordingHook : IBattleHook
        {
            private readonly List<string> _log;
            private readonly string _name;

            public Action<BattleContext> StartAct;
            public DraftAct BeforeAct;
            public Action<AttackResult> AfterAct;
            public Action<DamageResult> DamagedAct;
            public Action<BattleContext, DeathInfo> DeathAct;
            public Action<BattleContext> TickEndAct;

            public int SourceId { get; }

            public RecordingHook(List<string> log, string name, int sourceId)
            {
                _log = log;
                _name = name;
                SourceId = sourceId;
            }

            public void OnBattleStart(BattleContext ctx) { _log.Add(_name + ":start"); StartAct?.Invoke(ctx); }
            public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft) { _log.Add(_name + ":before"); BeforeAct?.Invoke(ref draft); }
            public void OnAfterAttack(BattleContext ctx, in AttackResult result) { _log.Add(_name + ":after"); AfterAct?.Invoke(result); }
            public void OnDamaged(BattleContext ctx, in DamageResult result) { _log.Add(_name + ":damaged"); DamagedAct?.Invoke(result); }
            public void OnDeath(BattleContext ctx, in DeathInfo death) { _log.Add(_name + ":death"); DeathAct?.Invoke(ctx, death); }
            public void OnTickEnd(BattleContext ctx) { _log.Add(_name + ":end"); TickEndAct?.Invoke(ctx); }
        }

        // ── 注册与注销 ──

        [Test]
        public void AddHook_Null_Throws()
        {
            var bus = new EventBus();
            var combat = new CombatSystem(new BoardData(bus), bus, 1u);
            Assert.Throws<ArgumentNullException>(() => combat.AddHook(null));
        }

        [Test]
        public void RemoveHooksBySource_BySource_CountAndGuards()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var combat = new CombatSystem(board, bus, 1u);
            var log = new List<string>();
            int srcA = ModSourceId.TestBase + 1, srcB = ModSourceId.TestBase + 2;

            combat.AddHook(new RecordingHook(log, "h1", srcA));
            combat.AddHook(new RecordingHook(log, "h2", srcA));
            combat.AddHook(new RecordingHook(log, "h3", srcB));

            Assert.AreEqual(2, combat.RemoveHooksBySource(srcA));   // 同源两条全摘
            Assert.AreEqual(0, combat.RemoveHooksBySource(srcA));   // 再摘：空手
            Assert.AreEqual(0, combat.RemoveHooksBySource(ModSourceId.TestBase + 5));   // 未知源：0
            Assert.Throws<ArgumentException>(() => combat.RemoveHooksBySource(ModSourceId.None));   // 0 不是"一个来源"

            // 摘剩下的钩子照常工作：双方各一单位（首 tick 只充条不出手）
            Place(bus, board, 1, true, new Pos(0, 0), Stats(100, 10, range: 5));
            Place(bus, board, 2, false, new Pos(3, 4), Stats(100, 10, range: 1));
            combat.Tick();
            CollectionAssert.AreEqual(new[] { "h3:start", "h3:end" }, log);   // h1/h2 已按源摘干净
        }

        // ── 回归红线 ──

        [Test]
        public void EmptyHooks_EventStreamIdenticalToNoHooks()
        {
            // 回归红线直接断言：空钩子注册前后，完整事件流逐条一致（骨架零污染阶段1 行为）
            var a = RunRichBattle(null);
            var b = RunRichBattle(new IBattleHook[]
            {
                new RecordingHook(new List<string>(), "空甲", ModSourceId.TestBase + 1),
                new RecordingHook(new List<string>(), "空乙", ModSourceId.TestBase + 2),
            });
            CollectionAssert.AreEqual(a.Log, b.Log, "空钩子不得改变任何事件");
            Assert.AreEqual(a.Ticks, b.Ticks);
            Assert.AreEqual(a.Rolls, b.Rolls);
        }

        /// <summary>丰富战斗（移动/暴击掷骰/回蓝/死亡全路径），返回全事件字符串流——两场同种子必须逐条一致。</summary>
        private static (List<string> Log, int Ticks, int Rolls) RunRichBattle(IBattleHook[] hooks)
        {
            var bus = new EventBus();
            var log = new List<string>();
            var board = new BoardData(bus);
            bus.Subscribe<UnitPlacedEvent>(e => log.Add($"placed {e.UnitId} {e.Cell.X},{e.Cell.Y} {e.IsPlayerSide}"));
            bus.Subscribe<UnitMovedEvent>(e => log.Add($"moved {e.UnitId} {e.FromCell.X},{e.FromCell.Y}>{e.ToCell.X},{e.ToCell.Y}"));
            bus.Subscribe<UnitAttackedEvent>(e => log.Add($"attacked {e.AttackerId}->{e.TargetId} crit={e.IsCrit}"));
            bus.Subscribe<UnitDamagedEvent>(e => log.Add($"damaged {e.UnitId} by{e.SourceUnitId} {e.FinalDamage} hp={e.CurrentHp}"));
            bus.Subscribe<UnitDiedEvent>(e => log.Add($"died {e.UnitId} {e.Cell.X},{e.Cell.Y}"));
            bus.Subscribe<UnitRemovedEvent>(e => log.Add($"removed {e.UnitId} {e.Cell.X},{e.Cell.Y}"));
            bus.Subscribe<UnitHealedEvent>(e => log.Add($"healed {e.UnitId} {e.Amount} hp={e.CurrentHp}"));
            bus.Subscribe<UnitMaxHpChangedEvent>(e => log.Add($"maxhp {e.UnitId} {e.MaxHp}"));
            bus.Subscribe<UnitRestoredEvent>(e => log.Add($"restored {e.UnitId}"));
            bus.Subscribe<ManaChangedEvent>(e => log.Add($"mana {e.UnitId} {e.CurrentMana}/{e.MaxMana}"));
            bus.Subscribe<BattleEndedEvent>(e => log.Add($"ended {e.PlayerWon} {e.TotalTicks}"));

            Place(bus, board, 1, true, new Pos(0, 0),
                Stats(200, 20, 1, 0.5f, 1.0f, crit: 50, maxMana: 30, manaPerHit: 10, manaRegen: 2));
            Place(bus, board, 2, false, new Pos(3, 3), Stats(100, 10));

            var combat = new CombatSystem(board, bus, 777u);
            if (hooks != null)
                foreach (var h in hooks) combat.AddHook(h);
            RunToEnd(combat);

            Assert.IsTrue(combat.IsOver);
            Assert.Greater(combat.RollCount, 0, "暴击率 50 的配置必须真的掷了骰——否则两条恒等流没有意义");
            return (log, combat.TickCount, combat.RollCount);
        }

        // ── 六挂点时间线与载荷 ──

        [Test]
        public void FullTimeline_SixMountPointsAndPayloads()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var log = new List<string>();
            AttackDraft seenDraft = default;
            AttackResult seenAfter = default;
            DamageResult seenDamaged = default;
            DeathInfo seenDeath = default;

            var a = Place(bus, board, 1, true, new Pos(0, 0), Stats(1000, 100, range: 5));
            var b = Place(bus, board, 2, false, new Pos(3, 3), Stats(50, 1, range: 1));   // 一击死：整条时间线在一个 tick 内闭合

            var hook = new RecordingHook(log, "h", ModSourceId.TestBase)
            {
                BeforeAct = (ref AttackDraft d) => seenDraft = d,
                AfterAct = r => seenAfter = r,
                DamagedAct = r => seenDamaged = r,
                DeathAct = (ctx, x) => seenDeath = x,
            };

            var combat = new CombatSystem(board, bus, 5u);
            combat.AddHook(hook);
            RunToEnd(combat);

            // 钩子时间线：start 唯一且最先；击杀 tick 的挂点序 = before → after → damaged → death → end
            Assert.AreEqual(1, log.Count(s => s == "h:start"));
            Assert.AreEqual("h:start", log[0]);
            CollectionAssert.AreEqual(
                new[] { "h:before", "h:after", "h:damaged", "h:death", "h:end" },
                log.GetRange(log.Count - 5, 5));

            // 载荷逐项
            Assert.AreSame(a, seenDraft.Attacker);
            Assert.AreSame(b, seenDraft.Target);
            Assert.IsFalse(seenDraft.IsCrit);              // 攻方暴击率 0 → 掷骰根本没发生
            Assert.AreEqual(0, seenDraft.FlatPen);        // 未填的四个乘区参数全 0
            Assert.AreEqual(0, seenDraft.PercentPen);
            Assert.AreEqual(0f, seenDraft.DamageAmpPercent);
            Assert.AreEqual(0f, seenDraft.DamageReductionPercent);

            Assert.AreSame(a, seenAfter.Attacker);
            Assert.AreEqual(50, seenAfter.DamageApplied);  // 攻 100 血 50：溢出截断，实际伤害 = 目标血量
            Assert.IsTrue(seenAfter.TargetDied);
            Assert.IsFalse(seenAfter.IsCrit);

            Assert.AreSame(b, seenDamaged.Target);
            Assert.AreEqual(50, seenDamaged.Damage);
            Assert.AreEqual(0, seenDamaged.CurrentHp);
            Assert.IsTrue(seenDamaged.Died);               // 致死攻击也发受伤钩子——伤害确实发生了

            Assert.AreSame(b, seenDeath.Dead);
            Assert.AreSame(a, seenDeath.Killer);           // #22④：死亡钩子携带击杀者
            Assert.AreEqual(new Pos(3, 3), seenDeath.Cell); // 尸体清出前的最后一格
        }

        [Test]
        public void OnBeforeAttack_DamageMods_GoThroughFormula()
        {
            // 子场景一：增伤乘区——基础 10，钩子 +100% 增伤 → 20（精确账目走 DamageFormula）
            int ampBaseline = RunCaptureApplied(atk: 10, armor: 0, wire: null);
            Assert.AreEqual(10, ampBaseline);
            int ampApplied = RunCaptureApplied(atk: 10, armor: 0, wire: h => h.BeforeAct = (ref AttackDraft d) => d.DamageAmpPercent += 100f);
            Assert.AreEqual(20, ampApplied);

            // 子场景二：固定穿透——甲 100（承伤 100/200=50%），基础 5；FlatPen=100 → 有效甲 0 → 10
            int penBaseline = RunCaptureApplied(atk: 10, armor: 100, wire: null);
            Assert.AreEqual(5, penBaseline);
            int penApplied = RunCaptureApplied(atk: 10, armor: 100, wire: h => h.BeforeAct = (ref AttackDraft d) => d.FlatPen = 100);
            Assert.AreEqual(10, penApplied);
        }

        /// <summary>跑一场打不死人的战斗（目标血 1000），捕获第一次出手的实际伤害。</summary>
        private static int RunCaptureApplied(int atk, int armor, Action<RecordingHook> wire)
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            Place(bus, board, 1, true, new Pos(0, 0), Stats(1000, atk, range: 5));
            Place(bus, board, 2, false, new Pos(3, 3), WithArmor(Stats(1000, 1, range: 1), armor));

            int applied = -1;
            var hook = new RecordingHook(new List<string>(), "h", ModSourceId.TestBase)
            {
                AfterAct = r => applied = r.DamageApplied,
            };
            wire?.Invoke(hook);

            var combat = new CombatSystem(board, bus, 3u);
            combat.AddHook(hook);
            int guard = 0;
            while (applied == -1 && guard++ < 100) combat.Tick();
            Assert.AreNotEqual(-1, applied, "100 步内必须出手过一次");
            return applied;
        }

        [Test]
        public void OnBeforeAttack_IsCritFrozen()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            Place(bus, board, 1, true, new Pos(0, 0), Stats(1000, 10, range: 5, crit: 100));
            Place(bus, board, 2, false, new Pos(3, 3), Stats(1000, 1, range: 1));

            AttackResult seen = default;
            bool draftSeenCrit = false;
            var hook = new RecordingHook(new List<string>(), "h", ModSourceId.TestBase)
            {
                // 恶意改：D2-3 说暴击已掷定不许改——骨架只透传 IsCrit，结算用 CombatSystem 本地变量
                BeforeAct = (ref AttackDraft d) => { draftSeenCrit = d.IsCrit; d.IsCrit = false; },
                AfterAct = r => seen = r,
            };

            var combat = new CombatSystem(board, bus, 9u);
            combat.AddHook(hook);
            int guard = 0;
            while (seen.DamageApplied == 0 && guard++ < 100) combat.Tick();

            Assert.IsTrue(draftSeenCrit, "暴击率 100：钩子看到的 IsCrit 已掷定为 true");
            Assert.IsTrue(seen.IsCrit, "钩子改 draft.IsCrit 无效（D2-3 结构化锁死）");
            Assert.AreEqual(15, seen.DamageApplied);   // 10 × (1+50/100) = 15，甲 0 无折扣——暴击账目照走
        }

        [Test]
        public void OnDeath_BetweenDiedAndRemovedEvents()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var log = new List<string>();
            bus.Subscribe<UnitAttackedEvent>(e => log.Add("evt:attacked"));
            bus.Subscribe<UnitDamagedEvent>(e => log.Add("evt:damaged"));
            bus.Subscribe<UnitDiedEvent>(e => log.Add("evt:died"));
            bus.Subscribe<UnitRemovedEvent>(e => log.Add("evt:removed"));
            bus.Subscribe<BattleEndedEvent>(e => log.Add("evt:ended"));

            Place(bus, board, 1, true, new Pos(0, 0), Stats(1000, 100, range: 5));
            Place(bus, board, 2, false, new Pos(3, 3), Stats(50, 1, range: 1));

            var combat = new CombatSystem(board, bus, 5u);
            combat.AddHook(new RecordingHook(log, "h", ModSourceId.TestBase));
            RunToEnd(combat);

            int iDied = log.IndexOf("evt:died");
            int iDeath = log.IndexOf("h:death");
            int iRemoved = log.IndexOf("evt:removed");
            Assert.Greater(iDeath, iDied, "死亡钩子在 UnitDiedEvent 之后——死亡事实先陈述");
            Assert.Less(iDeath, iRemoved, "死亡钩子在尸体清出之前——钩子还能读到死者最后一格（#22⑤ 战绩/亡语类效果的前提）");
            Assert.Greater(log.IndexOf("evt:ended"), log.IndexOf("h:end"), "每 tick 结束钩子在胜负广播之前");
            Assert.AreEqual("evt:ended", log[log.Count - 1], "BattleEndedEvent 恒为本 tick 最后一条事件（钩子没有打乱既有事件序）");
        }

        // ── OnBattleStart / OnTickEnd 的时机 ──

        [Test]
        public void OnBattleStart_OnceBeforeAllActions_BoardVisible()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var log = new List<string>();
            int playersOnStart = -1, enemiesOnStart = -1;

            Place(bus, board, 1, true, new Pos(0, 0), Stats(1000, 10, range: 5));
            Place(bus, board, 2, false, new Pos(3, 3), Stats(200, 1, range: 1));   // 血 200：多 tick 战斗

            var hook = new RecordingHook(log, "h", ModSourceId.TestBase)
            {
                StartAct = ctx => { playersOnStart = ctx.Board.CountOnBoard(true); enemiesOnStart = ctx.Board.CountOnBoard(false); },
            };
            var combat = new CombatSystem(board, bus, 5u);
            combat.AddHook(hook);
            RunToEnd(combat);

            Assert.AreEqual(1, log.Count(s => s == "h:start"), "一场只触发一次");
            Assert.AreEqual("h:start", log[0], "先于一切出手/移动——战斗开始类效果必须先于任何单位行动生效");
            Assert.Less(log.IndexOf("h:start"), log.IndexOf("h:before"));
            Assert.AreEqual(1, playersOnStart, "钩子视角棋盘可用：战斗开始时双方都在场");
            Assert.AreEqual(1, enemiesOnStart);
        }

        [Test]
        public void ConstructInstantOver_NoTick_NoHookFires()
        {
            var bus = new EventBus();
            var log = new List<string>();
            var combat = new CombatSystem(new BoardData(bus), bus, 5u);   // 双方空场：构造即终局
            combat.AddHook(new RecordingHook(log, "h", ModSourceId.TestBase));

            Assert.IsTrue(combat.IsOver);
            Assert.AreEqual(0, log.Count, "空场战斗从不 Tick——战斗根本没发生，钩子永不触发（Session 保证不 Tick，此测试锁语义）");
        }

        [Test]
        public void OnTickEnd_FiresEveryTick_BeforeBattleEnded()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var log = new List<string>();
            bus.Subscribe<BattleEndedEvent>(e => log.Add("evt:ended"));

            Place(bus, board, 1, true, new Pos(0, 0), Stats(1000, 10, range: 5, atkIv: 0.5f));
            Place(bus, board, 2, false, new Pos(3, 3), Stats(200, 1, range: 1));   // 20 击结束 ≈ 300 tick

            var combat = new CombatSystem(board, bus, 5u);
            combat.AddHook(new RecordingHook(log, "h", ModSourceId.TestBase));
            RunToEnd(combat);

            Assert.AreEqual(combat.TickCount, log.Count(s => s == "h:end"), "每 tick 恰好一次（含分出胜负的那一 tick）");
            Assert.AreEqual("h:end", log[log.Count - 2], "末 tick：OnTickEnd 在胜负结算之前");
            Assert.AreEqual("evt:ended", log[log.Count - 1], "BattleEndedEvent 依旧是本 tick 最后一条事件");
        }

        // ── 多钩子与快照遍历 ──

        [Test]
        public void MultipleHooks_RegistrationOrder()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var log = new List<string>();

            Place(bus, board, 1, true, new Pos(0, 0), Stats(1000, 100, range: 5));
            Place(bus, board, 2, false, new Pos(3, 3), Stats(50, 1, range: 1));

            var combat = new CombatSystem(board, bus, 5u);
            combat.AddHook(new RecordingHook(log, "h1", ModSourceId.TestBase + 1));
            combat.AddHook(new RecordingHook(log, "h2", ModSourceId.TestBase + 2));
            RunToEnd(combat);

            Assert.Less(log.IndexOf("h1:start"), log.IndexOf("h2:start"), "同挂点按注册序：先注册的先触发");
            Assert.AreEqual("h2:start", log[log.IndexOf("h1:start") + 1], "两钩子的 start 相邻");
            Assert.Less(log.IndexOf("h1:end"), log.IndexOf("h2:end"));
        }

        [Test]
        public void SnapshotIteration_RemoveTakesEffectNextDispatch()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var log = new List<string>();

            Place(bus, board, 1, true, new Pos(0, 0), Stats(1000, 10, range: 5));
            Place(bus, board, 2, false, new Pos(3, 3), Stats(2000, 1, range: 1));   // 血 2000：两 tick 内打不死

            int srcB = ModSourceId.TestBase + 2;
            var combat = new CombatSystem(board, bus, 5u);
            var hookA = new RecordingHook(log, "a", ModSourceId.TestBase + 1)
            {
                TickEndAct = _ => combat.RemoveHooksBySource(srcB),   // 当轮 dispatch 中注销排在后面的 b
            };
            combat.AddHook(hookA);
            combat.AddHook(new RecordingHook(log, "b", srcB));

            combat.Tick();   // tick1：start 双触发；tick-end：a 先触发并注销 b → b 在快照里，本轮照触发
            combat.Tick();   // tick2：b 已不在账本，tick-end 不再触发

            Assert.AreEqual(1, log.Count(s => s == "b:start"), "start dispatch 在注销发生前：b:start 一次");
            Assert.AreEqual(1, log.Count(s => s == "b:end"), "快照遍历：当轮已注销的 b 照样触发这一次（EventBus 同款纪律）");
            Assert.AreEqual(2, log.Count(s => s == "a:end"), "a 每轮都在");
            Assert.Less(log.IndexOf("a:end"), log.IndexOf("b:end"), "注册序：a 的 tick-end 先于 b");
        }

        // ── 攻击条推进（#22④ 暗影 6 档骨架口） ──

        [Test]
        public void PushAttackProgress_AdvancesAndCapsNextAttack()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            Place(bus, board, 1, true, new Pos(0, 0), Stats(1000, 10, range: 5));   // A：攻 10，攻击间隔 1 秒（30 步/击）
            var unitB = Place(bus, board, 2, false, new Pos(3, 3), Stats(10, 1, range: 1));   // B：一击死
            var unitC = Place(bus, board, 3, false, new Pos(4, 3), Stats(30, 1, range: 1));    // C：三击死

            var combat = new CombatSystem(board, bus, 5u);
            var attacks = new List<(int Tick, int Target)>();
            bus.Subscribe<UnitAttackedEvent>(e => attacks.Add((combat.TickCount, e.TargetId)));

            // 两个钩子都在击杀时给击杀者推 0.9 条——封顶蓄满（0.9+0.9 → 1.0），不产生攻击欠条
            combat.AddHook(new RecordingHook(new List<string>(), "推一", ModSourceId.TestBase + 1)
            { DeathAct = (ctx, d) => ctx.PushAttackProgress(d.Killer, 0.9f) });
            combat.AddHook(new RecordingHook(new List<string>(), "推二", ModSourceId.TestBase + 2)
            { DeathAct = (ctx, d) => ctx.PushAttackProgress(d.Killer, 0.9f) });
            RunToEnd(combat);

            // 出手序列：B 一击死（第 1 击）→ C 三击死（第 2~4 击）
            Assert.AreEqual(4, attacks.Count);
            Assert.AreEqual(unitB.Id, attacks[0].Target);
            Assert.AreEqual(unitC.Id, attacks[1].Target);

            // 推条生效：第二击紧随第一击（≤4 tick）——不推条时本应隔约 30 tick
            Assert.LessOrEqual(attacks[1].Tick - attacks[0].Tick, 4, "击杀推条 ×2 封顶蓄满：下一击几乎立即发生");

            // 封顶（无攻击欠条）：第二击后条归零起步，第三击恢复正常间隔 ~30 tick（±1/30 浮点抖动放宽）
            int gap = attacks[2].Tick - attacks[1].Tick;
            Assert.GreaterOrEqual(gap, 27, "若未封顶，0.9+0.9=1.8 会让第三击提前到 ~6 tick——封顶后必须恢复正常间隔");
            Assert.LessOrEqual(gap, 34);
            Assert.IsTrue(combat.PlayerWon);
        }
    }
}
