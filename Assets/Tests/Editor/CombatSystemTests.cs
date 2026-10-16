using System.Collections.Generic;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    // 战斗事件记录器：按到达顺序记事件名（时序断言的核心工具）
    internal sealed class CombatEventLog
    {
        public readonly List<string> Order = new List<string>();
        public readonly List<UnitAttackedEvent> Attacks = new List<UnitAttackedEvent>();
        public readonly List<UnitDamagedEvent> Damages = new List<UnitDamagedEvent>();
        public int Ended, Attacked, Moved, Damaged, Died, Removed, Healed;

        public void Attach(EventBus bus)
        {
            bus.Subscribe<UnitAttackedEvent>(e => { Attacked++; Order.Add("Attacked"); Attacks.Add(e); });
            bus.Subscribe<UnitDamagedEvent>(e => { Damaged++; Order.Add("Damaged"); Damages.Add(e); });
            bus.Subscribe<UnitDiedEvent>(e => { Died++; Order.Add("Died"); });
            bus.Subscribe<UnitRemovedEvent>(e => { Removed++; Order.Add("Removed"); });
            bus.Subscribe<UnitHealedEvent>(e => { Healed++; Order.Add("Healed"); });
            bus.Subscribe<UnitMovedEvent>(e => { Moved++; Order.Add("Moved"); });
            bus.Subscribe<BattleEndedEvent>(e => { Ended++; Order.Add("Ended"); });
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 时间线换算说明（步长 1/30 秒 = 每秒30步，批次5.6）：
    //  · 攻击/移动间隔 N 秒 = 攒 30N 步（浮点±1步抖动 → 断言用区间或"第N步前必发生"安全边际）
    //  · 回蓝走整数点数账本（RegenMana），精确无浮点 → 回蓝断言可钉死具体步数
    // ─────────────────────────────────────────────────────────────
    [TestFixture]
    public class CombatSystemTests
    {
        private EventBus _bus;
        private BoardData _board;
        private int _nextId;

        [SetUp]
        public void SetUp()
        {
            _bus = new EventBus();
            _board = new BoardData(_bus);
            _nextId = 1;
        }

        // 造一个单位并放上棋盘（放置失败直接让测试炸——站位写错了别静默）
        private Unit Place(bool playerSide, Pos cell, UnitStats stats)
        {
            var u = new Unit(_nextId++, 1, playerSide, stats, _bus);
            Assert.IsTrue(_board.TryPlace(u, cell), $"预备放置失败：{(playerSide ? "玩家" : "敌方")} @ {cell}");
            return u;
        }

        // 统一样板：默认无暴击（crit 0）→ 测试序列与随机无关，稳定可手推
        private static UnitStats Stats(int maxHp = 50, int attack = 14, int armor = 0, int range = 1,
            float atkInterval = 1.0f, float moveInterval = 1.0f, int crit = 0, int critDmg = 50)
        {
            UnitStats s = DefFactory.SampleStats;
            s.MaxHp = maxHp;
            s.AttackPower = attack;
            s.Armor = armor;
            s.MagicResist = 0;
            s.AttackRange = range;
            s.AttackInterval = atkInterval;
            s.MoveInterval = moveInterval;
            s.CritChance = crit;
            s.CritDamage = critDmg;
            return s;
        }

        private static void RunToCompletion(CombatSystem combat)
        {
            // 注意：IsOver 是查棋盘的活查询——空敌方的战斗一构造就是 IsOver，
            // 这种局面要用手动 Tick 收尾（见 EmptyEnemySide 测试），不走本方法。
            for (int i = 0; i < GameConst.MaxTicks + 10 && !combat.IsOver; i++)
                combat.Tick();
        }

        // ─────────────── 胜负路径 ───────────────

        [Test]
        public void CrushBattle_PlayerWinsFast_NoRolls()
        {
            var hero = Place(true, new Pos(0, 2), Stats(maxHp: 1000, attack: 999, range: 3, atkInterval: 0.5f));
            var foe = Place(false, new Pos(0, 5), Stats(maxHp: 50, attack: 1));

            var log = new CombatEventLog();
            log.Attach(_bus);
            var combat = new CombatSystem(_board, _bus);
            RunToCompletion(combat);

            Assert.IsTrue(combat.IsOver);
            Assert.IsTrue(combat.PlayerWon);
            // 攻击间隔0.5秒 = 攒15步（浮点±1步抖动）→ 首刀即一击终局
            Assert.That(combat.TickCount, Is.InRange(15, 16));
            Assert.AreEqual(1, log.Attacks.Count);
            Assert.AreEqual(0, combat.RollCount);  // 暴击率 0：一次骰都没掷
            Assert.AreEqual(1, log.Ended);
        }

        [Test]
        public void HopelessBattle_PlayerLoses()
        {
            var lamb = Place(true, new Pos(0, 2), Stats(maxHp: 10, attack: 1, range: 1));
            var wolf = Place(false, new Pos(0, 5), Stats(maxHp: 1000, attack: 999, range: 3));

            var log = new CombatEventLog();
            log.Attach(_bus);
            var combat = new CombatSystem(_board, _bus);
            RunToCompletion(combat);

            Assert.IsTrue(combat.IsOver);
            Assert.IsFalse(combat.PlayerWon);       // 打不过即败
            // 羔羊移动条与恶狼攻击条都是1.0秒=30步，同一步攒满：羔羊先挪、恶狼后杀
            Assert.That(combat.TickCount, Is.InRange(30, 31));
            Assert.AreEqual(1, log.Ended);
        }

        [Test]
        public void Timeout_CountsAsPlayerDefeat()
        {
            var pWall = Place(true, new Pos(0, 2), Stats(maxHp: 1000000, attack: 1, range: 1));
            var eWall = Place(false, new Pos(0, 5), Stats(maxHp: 1000000, attack: 1, range: 1));

            var log = new CombatEventLog();
            log.Attach(_bus);
            var combat = new CombatSystem(_board, _bus);
            RunToCompletion(combat);

            Assert.IsTrue(combat.IsOver);
            Assert.IsFalse(combat.PlayerWon);       // 超时（双方都活着）按玩家战败
            Assert.AreEqual(GameConst.MaxTicks, combat.TickCount); // 4500步=150秒，随常量自适应
            Assert.AreEqual(1, _board.CountOnBoard(true));   // 两堵墙都活着打满全场
            Assert.AreEqual(1, _board.CountOnBoard(false));
            Assert.AreEqual(1, log.Ended);
        }

        [Test]
        public void RangedGetsFreeHits_WhileMeleeApproaches()
        {
            // 弩手射程3/攻14/攻击间隔1秒；蛮兵血30/攻99/近战/移动间隔1秒，从 3 格外追进来
            var archer = Place(true, new Pos(0, 2), Stats(attack: 14, range: 3));
            var brute = Place(false, new Pos(0, 5), Stats(maxHp: 30, attack: 99, range: 1));

            var log = new CombatEventLog();
            log.Attach(_bus);
            var combat = new CombatSystem(_board, _bus);
            RunToCompletion(combat);

            // 追击时间线（每秒30步）：弩手每30步一箭（第30/60/90步±1），蛮兵每30步挪一格——
            // 第3箭时蛮兵才追到跟前，玩家侧先行动，轮不到蛮兵出手
            Assert.IsTrue(combat.PlayerWon);
            Assert.That(combat.TickCount, Is.InRange(90, 93));
            Assert.AreEqual(3, log.Attacks.Count);
            foreach (var atk in log.Attacks)
                Assert.AreEqual(archer.Id, atk.AttackerId, "蛮兵全程没捞到一次出手");
            Assert.AreEqual(50, archer.CurrentHp);  // 无伤
        }

        // ─────────────── 索敌与目标锁定 ───────────────

        [Test]
        public void AcquireTarget_TieBreaksByRowMajorScanOrder()
        {
            // 侦察兵 (3,2)；两个敌人切比雪夫距离同为 2：(1,4) 与 (5,4)
            var scout = Place(true, new Pos(3, 2), Stats(attack: 10, range: 1, moveInterval: 0.5f));
            var left = Place(false, new Pos(1, 4), Stats(maxHp: 1000, atkInterval: 999f, moveInterval: 999f));
            var right = Place(false, new Pos(5, 4), Stats(maxHp: 1000, atkInterval: 999f, moveInterval: 999f));

            var combat = new CombatSystem(_board, _bus);
            // 移动间隔0.5秒=攒15步（浮点±1）→ 17步内必走
            for (int i = 0; i < 17; i++) combat.Tick();

            // 并列取先扫到（行主序 → (1,4) 在 (5,4) 之前）→ 第一步朝 (1,4) 迈：(2,3)
            Assert.AreEqual(new Pos(2, 3), scout.Pos);
            Assert.AreEqual(new Pos(1, 4), left.Pos);  // 敌人被 999 间隔钉死，一步没走
            Assert.AreEqual(new Pos(5, 4), right.Pos);
        }

        [Test]
        public void TargetLock_PersistsUntilDeath_ThenReacquires()
        {
            var slayer = Place(true, new Pos(2, 2), Stats(attack: 999, range: 1, atkInterval: 0.5f, moveInterval: 0.5f));
            var near = Place(false, new Pos(2, 3), Stats(maxHp: 1, atkInterval: 999f, moveInterval: 999f));
            var far = Place(false, new Pos(7, 5), Stats(maxHp: 1000000, atkInterval: 999f, moveInterval: 999f));

            var log = new CombatEventLog();
            log.Attach(_bus);
            var combat = new CombatSystem(_board, _bus);

            // 首刀最晚第16步落地（攻击间隔0.5秒=攒15步）：锁近敌 → 一击致死
            for (int i = 0; i < 17; i++) combat.Tick();
            Assert.AreEqual(near.Id, log.Attacks[0].TargetId); // 锁的是近的
            Assert.AreEqual(1, log.Attacks.Count);

            // 近敌死 → 重新索敌锁远敌 → 朝 (7,5) 迈：(3,3)（移动间隔0.5秒，再攒15~16步）
            for (int i = 0; i < 17; i++) combat.Tick();
            Assert.AreEqual(new Pos(3, 3), slayer.Pos);
            Assert.AreEqual(1, log.Attacks.Count); // 远敌够不着，从未出过手
        }

        // ─────────────── 事件时序契约 ───────────────

        [Test]
        public void EventSequence_AttackedDamagedDiedRemovedEnded()
        {
            var killer = Place(true, new Pos(2, 2), Stats(attack: 999, range: 1, atkInterval: 0.5f));
            var victim = Place(false, new Pos(2, 3), Stats(maxHp: 5, atkInterval: 999f, moveInterval: 999f));

            var log = new CombatEventLog();
            log.Attach(_bus);
            var combat = new CombatSystem(_board, _bus);
            for (int i = 0; i < 17; i++) combat.Tick(); // 首刀最晚第16步，一击终局

            // 挥刀 → 掉血 → 倒地 → 让格 → 终局：表现层按这个顺序播演出
            CollectionAssert.AreEqual(
                new[] { "Attacked", "Damaged", "Died", "Removed", "Ended" }, log.Order);
        }

        [Test]
        public void BattleEnded_FiredExactlyOnce_AndTickGuardsAfterEnd()
        {
            var hero = Place(true, new Pos(0, 2), Stats(maxHp: 1000, attack: 999, range: 3, atkInterval: 0.5f));
            Place(false, new Pos(0, 5), Stats(maxHp: 50));

            var log = new CombatEventLog();
            log.Attach(_bus);
            var combat = new CombatSystem(_board, _bus);
            RunToCompletion(combat);

            Assert.AreEqual(1, log.Ended);
            int ticksAtEnd = combat.TickCount;

            for (int i = 0; i < 5; i++) combat.Tick(); // 终局后继续推：收尾守卫顶住

            Assert.AreEqual(ticksAtEnd, combat.TickCount);  // TickCount 不增
            Assert.AreEqual(1, log.Ended);                  // 结束事件不重发
        }

        [Test]
        public void EmptyEnemySide_BattleEndsOnFirstTick()
        {
            Place(true, new Pos(0, 0), Stats());

            BattleEndedEvent last = default;
            int ended = 0;
            _bus.Subscribe<BattleEndedEvent>(e => { last = e; ended++; });

            var combat = new CombatSystem(_board, _bus);

            Assert.IsTrue(combat.IsOver);    // D5 查棋盘：不 tick 也已见分晓
            Assert.IsTrue(combat.PlayerWon);
            Assert.AreEqual(0, combat.TickCount);

            combat.Tick();                   // 结论性的一 tick：只发一次结束事件
            Assert.AreEqual(1, combat.TickCount);
            Assert.AreEqual(1, ended);
            Assert.IsTrue(last.PlayerWon);
            Assert.AreEqual(1, last.TotalTicks);
        }

        // ─────────────── 暴击与掷骰契约（D8/D9） ───────────────

        [Test]
        public void CritChanceZero_NeverRolls_DamageEqualsBase()
        {
            var attacker = Place(true, new Pos(2, 2), Stats(attack: 14, range: 1, atkInterval: 0.5f, crit: 0));
            var tank = Place(false, new Pos(2, 3), Stats(maxHp: 1000000, atkInterval: 999f, moveInterval: 999f));

            var log = new CombatEventLog();
            log.Attach(_bus);
            var combat = new CombatSystem(_board, _bus);
            for (int i = 0; i < 50; i++) combat.Tick(); // 攻击间隔0.5秒=15~16步一刀，50步稳出3刀

            Assert.AreEqual(3, log.Attacks.Count);
            Assert.AreEqual(0, combat.RollCount);  // 暴击率 0 ⇒ 一次都没掷
            foreach (var atk in log.Attacks)
                Assert.IsFalse(atk.IsCrit);
            foreach (var dmg in log.Damages)
                Assert.AreEqual(14, dmg.FinalDamage); // 无暴击公式值：14 打 0 甲 = 14
        }

        [Test]
        public void CritChanceHundred_EveryHitCrits_At150Percent()
        {
            var attacker = Place(true, new Pos(2, 2), Stats(attack: 14, range: 1, atkInterval: 0.5f, crit: 100, critDmg: 50));
            var tank = Place(false, new Pos(2, 3), Stats(maxHp: 1000000, atkInterval: 999f, moveInterval: 999f));

            var log = new CombatEventLog();
            log.Attach(_bus);
            var combat = new CombatSystem(_board, _bus);
            for (int i = 0; i < 50; i++) combat.Tick();

            Assert.AreEqual(3, log.Attacks.Count);
            Assert.AreEqual(3, combat.RollCount);   // 每次出手恰掷一次
            foreach (var atk in log.Attacks)
                Assert.IsTrue(atk.IsCrit);
            foreach (var dmg in log.Damages)
                Assert.AreEqual(21, dmg.FinalDamage); // RoundToInt(14 × 1.5) = 21
        }

        [Test]
        public void NoValidTarget_NeverRolls()
        {
            // 攻 999 的急战侠够不着（射程1 vs 距离3）也走不动（移速999）：永远无法出手
            var eager = Place(true, new Pos(0, 2), Stats(attack: 999, range: 1, moveInterval: 999f));
            Place(false, new Pos(0, 5), Stats(maxHp: 1000000, range: 1, atkInterval: 999f, moveInterval: 999f));

            var log = new CombatEventLog();
            log.Attach(_bus);
            var combat = new CombatSystem(_board, _bus);
            for (int i = 0; i < 300; i++) combat.Tick(); // 300步=10秒（上限4500步=150秒，远未到）

            Assert.AreEqual(0, combat.RollCount);   // 掷骰只发生在真正出手时
            Assert.AreEqual(0, log.Attacks.Count);
            Assert.IsFalse(combat.IsOver);          // 双方都活着，远未超时
        }

        // ─────────────── CombatRules 执行（5.3，规则中立） ───────────────

        [Test]
        public void Lifesteal_HealsAttackerByAppliedDamage()
        {
            var rules = new CombatRules { PlayerLifestealPercent = 50f };
            var vamp = Place(true, new Pos(2, 2), Stats(maxHp: 50, attack: 14, range: 1, atkInterval: 0.5f));
            var bag = Place(false, new Pos(2, 3), Stats(maxHp: 1000000, atkInterval: 999f, moveInterval: 999f));

            vamp.TakeDamage(20, null); // 预先扣到 30/50，留出吸血空间
            var log = new CombatEventLog();
            log.Attach(_bus);
            var combat = new CombatSystem(_board, _bus, rules: rules);
            for (int i = 0; i < 17; i++) combat.Tick(); // 首刀最晚第16步

            // 吸血 = 实际伤害 14 × 50% = 7 → 30 + 7 = 37
            Assert.AreEqual(37, vamp.CurrentHp);
            CollectionAssert.AreEqual(new[] { "Attacked", "Damaged", "Healed" }, log.Order);
        }

        [Test]
        public void KillHeal_RestoresByEffectiveMaxHp()
        {
            var rules = new CombatRules { PlayerKillHealPercent = 20f };
            var hunter = Place(true, new Pos(2, 2), Stats(maxHp: 50, attack: 999, range: 1, atkInterval: 0.5f));
            var prey = Place(false, new Pos(2, 3), Stats(maxHp: 1, atkInterval: 999f, moveInterval: 999f));

            hunter.TakeDamage(20, null); // 30/50
            var log = new CombatEventLog();
            log.Attach(_bus);
            var combat = new CombatSystem(_board, _bus, rules: rules);
            for (int i = 0; i < 17; i++) combat.Tick(); // 一击击杀 → 击杀回复 50 × 20% = 10

            Assert.AreEqual(40, hunter.CurrentHp);
            CollectionAssert.AreEqual(
                new[] { "Attacked", "Damaged", "Died", "Removed", "Healed", "Ended" }, log.Order);
        }
    }
}
