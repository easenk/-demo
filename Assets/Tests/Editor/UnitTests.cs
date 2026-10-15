using System;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    [TestFixture]
    public class UnitTests
    {
        private EventBus _bus;
        private BoardEventCounter _events;
        private int _nextId;

        [SetUp]
        public void SetUp()
        {
            _bus = new EventBus();
            _events = new BoardEventCounter();
            _events.Attach(_bus);
            _nextId = 1;
        }

        private Unit NewUnit(bool playerSide = true)
        {
            return new Unit(_nextId++, 1, playerSide, DefFactory.SampleStats, _bus);
        }

        private static UnitStats Stats(int maxHp = 50, int attack = 14, int range = 1)
        {
            UnitStats s = DefFactory.SampleStats;
            s.MaxHp = maxHp;
            s.AttackPower = attack;
            s.AttackRange = range;
            return s;
        }

        [Test]
        public void Constructor_InitializesFullHp()
        {
            var u = new Unit(7, 3, true, Stats(), _bus);
            Assert.AreEqual(7, u.Id);
            Assert.AreEqual(3, u.CharId);
            Assert.AreEqual(50, u.CurrentHp);
            Assert.IsTrue(u.IsAlive);
            Assert.AreEqual(50, u.Stats.MaxHp);
        }

        [Test]
        public void Constructor_RejectsInvalidStats()
        {
            UnitStats s;

            s = Stats(); s.MaxHp = 0;
            Assert.Throws<ArgumentOutOfRangeException>(() => new Unit(1, 1, true, s, _bus));

            s = Stats(); s.AttackInterval = 0f;
            Assert.Throws<ArgumentOutOfRangeException>(() => new Unit(1, 1, true, s, _bus));

            s = Stats(); s.MoveInterval = 0f;
            Assert.Throws<ArgumentOutOfRangeException>(() => new Unit(1, 1, true, s, _bus));
        }

        [Test]
        public void TakeDamage_Lifecycle_DamagesThenKillsInOrder()
        {
            var u = NewUnit();
            UnitDamagedEvent lastDamaged = default;
            UnitDiedEvent lastDied = default;
            _bus.Subscribe<UnitDamagedEvent>(e => lastDamaged = e);
            _bus.Subscribe<UnitDiedEvent>(e => lastDied = e);

            Assert.AreEqual(20, u.TakeDamage(20, null));  // 第一击
            Assert.AreEqual(30, u.CurrentHp);
            Assert.AreEqual(u.Id, lastDamaged.UnitId);
            Assert.AreEqual(0, lastDamaged.SourceUnitId);  // null 攻击者 → 0（无/非法 id）
            Assert.AreEqual(20, lastDamaged.FinalDamage);
            Assert.AreEqual(30, lastDamaged.CurrentHp);
            Assert.AreEqual(0, _events.Died);

            Assert.AreEqual(30, u.TakeDamage(999, null)); // 致死一击：溢出不记账
            Assert.AreEqual(0, u.CurrentHp);
            Assert.IsFalse(u.IsAlive);
            Assert.AreEqual(30, lastDamaged.FinalDamage); // 实际掉了 30，不是 999
            Assert.AreEqual(u.Id, lastDied.UnitId);
            Assert.AreEqual(u.Pos, lastDied.Cell);        // 死亡只陈述，不涉及销毁
        }

        [Test]
        public void TakeDamage_NonPositiveOrDead_IsSilent()
        {
            var u = NewUnit();

            Assert.AreEqual(0, u.TakeDamage(0, null));    // ≤0：静默
            Assert.AreEqual(0, u.TakeDamage(-5, null));
            Assert.AreEqual(50, u.CurrentHp);
            Assert.AreEqual(0, _events.Damaged);

            u.TakeDamage(999, null); // 击杀
            Assert.AreEqual(0, u.TakeDamage(10, null));    // 死人不受击
            Assert.AreEqual(0, u.CurrentHp);
            Assert.AreEqual(1, _events.Damaged);          // 全程只有致死那一下广播了
            Assert.AreEqual(1, _events.Died);
        }

        [Test]
        public void Heal_CapsAtMax()
        {
            var u = NewUnit();
            u.TakeDamage(20, null);

            UnitHealedEvent lastHealed = default;
            _bus.Subscribe<UnitHealedEvent>(e => lastHealed = e);

            Assert.AreEqual(20, u.Heal(100)); // 只回了缺的 20
            Assert.AreEqual(50, u.CurrentHp);
            Assert.AreEqual(u.Id, lastHealed.UnitId);
            Assert.AreEqual(20, lastHealed.Amount);
            Assert.AreEqual(50, lastHealed.CurrentHp);
        }

        [Test]
        public void Heal_FullOrDead_IsSilent()
        {
            var u = NewUnit();
            Assert.AreEqual(0, u.Heal(10));      // 满血：0 回复且不广播
            Assert.AreEqual(0, _events.Healed);

            u.TakeDamage(999, null);
            Assert.AreEqual(0, u.Heal(50));      // 治疗不能复活死人
            Assert.AreEqual(0, u.CurrentHp);
            Assert.AreEqual(0, _events.Healed);
        }

        [Test]
        public void Death_LeavesCorpseOnBoard()
        {
            var board = new BoardData(_bus);
            var u = NewUnit();
            Assert.IsTrue(board.TryPlace(u, new Pos(2, 1)));

            u.TakeDamage(999, null); // 死亡只翻 bool：板上尸体还在，清场归 CombatSystem

            Assert.AreSame(u, board.GetOccupant(new Pos(2, 1)));
            Assert.IsTrue(u.IsOnBoard);

            Assert.IsTrue(board.Remove(u)); // 尸体让格是 BoardData.Remove 的事
            Assert.IsNull(board.GetOccupant(new Pos(2, 1)));
        }

        [Test]
        public void MaxHp_Grow_AddsDeltaToCurrentHp()
        {
            var u = NewUnit();
            u.TakeDamage(20, null); // hp 30/50

            UnitMaxHpChangedEvent last = default;
            _bus.Subscribe<UnitMaxHpChangedEvent>(e => last = e);

            u.AddModifier(new StatModifier(StatKind.MaxHp, StatOp.Flat, 50f, 3000));
            Assert.AreEqual(100, u.EffectiveStats.MaxHp);
            Assert.AreEqual(80, u.CurrentHp);   // 30 + 50：等量增加
            Assert.AreEqual(u.Id, last.UnitId);
            Assert.AreEqual(100, last.MaxHp);
            Assert.AreEqual(80, last.CurrentHp);
        }

        [Test]
        public void MaxHp_Shrink_ClampsIntoNewCap_NotProportional()
        {
            // 用例A：血 45、上限砍到 5 → 钳进 5（不是 45−45=0→1）
            var a = NewUnit();
            a.TakeDamage(5, null); // hp 45/50
            a.AddModifier(new StatModifier(StatKind.MaxHp, StatOp.Flat, -45f, 3000));
            Assert.AreEqual(5, a.EffectiveStats.MaxHp);
            Assert.AreEqual(5, a.CurrentHp);

            // 用例B：血 3、上限砍到 2 → 血 3>2 钳到 2；血 ≤ 新上限的原样保留
            var b = NewUnit();
            b.TakeDamage(47, null); // hp 3/50
            b.AddModifier(new StatModifier(StatKind.MaxHp, StatOp.Flat, -48f, 3000));
            Assert.AreEqual(2, b.EffectiveStats.MaxHp);
            Assert.AreEqual(2, b.CurrentHp);
            Assert.AreEqual(2, _events.MaxHpChanged);
        }

        [Test]
        public void MaxHp_DeadUnit_StaysDead()
        {
            var u = NewUnit();
            u.TakeDamage(999, null);

            UnitMaxHpChangedEvent last = default;
            _bus.Subscribe<UnitMaxHpChangedEvent>(e => last = e);

            u.AddModifier(new StatModifier(StatKind.MaxHp, StatOp.Flat, 100f, 3000));
            Assert.AreEqual(150, u.EffectiveStats.MaxHp); // 上限真的变了
            Assert.AreEqual(0, u.CurrentHp);              // 但修饰器不复活
            Assert.AreEqual(150, last.MaxHp);
            Assert.AreEqual(0, last.CurrentHp);            // 事件照发（陈述事实）
        }

        [Test]
        public void MaxHp_Unchanged_NoEvent()
        {
            var u = NewUnit();
            u.AddModifier(new StatModifier(StatKind.AttackPower, StatOp.Flat, 10f, 3000));
            Assert.AreEqual(24, u.EffectiveStats.AttackPower);
            Assert.AreEqual(0, _events.MaxHpChanged); // MaxHp 没动就不许广播
        }

        [Test]
        public void Ledger_IsSymmetric_AddRemove()
        {
            var u = NewUnit();
            var m = new StatModifier(StatKind.Armor, StatOp.Flat, 5f, 3000);

            u.AddModifier(m);
            Assert.AreEqual(35, u.EffectiveStats.Armor);
            Assert.IsTrue(u.RemoveModifier(m));   // 加得上的才摘得掉
            Assert.AreEqual(30, u.EffectiveStats.Armor);
            Assert.IsFalse(u.RemoveModifier(m)); // 账本里已没有第二条
        }

        [Test]
        public void RestoreFullHp_BroadcastsRestored()
        {
            var u = NewUnit();
            u.TakeDamage(20, null);

            UnitRestoredEvent last = default;
            _bus.Subscribe<UnitRestoredEvent>(e => last = e);

            u.RestoreFullHp();
            Assert.AreEqual(50, u.CurrentHp);
            Assert.AreEqual(u.Id, last.UnitId);
            Assert.AreEqual(50, last.CurrentHp);
        }

        [Test]
        public void CanAttack_ChebyshevRange_AndVitalityRules()
        {
            var board = new BoardData(_bus);
            var attacker = new Unit(_nextId++, 1, true, Stats(range: 1), _bus);
            var target = NewUnit(false);
            Assert.IsTrue(board.TryPlace(attacker, new Pos(0, 0)));
            Assert.IsTrue(board.TryPlace(target, new Pos(2, 3))); // 敌方只能进 row 3~5（镜像半场）

            Assert.IsFalse(attacker.CanAttack(target)); // 切比雪夫距离 max(2,3)=3 > 射程 1
            Assert.IsTrue(board.TryMove(target, new Pos(1, 1), mustStayInOwnHalf: false));
            Assert.IsTrue(attacker.CanAttack(target));  // 斜对角：距离 1 = 射程 1

            Assert.IsFalse(attacker.CanAttack(null));   // null → false

            target.TakeDamage(999, null);
            Assert.IsFalse(attacker.CanAttack(target)); // 目标已死 → false

            attacker.TakeDamage(999, null);
            Assert.IsFalse(attacker.CanAttack(target)); // 自己已死 → false
        }
    }
}