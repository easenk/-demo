using System.Collections.Generic;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    /// <summary>
    /// 批次5.5：战斗循环的回蓝记账（命中回蓝 + 每 tick 回蓝）。
    /// 施法打断循环归阶段3——现在蓝满了就停在满，不会自己消失。
    /// </summary>
    [TestFixture]
    public class CombatManaTests
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

        private Unit Place(bool playerSide, Pos cell, UnitStats stats)
        {
            var u = new Unit(_nextId++, 1, playerSide, stats, _bus);
            Assert.IsTrue(_board.TryPlace(u, cell), $"预备放置失败：{(playerSide ? "玩家" : "敌方")} @ {cell}");
            return u;
        }

        private static UnitStats Stats(int maxHp = 50, int attack = 14, int armor = 0, int range = 1,
            float atkInterval = 1.0f, float moveInterval = 1.0f,
            int maxMana = 0, int manaPerHit = 0, int regenPerSecond = 0)
        {
            UnitStats s = DefFactory.SampleStats;
            s.MaxHp = maxHp;
            s.AttackPower = attack;
            s.Armor = armor;
            s.MagicResist = 0;
            s.AttackRange = range;
            s.AttackInterval = atkInterval;
            s.MoveInterval = moveInterval;
            s.CritChance = 0; // 默认无暴击：时间线纯手推
            s.CritDamage = 50;
            s.MaxMana = maxMana;
            s.ManaPerHit = manaPerHit;
            s.ManaRegenPerSecond = regenPerSecond;
            return s;
        }

        [Test]
        public void AttackHit_GrantsManaPerHit_DefenderGainsNothing()
        {
            // 坦克口径：命中回 5 蓝；靶子无蓝条
            var tank = Place(true, new Pos(2, 2), Stats(maxMana: 50, manaPerHit: 5, atkInterval: 0.5f));
            var bag = Place(false, new Pos(2, 3), Stats(maxHp: 1000000, atkInterval: 999f, moveInterval: 999f));

            var manaEvents = new List<ManaChangedEvent>();
            _bus.Subscribe<ManaChangedEvent>(e => manaEvents.Add(e));
            var combat = new CombatSystem(_board, _bus);

            combat.Tick();
            combat.Tick();
            combat.Tick();

            Assert.AreEqual(15, tank.CurrentMana); // 3 次命中 × 5
            Assert.AreEqual(0, bag.CurrentMana);    // 受击不回蓝（决策⑥：暂不做）
            Assert.AreEqual(3, manaEvents.Count);
            Assert.AreEqual(5,  manaEvents[0].CurrentMana);
            Assert.AreEqual(10, manaEvents[1].CurrentMana);
            Assert.AreEqual(15, manaEvents[2].CurrentMana);
        }

        [Test]
        public void RegenPerTick_FillsMana_EvenWhenOutOfReach()
        {
            // 术士的被动部分：每秒 2 蓝 → 每 tick 1 蓝；够不着敌人（走不动）也照回
            var warlock = Place(true, new Pos(0, 2), Stats(maxMana: 50, regenPerSecond: 2, moveInterval: 999f));
            var idle = Place(true, new Pos(1, 2), Stats(maxMana: 50)); // 对照组：无被动回蓝
            Place(false, new Pos(0, 5), Stats(maxHp: 1000000, atkInterval: 999f, moveInterval: 999f));

            var manaEvents = new List<ManaChangedEvent>();
            _bus.Subscribe<ManaChangedEvent>(e => manaEvents.Add(e));
            var combat = new CombatSystem(_board, _bus);

            for (int i = 0; i < 5; i++) combat.Tick();

            Assert.AreEqual(5, warlock.CurrentMana); // 1/tick × 5
            Assert.AreEqual(0, idle.CurrentMana);    // 对照组纹丝不动
            Assert.AreEqual(5, manaEvents.Count);
            foreach (var e in manaEvents)
                Assert.AreEqual(warlock.Id, e.UnitId); // 只有术士在回蓝
            Assert.AreEqual(1, manaEvents[0].CurrentMana);
            Assert.AreEqual(5, manaEvents[4].CurrentMana);
        }

        [Test]
        public void Warlock_HitAndRegen_Stack()
        {
            // 术士全口径：命中回 7 + 每秒回 2（每 tick 1），贴脸每 tick 出手
            var warlock = Place(true, new Pos(2, 2),
                Stats(maxMana: 50, manaPerHit: 7, regenPerSecond: 2, atkInterval: 0.5f));
            Place(false, new Pos(2, 3), Stats(maxHp: 1000000, atkInterval: 999f, moveInterval: 999f));

            var manaEvents = new List<ManaChangedEvent>();
            _bus.Subscribe<ManaChangedEvent>(e => manaEvents.Add(e));
            var combat = new CombatSystem(_board, _bus);

            combat.Tick(); // t1：命中 +7 → 事件；tick 末被动 +1 → 事件
            combat.Tick();
            combat.Tick();

            // 每 tick 两次记账（先命中后被动）：7,8 / 15,16 / 23,24
            Assert.AreEqual(24, warlock.CurrentMana);
            CollectionAssert.AreEqual(new[] { 7, 8, 15, 16, 23, 24 },
                manaEvents.ConvertAll(e => e.CurrentMana));
        }

        [Test]
        public void ManaStopsAtCap_NoOverflowEvents()
        {
            // 上限 20、每击 +10：t1 到 10、t2 到 20 封顶、t3 满蓝静默
            //（阶段3 施法上线前，蓝满了就停在满——不会自己消失）
            var u = Place(true, new Pos(2, 2), Stats(maxMana: 20, manaPerHit: 10, atkInterval: 0.5f));
            Place(false, new Pos(2, 3), Stats(maxHp: 1000000, atkInterval: 999f, moveInterval: 999f));

            var manaEvents = new List<ManaChangedEvent>();
            _bus.Subscribe<ManaChangedEvent>(e => manaEvents.Add(e));
            var combat = new CombatSystem(_board, _bus);

            combat.Tick();
            combat.Tick();
            combat.Tick();

            Assert.AreEqual(20, u.CurrentMana);
            Assert.AreEqual(2, manaEvents.Count); // 第三击满蓝：不重复陈述
        }
    }
}
