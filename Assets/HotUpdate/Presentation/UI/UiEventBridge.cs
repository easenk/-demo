using System;
using System.Collections.Generic;
using AutoChess.Game;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 会话总线 → UI 面板的事件桥（批次4U0）。
    /// 职责三件：
    ///  1. 白名单——面板只许订 UI 关心的会话事件（战斗微事件归场景立牌/弹道那套表现层消费；
    ///     与批次4A 事件流白名单同款纪律：名单即文档、越界即炸）；
    ///  2. 会话生命周期解耦——桥 Attach 到当前会话的总线，重开一局换总线只重挂桥，
    ///     面板挂在桥上的订阅原地不动（面板不该知道「这一局的总线是谁」）；
    ///  3. Attach 时一次性全订白名单事件、转发给桥上名单——总线上永远只有桥这一层订阅者，
    ///     面板增减订阅不碰总线（摘挂干干净净，防订阅泄漏扩散到总线侧）。
    /// 纯 C# 零 FairyGUI 依赖——EditMode 直测。
    /// 白名单口径：初版覆盖 4U1 面板需求（主 HUD/商店/羁绊/开始界面）；摆位三件套（UnitPlaced/Moved/Removed）
    /// 原计划 4U2 拉入——备战席卡片随 4U1 上线，提前进白名单。
    /// </summary>
    public sealed class UiEventBridge
    {
        /// <summary>UI 关心的会话总线事件全集（4U0 初版 22 条 + 4U1 扩容 4 条 = 26 条）。</summary>
        private static readonly HashSet<Type> Whitelist = new HashSet<Type>
        {
            // 经济与血量（主 HUD）
            typeof(GoldChangedEvent),
            typeof(HpChangedEvent),
            typeof(XpChangedEvent),
            typeof(LevelUpEvent),
            typeof(IncomeSettledEvent),
            // 回合与终局（HUD 回合数 / 4U4 结算面板）
            typeof(RoundStartedEvent),
            typeof(RoundEndedEvent),
            typeof(RunFinishedEvent),
            // 商店（4U1 商店面板）
            typeof(ShopRolledEvent),
            typeof(ShopBoughtEvent),
            // 羁绊（4U1 羁绊面板）
            typeof(SynergiesChangedEvent),
            // 名册 + 摆位三件套（备战席卡片数据源；原计划 4U2 拉入——备战席卡片随 4U1 上线，提前进白名单）
            // BenchChangedEvent = 上场/下场/互拖的会话级名册事实（BoardData 的 Placed/Removed 在名册中途发布，
            // 拉会话现势的消费者会读到旧名单——4U1 真台实抓后补的会话级事件）
            typeof(UnitAcquiredEvent),
            typeof(UnitSoldEvent),
            typeof(UnitMergedEvent),
            typeof(UnitPlacedEvent),
            typeof(UnitMovedEvent),
            typeof(UnitRemovedEvent),
            typeof(BenchChangedEvent),
            // 背包与装备（4U2 装备面板）
            typeof(InventoryChangedEvent),
            typeof(EquipmentEquippedEvent),
            typeof(EquipmentUnequippedEvent),
            typeof(EquipmentUpgradedEvent),
            // 晶矿（4U3 拾取交互）
            typeof(OreDroppedEvent),
            typeof(OreOpenedEvent),
            // 策略（4U3 三选一弹层）
            typeof(PolicyOfferedEvent),
            typeof(PolicyPickedEvent),
        };

        private readonly Dictionary<Type, Delegate> _sinks = new Dictionary<Type, Delegate>();
        private readonly List<Action> _unwires = new List<Action>(28);

        private EventBus _bus;

        /// <summary>是否挂着某条会话总线。</summary>
        public bool IsAttached => _bus != null;

        /// <summary>挂到会话总线。重开一局换总线：先摘旧再挂新；同一总线重复挂 = 幂等。</summary>
        public void Attach(EventBus bus)
        {
            if (bus == null) throw new ArgumentNullException(nameof(bus));
            if (_bus == bus) return;
            Detach();
            _bus = bus;

            // 白名单全量登记（订阅者永远只有桥自己——面板的增减发生在桥上）
            Wire<GoldChangedEvent>(bus);
            Wire<HpChangedEvent>(bus);
            Wire<XpChangedEvent>(bus);
            Wire<LevelUpEvent>(bus);
            Wire<IncomeSettledEvent>(bus);
            Wire<RoundStartedEvent>(bus);
            Wire<RoundEndedEvent>(bus);
            Wire<RunFinishedEvent>(bus);
            Wire<ShopRolledEvent>(bus);
            Wire<ShopBoughtEvent>(bus);
            Wire<SynergiesChangedEvent>(bus);
            Wire<UnitAcquiredEvent>(bus);
            Wire<UnitSoldEvent>(bus);
            Wire<UnitMergedEvent>(bus);
            Wire<UnitPlacedEvent>(bus);
            Wire<UnitMovedEvent>(bus);
            Wire<UnitRemovedEvent>(bus);
            Wire<BenchChangedEvent>(bus);
            Wire<InventoryChangedEvent>(bus);
            Wire<EquipmentEquippedEvent>(bus);
            Wire<EquipmentUnequippedEvent>(bus);
            Wire<EquipmentUpgradedEvent>(bus);
            Wire<OreDroppedEvent>(bus);
            Wire<OreOpenedEvent>(bus);
            Wire<PolicyOfferedEvent>(bus);
            Wire<PolicyPickedEvent>(bus);
        }

        /// <summary>摘下总线（面板订阅保留——重挂后继续收）。</summary>
        public void Detach()
        {
            if (_bus == null) return;
            foreach (Action unwire in _unwires) unwire();
            _unwires.Clear();
            _bus = null;
        }

        /// <summary>面板挂事件（白名单外 = 编程错误，fail fast）。</summary>
        public void Subscribe<T>(Action<T> handler) where T : struct
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (!Whitelist.Contains(typeof(T)))
                throw new ArgumentException(
                    $"事件 {typeof(T).Name} 不在 UI 白名单内——面板只许订 UI 关心的会话事件（越界说明该走别的消费路径）");
            if (_sinks.TryGetValue(typeof(T), out Delegate existing))
                _sinks[typeof(T)] = Delegate.Combine(existing, handler);
            else
                _sinks[typeof(T)] = handler;
        }

        /// <summary>按值摘一条订阅（委托相等 = 方法+目标一致）。没登记过就静默返回。</summary>
        public void Unsubscribe<T>(Action<T> handler) where T : struct
        {
            if (handler == null) return;
            if (_sinks.TryGetValue(typeof(T), out Delegate existing))
            {
                Delegate removed = Delegate.Remove(existing, handler);
                if (removed == null) _sinks.Remove(typeof(T));
                else _sinks[typeof(T)] = removed;
            }
        }

        private void Wire<T>(EventBus bus) where T : struct
        {
            bus.Subscribe<T>(Forward<T>);
            _unwires.Add(() => bus.Unsubscribe<T>(Forward<T>));
        }

        private void Forward<T>(T evt) where T : struct
        {
            if (_sinks.TryGetValue(typeof(T), out Delegate d))
                ((Action<T>)d).Invoke(evt);
        }
    }
}
