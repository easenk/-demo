using System;
using System.Collections.Generic;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    [TestFixture]
    public class EconomyTests
    {
        // ── 事件录制器：每条用例自己 new 总线自己订阅（纪律：测试互不污染）──
        private readonly List<GoldChangedEvent> _gold = new List<GoldChangedEvent>();
        private readonly List<LevelUpEvent> _levelUps = new List<LevelUpEvent>();
        private readonly List<XpChangedEvent> _xp = new List<XpChangedEvent>();
        private readonly List<IncomeSettledEvent> _income = new List<IncomeSettledEvent>();
        private readonly List<string> _order = new List<string>();   // 跨类型事件顺序（时间线契约）

        private Economy NewEco(int startGold)
        {
            _gold.Clear(); _levelUps.Clear(); _xp.Clear(); _income.Clear(); _order.Clear();
            var bus = new EventBus();
            bus.Subscribe<GoldChangedEvent>(e => { _gold.Add(e); _order.Add("gold"); });
            bus.Subscribe<LevelUpEvent>(e => { _levelUps.Add(e); _order.Add("levelup"); });
            bus.Subscribe<XpChangedEvent>(e => { _xp.Add(e); _order.Add("xp"); });
            bus.Subscribe<IncomeSettledEvent>(e => { _income.Add(e); _order.Add("income"); });
            return new Economy(bus, startGold);
        }

        /// <summary>推到满级 8（L8 累计门槛恰好 84，#21⑤ 修订版），并清掉升级路上的历史事件——只看后续动作。</summary>
        private Economy MaxLevelEco(int gold)
        {
            var eco = NewEco(gold);
            eco.AddXp(84);
            _gold.Clear(); _levelUps.Clear(); _xp.Clear(); _income.Clear(); _order.Clear();
            return eco;
        }

        // ── 构造与派生 ──

        [Test]
        public void Ctor_NegativeStartGold_Throws()
        {
            // 钱包不变量从出生就守：负开局金 = 程序员错误，静默接受会污染"Gold 恒 ≥ 0"
            Assert.Throws<ArgumentOutOfRangeException>(() => NewEco(-1));
        }

        [Test]
        public void InitialState_WalletLevelXp()
        {
            var eco = NewEco(Economy.DefaultStartGold);
            Assert.AreEqual(3, eco.Gold);            // #21④ 起始金币 3
            Assert.AreEqual(2, eco.Level);           // 开局 2 级 = 上场位 2 起步
            Assert.AreEqual(0, eco.XpTotal);
            Assert.AreEqual(0, eco.XpProgress);
            Assert.AreEqual(4, eco.XpToNextLevel);   // L3 = 4 累计经验（#21⑤ 修订版）
            Assert.AreEqual(0, eco.WinStreak);
        }

        [Test]
        public void Interest_Ladder()
        {
            var eco = NewEco(0);
            Assert.AreEqual(0, eco.Interest);        // 0 金 → 0 利息
            eco.AddGold(9);
            Assert.AreEqual(0, eco.Interest);        // 9 金 → 还差 1
            eco.AddGold(1);
            Assert.AreEqual(1, eco.Interest);        // 10 金 → +1
            eco.AddGold(40);
            Assert.AreEqual(5, eco.Interest);        // 50 金 → 5（恰好到顶，银行 50 封顶）
            eco.AddGold(10);
            Assert.AreEqual(5, eco.Interest);        // 60 金 → 封顶 5，存钱流天花板
        }

        // ── TrySpend / AddGold / SpendClamped ──

        [Test]
        public void TrySpend_NonPositive_PureReject()
        {
            var eco = NewEco(5);
            Assert.IsFalse(eco.TrySpend(0));
            Assert.IsFalse(eco.TrySpend(-3));
            Assert.AreEqual(5, eco.Gold);
            Assert.AreEqual(0, _gold.Count);         // 纯拒绝：不扣钱、不发事件
        }

        [Test]
        public void TrySpend_Insufficient_PureReject()
        {
            var eco = NewEco(5);
            Assert.IsFalse(eco.TrySpend(6));
            Assert.AreEqual(5, eco.Gold);
            Assert.AreEqual(0, _gold.Count);
        }

        [Test]
        public void TrySpend_Success_PublishesGoldChanged()
        {
            var eco = NewEco(10);
            Assert.IsTrue(eco.TrySpend(4));
            Assert.AreEqual(6, eco.Gold);
            Assert.AreEqual(1, _gold.Count);
            Assert.AreEqual(6, _gold[0].Gold);       // 事件带的是"新余额"，UI 不用再算
            Assert.AreEqual(-4, _gold[0].Delta);
        }

        [Test]
        public void AddGold_NonPositive_Silent()
        {
            var eco = NewEco(5);
            eco.AddGold(0);
            eco.AddGold(-5);
            Assert.AreEqual(5, eco.Gold);
            Assert.AreEqual(0, _gold.Count);         // ≤0 的收入是配置错误：不进账本不发事件
        }

        [Test]
        public void AddGold_Success_PublishesGoldChanged()
        {
            var eco = NewEco(2);
            eco.AddGold(3);
            Assert.AreEqual(5, eco.Gold);
            Assert.AreEqual(1, _gold.Count);
            Assert.AreEqual(5, _gold[0].Gold);
            Assert.AreEqual(3, _gold[0].Delta);
        }

        [Test]
        public void SpendClamped_WithinBalance_SpendsFull()
        {
            var eco = NewEco(10);
            Assert.AreEqual(4, eco.SpendClamped(4)); // 返回实扣
            Assert.AreEqual(6, eco.Gold);
            Assert.AreEqual(1, _gold.Count);
            Assert.AreEqual(6, _gold[0].Gold);
            Assert.AreEqual(-4, _gold[0].Delta);
        }

        [Test]
        public void SpendClamped_OverBalance_ClampsToZeroAndReportsActual()
        {
            var eco = NewEco(3);
            Assert.AreEqual(3, eco.SpendClamped(5)); // 只有 3：扣到 0 为止，不产生负债
            Assert.AreEqual(0, eco.Gold);
            Assert.AreEqual(1, _gold.Count);
            Assert.AreEqual(0, _gold[0].Gold);
            Assert.AreEqual(-3, _gold[0].Delta);     // Delta 是实扣而非请求额——账实相符
        }

        [Test]
        public void SpendClamped_NonPositiveOrEmptyWallet_SilentZero()
        {
            var eco = NewEco(10);
            Assert.AreEqual(0, eco.SpendClamped(0));
            Assert.AreEqual(0, eco.SpendClamped(-2));
            Assert.AreEqual(10, eco.Gold);
            Assert.AreEqual(0, _gold.Count);         // 没扣成 = 没有事实 = 静默

            var broke = NewEco(0);
            Assert.AreEqual(0, broke.SpendClamped(5)); // 空钱包：返回 0，不广播
            Assert.AreEqual(0, broke.Gold);
        }

        // ── BuyXp（4 金 = 4 XP，不限购）──

        [Test]
        public void BuyXp_FirstBuy_ReachesLevel3_EventsInOrder()
        {
            var eco = NewEco(10);
            Assert.IsTrue(eco.BuyXp());              // 4 XP 恰好压线 L3 门槛（累计 4）→ 升级
            Assert.AreEqual(6, eco.Gold);
            Assert.AreEqual(3, eco.Level);
            Assert.AreEqual(0, eco.XpProgress);      // 恰好压线：级内余量 0
            Assert.AreEqual(8, eco.XpToNextLevel);   // L4 门槛 12 - 累计 4
            CollectionAssert.AreEqual(new[] { "gold", "levelup", "xp" }, _order);   // 事件序契约
        }

        [Test]
        public void BuyXp_SecondBuy_NoLevelUp()
        {
            var eco = NewEco(10);
            eco.BuyXp();                              // → L3，累计 4
            _gold.Clear(); _levelUps.Clear(); _xp.Clear(); _order.Clear();

            Assert.IsTrue(eco.BuyXp());              // +4 = 累计 8 < 12（L4）→ 不升级
            Assert.AreEqual(2, eco.Gold);
            Assert.AreEqual(3, eco.Level);
            Assert.AreEqual(4, eco.XpProgress);      // 8 - 4
            Assert.AreEqual(4, eco.XpToNextLevel);   // 12 - 8
            Assert.AreEqual(0, _levelUps.Count);
            CollectionAssert.AreEqual(new[] { "gold", "xp" }, _order);
        }

        [Test]
        public void BuyXp_NotEnoughGold_PureReject()
        {
            var eco = NewEco(3);
            Assert.IsFalse(eco.BuyXp());
            Assert.AreEqual(3, eco.Gold);
            Assert.AreEqual(0, _gold.Count);
            Assert.AreEqual(0, _levelUps.Count);
        }

        [Test]
        public void BuyXp_AtMaxLevel_RejectsBeforeSpending()
        {
            var eco = MaxLevelEco(10);
            Assert.IsFalse(eco.BuyXp());
            Assert.AreEqual(10, eco.Gold);            // 满级检查先于扣钱：白买不收钱
            Assert.AreEqual(0, _gold.Count);
        }

        // ── AddXp（累计门槛制）──

        [Test]
        public void AddXp_LevelUpCarriesRemainder()
        {
            var eco = NewEco(0);
            eco.AddXp(5);                             // 5 ≥ 4 → L3，余 1 滚入下一级
            Assert.AreEqual(3, eco.Level);
            Assert.AreEqual(5, eco.XpTotal);
            Assert.AreEqual(1, eco.XpProgress);      // 5 - 4
            Assert.AreEqual(7, eco.XpToNextLevel);   // 12 - 5
        }

        [Test]
        public void AddXp_MultiLevelUp_AllLevelUpsBeforeFinalXp()
        {
            var eco = NewEco(0);
            eco.AddXp(30);                            // L3(4)→L4(12)→L5(22)，余 8 < 36 停
            Assert.AreEqual(5, eco.Level);
            Assert.AreEqual(30, eco.XpTotal);
            Assert.AreEqual(8, eco.XpProgress);      // 30 - 22
            Assert.AreEqual(6, eco.XpToNextLevel);   // 36 - 30

            Assert.AreEqual(3, _levelUps.Count);      // 连升 3 级逐级各发一条
            Assert.AreEqual(3, _levelUps[0].Level);
            Assert.AreEqual(4, _levelUps[1].Level);
            Assert.AreEqual(5, _levelUps[2].Level);
            Assert.AreEqual(1, _xp.Count);            // 最终经验条状态只发一次
            CollectionAssert.AreEqual(
                new[] { "levelup", "levelup", "levelup", "xp" }, _order);   // 升级全在前
        }

        [Test]
        public void AddXp_ExactMaxLevel_ZeroRemainder()
        {
            var eco = NewEco(0);
            eco.AddXp(84);                            // L8 恰好压线（#21⑤ 修订版满级全程数学的地基）
            Assert.AreEqual(8, eco.Level);
            Assert.AreEqual(84, eco.XpTotal);
            Assert.AreEqual(0, eco.XpProgress);
            Assert.AreEqual(0, eco.XpToNextLevel);
            Assert.AreEqual(6, _levelUps.Count);      // L3~L8 共 6 级
        }

        [Test]
        public void AddXp_AtMaxLevel_DiscardsSilently()
        {
            var eco = MaxLevelEco(0);
            int totalBefore = eco.XpTotal;
            eco.AddXp(10);
            Assert.AreEqual(8, eco.Level);            // 满级恒 8
            Assert.AreEqual(totalBefore, eco.XpTotal); // 丢弃不动累计账本
            Assert.AreEqual(0, _levelUps.Count);
            Assert.AreEqual(0, _xp.Count);            // 丢弃不产生事实：静默
        }

        [Test]
        public void AddXp_NonPositive_Silent()
        {
            var eco = NewEco(0);
            eco.AddXp(0);
            eco.AddXp(-1);
            Assert.AreEqual(2, eco.Level);
            Assert.AreEqual(0, eco.XpTotal);
            Assert.AreEqual(0, _xp.Count);
        }

        [Test]
        public void XpToNextLevel_AtMaxLevel_Zero()
        {
            var eco = MaxLevelEco(0);
            Assert.AreEqual(0, eco.XpToNextLevel);   // 满级：经验条恒"满"
        }

        // ── SettleBattleIncome（三笔分账 + 连胜 + 被动经验）──

        [Test]
        public void StreakBonus_LadderViaSettlements()
        {
            var eco = NewEco(0);
            // #21③ 连胜阶梯：1 连 0 → 2 连 1 → 3 连 2 → 4 连 3 → 5 连 3（封顶）
            Assert.AreEqual(0, eco.SettleBattleIncome(true, 0).StreakGold);
            Assert.AreEqual(1, eco.SettleBattleIncome(true, 0).StreakGold);
            Assert.AreEqual(2, eco.SettleBattleIncome(true, 0).StreakGold);
            Assert.AreEqual(3, eco.SettleBattleIncome(true, 0).StreakGold);
            Assert.AreEqual(3, eco.SettleBattleIncome(true, 0).StreakGold);   // 4 连及以上封顶 3
            Assert.AreEqual(5, eco.WinStreak);
        }

        [Test]
        public void SettleBattleIncome_Loss_ResetsStreakAndSkipsStreakGold()
        {
            var eco = NewEco(0);
            eco.SettleBattleIncome(true, 0);
            eco.SettleBattleIncome(true, 0);                    // 2 连
            var bill = eco.SettleBattleIncome(false, 0);        // 败场：清零、无连胜笔
            Assert.AreEqual(0, bill.StreakGold);
            Assert.AreEqual(0, eco.WinStreak);
            Assert.AreEqual(0, eco.SettleBattleIncome(true, 0).StreakGold);   // 重新 1 连：又是 0
        }

        [Test]
        public void SettleBattleIncome_EliteClears_RaiseFixedSegment()
        {
            var eco = NewEco(0);
            // #21① 固定段位三段：R1~R6 每场 +5；通 R6 后 +6；通 R11 后 +7
            Assert.AreEqual(5, eco.SettleBattleIncome(true, 0).FixedGold);
            Assert.AreEqual(6, eco.SettleBattleIncome(true, 1).FixedGold);
            Assert.AreEqual(7, eco.SettleBattleIncome(true, 2).FixedGold);
        }

        [Test]
        public void SettleBattleIncome_InvalidEliteClears_Throws()
        {
            var eco = NewEco(0);
            Assert.Throws<ArgumentOutOfRangeException>(() => eco.SettleBattleIncome(true, 3));
            Assert.Throws<ArgumentOutOfRangeException>(() => eco.SettleBattleIncome(true, -1));
        }

        [Test]
        public void SettleBattleIncome_InterestFromPreIncomeBalance()
        {
            var eco = NewEco(20);
            var bill = eco.SettleBattleIncome(false, 0);
            Assert.AreEqual(2, bill.InterestGold);   // 按结算前余额 20 算（不是按入账后的 27）
            Assert.AreEqual(7, bill.TotalGold);      // 5 + 2 + 0（败场无连胜笔）
            Assert.AreEqual(27, eco.Gold);
        }

        [Test]
        public void SettleBattleIncome_GrantsPassiveXp()
        {
            var eco = NewEco(0);
            eco.SettleBattleIncome(true, 0);
            Assert.AreEqual(Economy.PassiveXpPerBattle, eco.XpTotal);   // #21⑤ 每场战斗结束 +2
            Assert.AreEqual(2, eco.XpProgress);

            eco.SettleBattleIncome(true, 0);
            Assert.AreEqual(4, eco.XpTotal);         // 两场 4 → 恰好 L3（L3 门槛 4 不变）
            Assert.AreEqual(3, eco.Level);
        }

        [Test]
        public void SettleBattleIncome_EventOrder_IncomeGoldLevelUpXp()
        {
            var eco = NewEco(0);

            eco.SettleBattleIncome(true, 0);          // 第一场：2 XP 不升级
            CollectionAssert.AreEqual(new[] { "income", "gold", "xp" }, _order);
            Assert.AreEqual(1, _income.Count);
            Assert.AreEqual(5, _income[0].Bill.FixedGold);
            Assert.AreEqual(0, _income[0].Bill.InterestGold);
            Assert.AreEqual(1, _income[0].Bill.WinStreak);

            _gold.Clear(); _levelUps.Clear(); _xp.Clear(); _income.Clear(); _order.Clear();

            eco.SettleBattleIncome(true, 0);          // 第二场：累计 4 → L3，升级夹在中间
            CollectionAssert.AreEqual(new[] { "income", "gold", "levelup", "xp" }, _order);
            Assert.AreEqual(2, _income[0].Bill.WinStreak);
        }
    }
}
