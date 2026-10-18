using System.Collections.Generic;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    [TestFixture]
    public class ShopSystemTests
    {
        // 与 ShopPoolTests 同一张测试角色表（两个 1 费 / 一个 2 费 / 一个 3 费，全池 63 份）
        private static CharacterTable TestCharacters => new CharacterTable(new[]
        {
            Def(1, "一费甲", 1),
            Def(2, "一费乙", 1),
            Def(3, "二费甲", 2),
            Def(4, "三费甲", 3),
        });

        private static CharacterDef Def(int id, string name, int cost)
            => new CharacterDef(id, name, cost, ValidStats(), null);

        private static UnitStats ValidStats() => new UnitStats
        {
            MaxHp = 50, AttackPower = 10, Armor = 20, MagicResist = 20, AbilityPower = 100,
            AttackRange = 1, AttackInterval = 1.0f, MoveInterval = 1.0f, CritChance = 5, CritDamage = 50,
        };

        /// <summary>把等级 2~8 全部钉在某一费 100%（测"按费用定价/拒绝"时用，绕开概率随机性）。</summary>
        private static ShopOdds AllInCost(int cost)
        {
            var t = new int[9, 6];
            for (int level = 2; level <= 8; level++) t[level, cost] = 100;
            return new ShopOdds(t);
        }

        private EventBus _bus;
        private Economy _eco;
        private ShopPool _pool;
        private ShopSystem _shop;
        private readonly List<ShopRolledEvent> _rolled = new List<ShopRolledEvent>();
        private readonly List<ShopBoughtEvent> _bought = new List<ShopBoughtEvent>();

        private ShopSystem NewShop(uint seed, int gold, ShopOdds odds = null)
        {
            _rolled.Clear(); _bought.Clear();
            _bus = new EventBus();
            _bus.Subscribe<ShopRolledEvent>(e => _rolled.Add(e));
            _bus.Subscribe<ShopBoughtEvent>(e => _bought.Add(e));
            _eco = new Economy(_bus, gold);
            _pool = new ShopPool(new GameRandom(seed), TestCharacters, odds ?? ShopOdds.Default);
            _shop = new ShopSystem(_pool, _eco, _bus);
            return _shop;
        }

        [Test]
        public void RollOffer_FillsFiveSlots_AtLevel2()
        {
            var shop = NewShop(3, 10);
            shop.RollOffer();
            var offers = shop.Offers;
            Assert.AreEqual(5, offers.Length);            // #21⑥ 货架 5 格
            foreach (var def in offers)
            {
                Assert.AreNotEqual(0, def.Id);            // 满员：L2 一费 100%，池里有货
                Assert.AreEqual(1, def.Cost);
            }
        }

        [Test]
        public void RollOffer_PublishesEvent_SnapshotMatches()
        {
            var shop = NewShop(3, 10);
            shop.RollOffer();
            Assert.AreEqual(1, _rolled.Count);
            var offers = shop.Offers;
            for (int i = 0; i < 5; i++)
                Assert.AreEqual(offers[i].Id, _rolled[0].CharIds[i]);   // 事件快照 = 货架实况
        }

        [Test]
        public void RollOffer_ReservesFromPool()
        {
            var shop = NewShop(3, 10);
            int before = _pool.TotalLeft;                 // 63
            shop.RollOffer();
            Assert.AreEqual(before - 5, _pool.TotalLeft); // 上架即预订：5 份出池锁定
        }

        [Test]
        public void RollOffer_ReturnsUnsold_BoughtNotReturned()
        {
            var shop = NewShop(3, 10);
            int before = _pool.TotalLeft;                 // 63
            shop.RollOffer();                              // 第一次预订 -5 → 58
            var bought = shop.Buy(0);                      // 买走一格：池不动（份数在 roll1 预订时已出池锁定）
            Assert.AreNotEqual(0, bought.Id);
            shop.RollOffer();                              // 未卖 4 格还池 +4、重新预订 -5
            Assert.AreEqual(before - 5 + 4 - 5, _pool.TotalLeft);
            Assert.AreEqual(2, _rolled.Count);
            // 终态 57 = 出厂 63 - 在架 5 - 已拥有 1（账目恒等式；若买走被误还池会是 58，未卖没还池会是 53）
        }

        [Test]
        public void Refresh_NotEnoughGold_PureReject()
        {
            var shop = NewShop(3, 1);
            shop.RollOffer();
            var before = shop.Offers;
            int poolBefore = _pool.TotalLeft;
            Assert.IsFalse(shop.Refresh());               // 只有 1 金 < 2 金（#21⑥）
            Assert.AreEqual(1, _eco.Gold);                // 钱没动
            for (int i = 0; i < 5; i++) Assert.AreEqual(before[i].Id, shop.Offers[i].Id);   // 货架原样
            Assert.AreEqual(poolBefore, _pool.TotalLeft); // 池没动
            Assert.AreEqual(1, _rolled.Count);            // 没有新上架事件
        }

        [Test]
        public void Refresh_Success_Deducts2AndRerolls()
        {
            var shop = NewShop(3, 10);
            shop.RollOffer();
            Assert.IsTrue(shop.Refresh());
            Assert.AreEqual(8, _eco.Gold);                // #21⑥ 刷新 2 金
            Assert.AreEqual(2, _rolled.Count);
        }

        [Test]
        public void Buy_OutOfRangeOrEmpty_ReturnsNone()
        {
            var shop = NewShop(3, 10);
            Assert.AreEqual(0, shop.Buy(-1).Id);          // 格号越界
            Assert.AreEqual(0, shop.Buy(5).Id);           // 格号越界
            Assert.AreEqual(0, shop.Buy(0).Id);           // 空货架（从未上架）
            Assert.AreEqual(10, _eco.Gold);               // 纯拒绝：钱一分没动
            Assert.AreEqual(0, _bought.Count);            // 也不发事件
        }

        [Test]
        public void Buy_Success_DeductsClearsPublishes()
        {
            var shop = NewShop(3, 10);
            shop.RollOffer();
            var def = shop.Buy(0);
            Assert.AreNotEqual(0, def.Id);
            Assert.AreEqual(10 - def.Cost, _eco.Gold);    // 售价 = 费用
            Assert.AreEqual(0, shop.Offers[0].Id);        // 清格
            Assert.AreEqual(1, _bought.Count);
            Assert.AreEqual(0, _bought[0].SlotIndex);
            Assert.AreEqual(def.Id, _bought[0].CharId);
            Assert.AreEqual(10 - def.Cost, _bought[0].GoldLeft);
        }

        [Test]
        public void Buy_InsufficientGold_PureReject_KeepsOffer()
        {
            var shop = NewShop(3, 0);
            shop.RollOffer();                              // 免费上架
            int poolAfterRoll = _pool.TotalLeft;           // 58：份数已出池锁定
            var before = shop.Offers;
            Assert.AreEqual(0, shop.Buy(0).Id);            // 1 费要 1 金，钱包 0
            Assert.AreEqual(0, _eco.Gold);
            for (int i = 0; i < 5; i++) Assert.AreEqual(before[i].Id, shop.Offers[i].Id);   // 货架原样
            Assert.AreEqual(poolAfterRoll, _pool.TotalLeft);   // 池不参与购买：纹丝不动
            Assert.AreEqual(0, _bought.Count);
        }

        [Test]
        public void Buy_CostTier_RespectsPrice()
        {
            var shop = NewShop(3, 1, AllInCost(2));        // 全等级 100% 2 费（绕开概率随机性）
            shop.RollOffer();
            for (int i = 0; i < 5; i++) Assert.AreEqual(2, shop.Offers[i].Cost);
            Assert.AreEqual(0, shop.Buy(0).Id);            // 2 费要 2 金，只有 1 金 → 纯拒绝
            Assert.AreEqual(1, _eco.Gold);
            for (int i = 0; i < 5; i++) Assert.AreNotEqual(0, shop.Offers[i].Id);   // 货架原样
            _eco.AddGold(1);                                // 补到 2 金
            var def = shop.Buy(0);
            Assert.AreEqual(2, def.Cost);
            Assert.AreEqual(0, _eco.Gold);                 // 2 金全花掉
            Assert.AreEqual(0, shop.Offers[0].Id);         // 清格
        }

        [Test]
        public void Offers_DefensiveCopy()
        {
            var shop = NewShop(3, 10);
            shop.RollOffer();
            var snapshot = shop.Offers;
            snapshot[0] = default;                          // 改的是拷贝
            Assert.AreNotEqual(0, shop.Offers[0].Id);      // 货架本体纹丝不动
        }

        [Test]
        public void RollOffer_SameSeed_SameSequence()
        {
            var a = NewShop(42, 99);
            var b = NewShop(42, 99);
            for (int round = 0; round < 3; round++)
            {
                a.RollOffer(); b.RollOffer();
                for (int i = 0; i < 5; i++)
                    Assert.AreEqual(a.Offers[i].Id, b.Offers[i].Id, $"第 {round + 1} 刷第 {i + 1} 格不同——同种子必须同货架");
                a.Buy(0); b.Buy(0);   // 同样的购买动作，保持两侧状态同构
            }
        }
    }
}
