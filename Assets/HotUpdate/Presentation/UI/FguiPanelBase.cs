using System;
using FairyGUI;

namespace AutoChess.Presentation
{
    /// <summary>
    /// FGUI 面板基类（批次4U0）：视图的创建/挂树/销毁收口——生命周期在 PanelBase（纯 C#），
    /// 本类只补「视图长在 FairyGUI 树上」这一层（薄绑定）。
    /// 真面板（4U1 起）：设 PackageName/ComponentName，从 FGUI 编辑器发布的包里 CreateObject
    /// （包先经 UiPackageLoader.Load 入货）；代码拼装面板（DevStub）：覆写 CreateView 自己拼。
    /// 子类钩子：OnViewCreated（绑事件/刷初值）/ OnViewClosing（收尾，此时 View 还活着）。
    /// </summary>
    public abstract class FguiPanelBase : PanelBase
    {
        protected FguiPanelBase(PanelId id, UiEventBridge bridge) : base(id, bridge) { }

        /// <summary>FGUI 编辑器发布的包名（真面板必设；代码拼装面板保持 null）。</summary>
        protected virtual string PackageName => null;

        /// <summary>包内组件名（真面板必设）。</summary>
        protected virtual string ComponentName => null;

        /// <summary>当前视图（面板打开期间非空；关闭即销毁置空）。</summary>
        public GComponent View { get; private set; }

        protected sealed override void OnOpen()
        {
            View = CreateView();
            if (View == null)
                throw new InvalidOperationException($"面板 {Id} 视图创建失败——检查包名/组件名或 CreateView 覆写");
            GRoot.inst.AddChild(View);
            OnViewCreated();
        }

        protected sealed override void OnClose()
        {
            OnViewClosing();
            if (View != null)
            {
                View.RemoveFromParent();
                View.Dispose();
                View = null;
            }
        }

        /// <summary>默认从包创建（真面板路径）；代码拼装面板覆写本方法。</summary>
        protected virtual GComponent CreateView()
        {
            if (PackageName == null || ComponentName == null)
                throw new InvalidOperationException($"面板 {Id} 未设 PackageName/ComponentName，也未覆写 CreateView");
            return UIPackage.CreateObject(PackageName, ComponentName) as GComponent;
        }

        protected virtual void OnViewCreated() { }
        protected virtual void OnViewClosing() { }
    }
}
