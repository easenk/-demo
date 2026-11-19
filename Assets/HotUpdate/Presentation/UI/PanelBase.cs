using System;
using System.Collections.Generic;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 面板基类（批次4U0）：生命周期（Open/Close 幂等）+ 事件订阅自动摘除。
    /// Bind 只许在面板打开期间调（OnOpen 内）——关面板必摘全部订阅：
    /// 桥上挂着已关面板的回调 = 「下一场还往死面板刷数据」的经典泄漏源，构造上杜绝。
    /// 纯 C#（FguiPanelBase 才碰 FairyGUI）——本类 EditMode 直测。
    /// </summary>
    public abstract class PanelBase
    {
        private readonly UiEventBridge _bridge;
        private readonly List<Action> _unbinds = new List<Action>(4);

        /// <summary>构造即定型（面板身份不可变）。</summary>
        protected PanelBase(PanelId id, UiEventBridge bridge)
        {
            if (bridge == null) throw new ArgumentNullException(nameof(bridge));
            Id = id;
            _bridge = bridge;
        }

        public PanelId Id { get; }
        public bool IsOpen { get; private set; }

        public void Open()
        {
            if (IsOpen) return;
            IsOpen = true;      // 先置位再回调——OnOpen 内的 Bind 守卫才放行
            OnOpen();
        }

        public void Close()
        {
            if (!IsOpen) return;
            OnClose();
            UnbindAll();        // 关面板必摘订阅（见类注释）
            IsOpen = false;
        }

        /// <summary>绑定白名单事件（白名单校验在桥侧）；关面板自动解绑。</summary>
        protected void Bind<T>(Action<T> handler) where T : struct
        {
            if (!IsOpen)
                throw new InvalidOperationException($"面板 {Id} 未打开——Bind 只能在 OnOpen 内调用");
            _bridge.Subscribe(handler);
            _unbinds.Add(() => _bridge.Unsubscribe(handler));
        }

        protected virtual void OnOpen() { }
        protected virtual void OnClose() { }

        private void UnbindAll()
        {
            foreach (Action unbind in _unbinds) unbind();
            _unbinds.Clear();
        }
    }
}
