using System.Collections.Generic;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    // ─────────────────────────────────────────────────────────────
    // 时间线换算说明（步长 1/30 秒 = 每秒30步，批次5.6）：
    //  · 命中回蓝走 AddMana(int)——整数值精确
    //  · 被动回蓝走 RegenMana 整数点数账本——每秒回2蓝 = 精确每15步回1蓝，可钉死步数断言
    // ─────────────────────────────────────────────────────────────
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
            for (int i = 0; i < 50; i++) combat.Tick(); // 攻击间隔0.5秒=15~16步一刀，50步稳出3刀

            Assert.AreEqual(15, tank.CurrentMana); // 3 次命中 × 5
            Assert.AreEqual(0, bag.CurrentMana);    // 受击不回蓝（决策⑥：暂不做）
            Assert.AreEqual(3, manaEvents.Count);
            Assert.AreEqual(5,  manaEvents[0].CurrentMana);
            Assert.AreEqual(10, manaEvents[1].CurrentMana);
            Assert.AreEqual(15, manaEvents[2].CurrentMana);
        }

        [Test]
        public void RegenMana_GrantsOneManaPer15Ticks_Exactly()
        {
            // 术士的被动部分：每秒回 2 蓝 = 整数点数账本精确地每15步回 1 蓝（无浮点抖动）
            // 术士够不着敌人（走不动）也照回
            var warlock = Place(true, new Pos(0, 2), Stats(maxMana: 50, regenPerSecond: 2, moveInterval: 999f));
            var idle = Place(true, new Pos(1, 2), Stats(maxMana: 50)); // 对照组：无被动回蓝
            Place(false, new Pos(0, 5), Stats(maxHp: 1000000, atkInterval: 999f, moveInterval: 999f));

            var manaEvents = new List<ManaChangedEvent>();
            _bus.Subscribe<ManaChangedEvent>(e => manaEvents.Add(e));
            var combat = new CombatSystem(_board, _bus);

            for (int i = 0; i < 75; i++) combat.Tick(); // 75步 = 5×15 → 精确回 5 蓝

            Assert.AreEqual(5, warlock.CurrentMana);
            Assert.AreEqual(0, idle.CurrentMana);    // 对照组纹丝不动
            Assert.AreEqual(5, manaEvents.Count);
            foreach (var e in manaEvents)
                Assert.AreEqual(warlock.Id, e.UnitId); // 只有术士在回蓝
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 },
                manaEvents.ConvertAll(e => e.CurrentMana));
        }

        [Test]
        public void Warlock_HitAndRegen_Stack()
        {
            // 术士全口径：命中回 7 + 每秒回 2（整数账本每15步回1），贴脸出手（15~16步一刀）
            var warlock = Place(true, new Pos(2, 2),
                Stats(maxMana: 50, manaPerHit: 7, regenPerSecond: 2, atkInterval: 0.5f));
            Place(false, new Pos(2, 3), Stats(maxHp: 1000000, atkInterval: 999f, moveInterval: 999f));

            var manaEvents = new List<ManaChangedEvent>();
            _bus.Subscribe<ManaChangedEvent>(e => manaEvents.Add(e));
            var combat = new CombatSystem(_board, _bus);

            for (int i = 0; i < 70; i++) combat.Tick();

            // 第70步盘点：命中 4 次（攻击条15步与16步两种浮点口径下第70步前都是4刀）×7 = 28，
            // 被动回蓝在第30/60步各+1 = 2 → 合计 30（两条口径殊途同归，断言稳定）
            Assert.AreEqual(30, warlock.CurrentMana);
            Assert.AreEqual(6, manaEvents.Count); // 4次命中回蓝 + 2次被动回蓝
        }

        [Test]
        public void ManaStopsAtCap_NoOverflowEvents()
        {
            // 上限 20、每击 +10：第15~16步到 10、第30~31步到 20 封顶、第45~46步满蓝静默
            //（阶段3 施法上线前，蓝满了就停在满——不会自己消失）
            var u = Place(true, new Pos(2, 2), Stats(maxMana: 20, manaPerHit: 10, atkInterval: 0.5f));
            Place(false, new Pos(2, 3), Stats(maxHp: 1000000, atkInterval: 999f, moveInterval: 999f));

            var manaEvents = new List<ManaChangedEvent>();
            _bus.Subscribe<ManaChangedEvent>(e => manaEvents.Add(e));
            var combat = new CombatSystem(_board, _bus);

            for (int i = 0; i < 50; i++) combat.Tick();

            Assert.AreEqual(20, u.CurrentMana);
            Assert.AreEqual(2, manaEvents.Count); // 第三刀满蓝：不重复陈述
        }
    }
}
