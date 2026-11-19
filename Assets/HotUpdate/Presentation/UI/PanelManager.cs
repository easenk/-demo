using System;
using System.Collections.Generic;

namespace AutoChess.Presentation
{
    /// <summary>面板集合（封闭——阶段4 UI 需求清单定死；DevStub 为开发期验收桩）。
    /// enum 判据（类型形态划线原则）：集合由设计文档定死、编译器拦跨类误传；None=0 哨兵不落实例。</summary>
    public enum PanelId
    {
        None = 0,
        Start = 1,          // 开始界面（4U1，替换 4S 临时按钮）
        MainHud = 2,        // 主 HUD：血量/等级经验/金币/回合/开战按钮（4U1）
        Shop = 3,           // 商店（4U1）
        Synergy = 4,        // 羁绊面板（4U1）
        Bench = 5,          // 备战席卡片栏（4U2，P4-7 FGUI 卡片形式）
        Equipment = 6,      // 装备面板（4U2）
        PolicyChoose = 7,   // 策略三选一弹层（4U3）
        Settlement = 8,     // 结算面板（4U4）
        ExitConfirm = 9,    // 退出确认（4U4）
        DevStub = 10,       // 开发期链路验收桩（4U0；4U1 真界面就位后退役）
    }

    /// <summary>
    /// 面板管理器（批次4U0）：注册表 + 打开/关闭 + 同场唯一实例。
    /// 面板在组合根登记（未登记就开 = 装配遗漏，fail fast 炸出来不静默吞）；
    /// Open/Close 幂等（重复开/重复关都是无害空转）。
    /// 纯 C# 零 FairyGUI 依赖——EditMode 直测；FGUI 绑定薄层在 FguiPanelBase。
    /// </summary>
    public sealed class PanelManager
    {
        private readonly Dictionary<PanelId, PanelBase> _panels = new Dictionary<PanelId, PanelBase>();

        public int Count => _panels.Count;

        /// <summary>登记面板（同 id 二次登记 = 装配错误，fail fast）。</summary>
        public void Register(PanelBase panel)
        {
            if (panel == null) throw new ArgumentNullException(nameof(panel));
            if (panel.Id == PanelId.None)
                throw new ArgumentException("面板必须持非 None 的 PanelId（None=哨兵，不落任何实例）", nameof(panel));
            if (_panels.ContainsKey(panel.Id))
                throw new InvalidOperationException($"PanelId {panel.Id} 已登记——同一面板二次装配");
            _panels.Add(panel.Id, panel);
        }

        /// <summary>摘除面板（开着就先关——订阅必被摘干净再走）。</summary>
        public bool Unregister(PanelId id)
        {
            if (!_panels.TryGetValue(id, out PanelBase panel)) return false;
            panel.Close();
            _panels.Remove(id);
            return true;
        }

        public bool TryGet(PanelId id, out PanelBase panel) => _panels.TryGetValue(id, out panel);

        public bool IsOpen(PanelId id) => _panels.TryGetValue(id, out PanelBase p) && p.IsOpen;

        /// <summary>打开面板（未登记 = 装配遗漏，fail fast）。</summary>
        public void Open(PanelId id)
        {
            if (!_panels.TryGetValue(id, out PanelBase panel))
                throw new InvalidOperationException($"面板 {id} 未登记——组合根装配遗漏");
            panel.Open();
        }

        public void Close(PanelId id)
        {
            if (!_panels.TryGetValue(id, out PanelBase panel))
                throw new InvalidOperationException($"面板 {id} 未登记——组合根装配遗漏");
            panel.Close();
        }

        /// <summary>关掉所有开着的面板（场景切换/终局收尾用）。</summary>
        public void CloseAll()
        {
            foreach (PanelBase panel in _panels.Values) panel.Close();
        }
    }
}
