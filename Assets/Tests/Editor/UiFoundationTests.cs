using System;
using System.Collections.Generic;
using AutoChess.Game;
using AutoChess.Presentation;
using NUnit.Framework;

namespace AutoChess.Tests
{
    /// <summary>
    /// 阶段4 批次4U0：FGUI 底座的纯 C# 内核验收（面板管理器/面板生命周期/事件桥）。
    ///
    /// 分节契约：
    ///  · PanelManager：登记/摘除/寻址 fail fast（同 id 二次登记炸、未登记开面板炸、
    ///    None 哨兵拒收）；Open/Close 幂等；摘除开着面板必先关（订阅摘干净再走）；CloseAll 全关；
    ///  · UiEventBridge 白名单：白名单内事件 总线→桥→面板 一跳到达（载荷逐字段保真）；
    ///    白名单外订阅即炸（批次4A 事件流白名单同款纪律）；摘挂自由（Detach 后总线事件不再达、
    ///    面板订阅保留、重挂后继续收）；换总线重挂（重开一局语义——旧局事件隔离）；
    ///  · 面板订阅自动摘除（Bind 闭环）：关面板后事件不再达（「死面板刷新」泄漏在构造上杜绝）、
    ///    重开重绑正常收；Bind 只许开面板期间调；两面板同事件互不干扰、关一个另一个照收。
    /// FairyGUI 绑定薄层（FguiPanelBase/UiPackageLoader/DevStubPanel）走真台验收，不在此测。
    /// </summary>
    public sealed class UiFoundationTests
    {
        // ─────────────────────── PanelManager ───────────────────────

        [Test]
        public void Manager_RegisterDuplicateId_Throws()
        {
            var manager = new PanelManager();
            manager.Register(new ProbePanel(PanelId.MainHud, new UiEventBridge()));
            Assert.Throws<InvalidOperationException>(() =>
                manager.Register(new ProbePanel(PanelId.MainHud, new UiEventBridge())), "同一 PanelId 二次登记=装配错误");
        }

        [Test]
        public void Manager_RegisterNoneId_Throws()
        {
            var manager = new PanelManager();
            Assert.Throws<ArgumentException>(() =>
                manager.Register(new ProbePanel(PanelId.None, new UiEventBridge())), "None=哨兵，不落任何实例");
        }

        [Test]
        public void Manager_OpenUnregistered_Throws()
        {
            var manager = new PanelManager();
            Assert.Throws<InvalidOperationException>(() => manager.Open(PanelId.Shop), "未登记开面板=装配遗漏 fail fast");
        }

        [Test]
        public void Manager_OpenAndClose_AreIdempotent()
        {
            var manager = new PanelManager();
            var panel = new ProbePanel(PanelId.MainHud, new UiEventBridge());
            manager.Register(panel);

            manager.Open(PanelId.MainHud);
            manager.Open(PanelId.MainHud);
            Assert.IsTrue(panel.IsOpen, "重复开无害");
            Assert.AreEqual(1, panel.OpenCount, "OnOpen 只回调一次");

            manager.Close(PanelId.MainHud);
            manager.Close(PanelId.MainHud);
            Assert.IsFalse(panel.IsOpen, "重复关无害");
            Assert.AreEqual(1, panel.CloseCount, "OnClose 只回调一次");
        }

        [Test]
        public void Manager_UnregisterOpenPanel_ClosesItFirst()
        {
            var manager = new PanelManager();
            var panel = new ProbePanel(PanelId.Settlement, new UiEventBridge());
            manager.Register(panel);
            manager.Open(PanelId.Settlement);

            Assert.IsTrue(manager.Unregister(PanelId.Settlement));
            Assert.IsFalse(panel.IsOpen, "摘除前先关——订阅摘干净再走");
            Assert.IsFalse(manager.TryGet(PanelId.Settlement, out _), "登记表里已移除");
        }

        [Test]
        public void Manager_CloseAll_ClosesEveryOpenPanel()
        {
            var manager = new PanelManager();
            var a = new ProbePanel(PanelId.MainHud, new UiEventBridge());
            var b = new ProbePanel(PanelId.Shop, new UiEventBridge());
            manager.Register(a);
            manager.Register(b);
            manager.Open(PanelId.MainHud);
            manager.Open(PanelId.Shop);

            manager.CloseAll();
            Assert.IsFalse(a.IsOpen);
            Assert.IsFalse(b.IsOpen);
        }

        // ─────────────────────── 桥：白名单转发与摘挂 ───────────────────────

        [Test]
        public void Bridge_ForwardsWhitelistedEventWithPayload()
        {
            var bus = new EventBus();
            var bridge = new UiEventBridge();
            bridge.Attach(bus);
            var manager = new PanelManager();
            var panel = new ProbePanel(PanelId.MainHud, bridge);
            manager.Register(panel);
            manager.Open(PanelId.MainHud);

            bus.Publish(new GoldChangedEvent(123, 5));

            Assert.AreEqual(1, panel.GoldDeltas.Count, "总线→桥→面板一跳到达");
            Assert.AreEqual(5, panel.GoldDeltas[0], "载荷逐字段保真（Delta）");
            Assert.AreEqual(123, panel.LastGold, "载荷逐字段保真（Gold）");
        }

        [Test]
        public void Bridge_SubscribeNonWhitelisted_Throws()
        {
            var bridge = new UiEventBridge();
            Assert.Throws<ArgumentException>(() => bridge.Subscribe<UnitDamagedEvent>(e => { }),
                "战斗微事件不在 UI 白名单——订阅即炸（批次4A 同款纪律）");
        }

        [Test]
        public void Bridge_Detach_StopsForwarding_SubscriptionKept()
        {
            var bus = new EventBus();
            var bridge = new UiEventBridge();
            bridge.Attach(bus);
            var manager = new PanelManager();
            var panel = new ProbePanel(PanelId.MainHud, bridge);
            manager.Register(panel);
            manager.Open(PanelId.MainHud);

            bridge.Detach();
            bus.Publish(new GoldChangedEvent(1, 1));
            Assert.AreEqual(0, panel.GoldDeltas.Count, "摘下总线后事件不再达");

            bridge.Attach(bus);   // 重挂（面板订阅保留）
            bus.Publish(new GoldChangedEvent(2, 2));
            Assert.AreEqual(1, panel.GoldDeltas.Count, "重挂后继续收——面板订阅不因摘挂而丢");
        }

        [Test]
        public void Bridge_ReattachToNewBus_ReplacesOld()
        {
            var oldBus = new EventBus();
            var newBus = new EventBus();   // 重开一局 = 新总线
            var bridge = new UiEventBridge();
            bridge.Attach(oldBus);
            var manager = new PanelManager();
            var panel = new ProbePanel(PanelId.MainHud, bridge);
            manager.Register(panel);
            manager.Open(PanelId.MainHud);

            bridge.Attach(newBus);
            oldBus.Publish(new GoldChangedEvent(1, 1));
            Assert.AreEqual(0, panel.GoldDeltas.Count, "旧总线事件不再达（重开一局后旧局事件隔离）");

            newBus.Publish(new GoldChangedEvent(9, 3));
            Assert.AreEqual(1, panel.GoldDeltas.Count);
            Assert.AreEqual(9, panel.LastGold);
        }

        [Test]
        public void Bridge_AttachNullBus_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new UiEventBridge().Attach(null));
        }

        // ─────────────────────── 面板订阅自动摘除（Bind 闭环） ───────────────────────

        [Test]
        public void PanelClose_AutoUnbinds_ReopenBindsAgain()
        {
            var bus = new EventBus();
            var bridge = new UiEventBridge();
            bridge.Attach(bus);
            var manager = new PanelManager();
            var panel = new ProbePanel(PanelId.MainHud, bridge);
            manager.Register(panel);
            manager.Open(PanelId.MainHud);

            bus.Publish(new GoldChangedEvent(10, 10));
            Assert.AreEqual(1, panel.GoldDeltas.Count);

            manager.Close(PanelId.MainHud);
            bus.Publish(new GoldChangedEvent(20, 20));
            Assert.AreEqual(1, panel.GoldDeltas.Count, "关面板后事件不再达——死面板刷新在构造上杜绝");

            manager.Open(PanelId.MainHud);   // OnOpen 重新 Bind
            bus.Publish(new GoldChangedEvent(30, 30));
            Assert.AreEqual(2, panel.GoldDeltas.Count, "重开重绑正常收");
        }

        [Test]
        public void Panel_BindWhenClosed_Throws()
        {
            var panel = new ProbePanel(PanelId.MainHud, new UiEventBridge());
            Assert.Throws<InvalidOperationException>(() => panel.BindProbeGold(),
                "Bind 只许面板打开期间调（OnOpen 内）");
        }

        [Test]
        public void Panel_TwoSubscribers_Independent()
        {
            var bus = new EventBus();
            var bridge = new UiEventBridge();
            bridge.Attach(bus);
            var manager = new PanelManager();
            var a = new ProbePanel(PanelId.MainHud, bridge);
            var b = new ProbePanel(PanelId.Shop, bridge);
            manager.Register(a);
            manager.Register(b);
            manager.Open(PanelId.MainHud);
            manager.Open(PanelId.Shop);

            bus.Publish(new GoldChangedEvent(7, 7));
            Assert.AreEqual(1, a.GoldDeltas.Count, "两面板同事件都收到");
            Assert.AreEqual(1, b.GoldDeltas.Count);

            manager.Close(PanelId.Shop);
            bus.Publish(new GoldChangedEvent(8, 8));
            Assert.AreEqual(2, a.GoldDeltas.Count, "关掉的不收，开着的照收");
            Assert.AreEqual(1, b.GoldDeltas.Count);
        }

        // ─────────────────────── 4U1 白名单扩容（摆位三件套提前拉入） ───────────────────────

        [Test]
        public void Bridge_Forwards4U1WhitelistAdditions()
        {
            var bus = new EventBus();
            var bridge = new UiEventBridge();
            bridge.Attach(bus);
            var manager = new PanelManager();
            var panel = new PlacementProbePanel(bridge);
            manager.Register(panel);
            manager.Open(PanelId.Bench);

            bus.Publish(new UnitPlacedEvent(1, new Pos(0, 0), true));
            bus.Publish(new UnitMovedEvent(1, new Pos(0, 0), new Pos(1, 0)));
            bus.Publish(new UnitRemovedEvent(1, new Pos(1, 0)));
            bus.Publish(new BenchChangedEvent(3));

            Assert.AreEqual(3, panel.PlacementEvents, "摆位三件套 4U1 起在白名单——备战席卡片的数据源（原计划 4U2，提前拉入）");
            Assert.AreEqual(3, panel.LastBenchCount, "BenchChangedEvent 载荷保真（会话级名册事实，上场/下场/互拖三路径）");
        }

        // ─────────────────────── 测试桩 ───────────────────────

        /// <summary>4U1 白名单扩容探针：摆位三件套各计一条 + BenchChangedEvent 记载荷（名册事实事件）。</summary>
        private sealed class PlacementProbePanel : PanelBase
        {
            public int PlacementEvents;
            public int LastBenchCount = -1;

            public PlacementProbePanel(UiEventBridge bridge) : base(PanelId.Bench, bridge) { }

            protected override void OnOpen()
            {
                Bind<UnitPlacedEvent>(e => PlacementEvents++);
                Bind<UnitMovedEvent>(e => PlacementEvents++);
                Bind<UnitRemovedEvent>(e => PlacementEvents++);
                Bind<BenchChangedEvent>(e => LastBenchCount = e.BenchCount);
            }
        }

        /// <summary>纯 C# 探针面板：数生命周期回调、收集 GoldChangedEvent 载荷。</summary>
        private sealed class ProbePanel : PanelBase
        {
            public int OpenCount;
            public int CloseCount;
            public readonly List<int> GoldDeltas = new List<int>();
            public int LastGold = -1;

            public ProbePanel(PanelId id, UiEventBridge bridge) : base(id, bridge) { }

            protected override void OnOpen()
            {
                OpenCount++;
                Bind<GoldChangedEvent>(e => { GoldDeltas.Add(e.Delta); LastGold = e.Gold; });
            }

            protected override void OnClose() => CloseCount++;

            /// <summary>暴露 protected Bind 给「未打开就 Bind 必炸」用例。</summary>
            public void BindProbeGold() => Bind<GoldChangedEvent>(e => { });
        }
    }
}
