using System;

namespace AutoChess.Game
{
    /// <summary>
    /// 商店货架：5 格上架 + 买卡 + 刷新（决策 #21⑥：货架 5 格、刷新 2 金/次）。
    ///
    /// 预订模型（上架即出池）——参考工程修复过"幻影副本"bug 后的货架生命周期，直接继承：
    ///   RollOffer：先把上一批没卖掉的卡还回池 → 再逐格上架，上架那一刻就 TakeOut（预订）。
    ///     同一刷的后续格子看到的是扣除后的余量 → 最后一份最多在架上出现一次，货架永不说谎。
    ///   Buy：只剩"钱"这一关——份数在上架时已锁死，"旧出价失效"的边角整个不存在，
    ///     钱不够时的还池补偿代码也随之删除——好的修法是删掉边界情况，不是守着它。
    ///   未卖掉的在下一次 RollOffer 归还；买走的份数永不归还（卖出回池走 ShopPool.Return，由 Session 批次5 调）。
    ///
    /// 账目恒等式：pool.TotalLeft = 出厂总量 - 玩家已拥有 - 当前在架份数。
    ///
    /// 职责边界：钱/等级归 Economy，池归 ShopPool，买入后的"角色落地"（建 Unit、入名册、
    /// 合成检查、羁绊重算）归 GameSession（批次5 接线）——本类只返回 CharacterDef。
    /// 拒绝路径纪律：枚举全部拒绝条件 → 纯拒绝（钱/池/货架/事件四不动）。
    /// </summary>
    public sealed class ShopSystem
    {
        public const int Slots = 5;           // #21⑥ 货架 5 格
        public const int RefreshCost = 2;     // #21⑥ 手动刷新 2 金/次

        private readonly ShopPool _pool;
        private readonly Economy _eco;
        private readonly EventBus _bus;
        private readonly CharacterDef[] _offers = new CharacterDef[Slots];   // Id=0 表示空格

        public ShopSystem(ShopPool pool, Economy eco, EventBus bus)
        {
            _pool = pool ?? throw new ArgumentNullException(nameof(pool));
            _eco = eco ?? throw new ArgumentNullException(nameof(eco));
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        }

        /// <summary>当前货架快照（防御性拷贝——外部拿到也不能改货架；Id=0 = 空格）。</summary>
        public CharacterDef[] Offers => (CharacterDef[])_offers.Clone();

        /// <summary>
        /// 免费上架：未售旧卡还池 → 重新预订 5 格（备战期开始时由 GameSession 调用）。
        /// 手动刷新走 Refresh（2 金）——免费与付费只差一笔钱，货架流程是同一条。
        /// </summary>
        public void RollOffer()
        {
            // 1) 旧货下架还池：买走的格早已被 Buy 清成空——只有没卖掉的会还
            for (int i = 0; i < Slots; i++)
            {
                if (_offers[i].Id != 0)
                    _pool.Return(_offers[i].Id);
                _offers[i] = default;
            }

            // 2) 新货上架：出价即预订——立刻 TakeOut，同刷的后续格看到的是扣完的余量
            for (int i = 0; i < Slots; i++)
            {
                var def = _pool.Roll(_eco.Level);
                if (def.Id != 0 && _pool.TakeOut(def.Id))
                    _offers[i] = def;
                // Roll 返回空（该等级可开档全空）或 TakeOut 失败（单线程下不该发生，防御）→ 空格
            }

            // 3) 广播货架快照（0 = 空格）
            var snapshot = new int[Slots];
            for (int i = 0; i < Slots; i++) snapshot[i] = _offers[i].Id;
            _bus.Publish(new ShopRolledEvent(snapshot));
        }

        /// <summary>
        /// 刷新货架：2 金 = 花钱把上架流程再跑一遍（#21⑥）。
        /// 钱不够 = 纯拒绝：货架原样保留（先扣钱再动货架的顺序保证这一点）。
        /// </summary>
        public bool Refresh()
        {
            if (!_eco.TrySpend(RefreshCost)) return false;   // 纯拒绝：钱没扣成，货架一个指头都不碰
            RollOffer();
            return true;
        }

        /// <summary>
        /// 买走一格。拒绝条件只剩两类（枚举完全）：
        ///   1. 格号越界 / 该格是空的（从未上架，或已买走）；
        ///   2. 钱不够（TrySpend 纯拒绝）。
        /// 份数在上架时已出池锁定——池不参与购买。
        /// 成功：清格 + 广播 ShopBoughtEvent，返回角色定义（调用方决定怎么落地：批次5 入名册）。
        /// </summary>
        public CharacterDef Buy(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= Slots) return CharacterDef.None;
            if (_offers[slotIndex].Id == 0) return CharacterDef.None;

            var def = _offers[slotIndex];
            if (!_eco.TrySpend(def.Cost)) return CharacterDef.None;   // 买只剩钱这一关

            _offers[slotIndex] = default;
            _bus.Publish(new ShopBoughtEvent(slotIndex, def.Id, _eco.Gold));
            return def;
        }
    }
}
