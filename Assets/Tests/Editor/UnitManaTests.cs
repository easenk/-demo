using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    /// <summary>
    /// 批次5.5：蓝条基建（Unit 侧契约）。战斗侧回蓝曲线在 CombatManaTests。
    /// AddMana 是回蓝唯一入口，契约为 Heal 的镜像：钳上限、无变化不发事件。
    /// </summary>
    [TestFixture]
    public class UnitManaTests
    {
        private EventBus _bus;
        private int _nextId;

        [SetUp]
        public void SetUp()
        {
            _bus = new EventBus();
            _nextId = 1;
        }

        private Unit NewUnit(int maxMana = 0, int manaPerHit = 0, int regenPerSecond = 0)
        {
            UnitStats s = DefFactory.SampleStats;
            s.MaxMana = maxMana;
            s.ManaPerHit = manaPerHit;
            s.ManaRegenPerSecond = regenPerSecond;
            return new Unit(_nextId++, 1, true, s, _bus);
        }

        [Test]
        public void NewUnit_StartsWithZeroMana()
        {
            // 决策③：起手蓝 0——"攻击积攒"从空起步
            Assert.AreEqual(0, NewUnit(maxMana: 50).CurrentMana);
            Assert.AreEqual(0, NewUnit().CurrentMana); // 无蓝条单位也是 0
        }

        [Test]
        public void AddMana_Accumulates_AndBroadcasts()
        {
            var u = NewUnit(maxMana: 50);
            ManaChangedEvent last = default;
            int received = 0;
            _bus.Subscribe<ManaChangedEvent>(e => { last = e; received++; });

            u.AddMana(10);

            Assert.AreEqual(10, u.CurrentMana);
            Assert.AreEqual(1, received);
            Assert.AreEqual(u.Id, last.UnitId);
            Assert.AreEqual(10, last.CurrentMana);
            Assert.AreEqual(50, last.MaxMana); // 事件自带上限：蓝条 UI 不用回头查
        }

        [Test]
        public void AddMana_ClampsAtMax()
        {
            var u = NewUnit(maxMana: 50);
            u.AddMana(30);
            u.AddMana(30); // 第二次只进 20：钳进上限

            Assert.AreEqual(50, u.CurrentMana);
        }

        [Test]
        public void AddMana_WithoutBar_IsSilent()
        {
            // MaxMana=0 = 无蓝条单位（"3费以上才有技能"数据纪律的代码侧）：连账本都不建
            var u = NewUnit(maxMana: 0, manaPerHit: 10);
            int received = 0;
            _bus.Subscribe<ManaChangedEvent>(e => received++);

            u.AddMana(10);

            Assert.AreEqual(0, u.CurrentMana);
            Assert.AreEqual(0, received);
        }

        [Test]
        public void AddMana_NonPositive_IsSilent()
        {
            var u = NewUnit(maxMana: 50);
            int received = 0;
            _bus.Subscribe<ManaChangedEvent>(e => received++);

            u.AddMana(0);
            u.AddMana(-5);

            Assert.AreEqual(0, u.CurrentMana);
            Assert.AreEqual(0, received);
        }

        [Test]
        public void AddMana_WhenFull_IsSilent()
        {
            var u = NewUnit(maxMana: 50);
            u.AddMana(30);
            u.AddMana(30);
            int received = 0;
            _bus.Subscribe<ManaChangedEvent>(e => received++);

            u.AddMana(10); // 已满：不重复陈述

            Assert.AreEqual(50, u.CurrentMana);
            Assert.AreEqual(0, received);
        }

        [Test]
        public void EffectiveStats_ManaDims_ClampAtZero()
        {
            var u = NewUnit(maxMana: 100, manaPerHit: 5, regenPerSecond: 3);
            u.AddModifier(new StatModifier(StatKind.MaxMana,            StatOp.Flat, -999f, ModSourceId.TestBase));
            u.AddModifier(new StatModifier(StatKind.ManaPerHit,         StatOp.Flat, -10f, ModSourceId.TestBase));
            u.AddModifier(new StatModifier(StatKind.ManaRegenPerSecond, StatOp.Flat, -5f, ModSourceId.TestBase));

            // 蓝条三维全部 ≥ 0（回蓝不许为负——负回蓝 debuff 有了真需求再谈）
            Assert.AreEqual(0, u.EffectiveStats.MaxMana);
            Assert.AreEqual(0, u.EffectiveStats.ManaPerHit);
            Assert.AreEqual(0, u.EffectiveStats.ManaRegenPerSecond);
        }
    }
}
