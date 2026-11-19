using FairyGUI;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace AutoChess.Presentation
{
    /// <summary>
    /// UI 底座组合根（批次4U0）：面板管理器 + 事件桥 + FGUI Stage 的初始化收口。
    /// HotUpdateEntry 移交瞬间 Init（与 SceneFlowDriver 同期装配）；幂等——BattleRoot 侧防御性再调无害。
    /// GRoot.inst 首次访问自动创建 Stage（自带 StageEngine/StageCamera/UIContentScaler，
    /// DontDestroyOnLoad 跨场景存活）——UI 底座生于 Bootstrap、活着穿过 Start/Main，
    /// 与场景流驱动器同款生存策略。
    /// 设计分辨率 1920x1080 是常量真源：FGUI 编辑器工程的发布设置必须与之一致
    /// （4U1 做界面时对齐；要改两边一起改——不一致 = UI 在不同机器上被缩放得非预期）。
    /// </summary>
    public static class UiRoot
    {
        public const int DesignWidth = 1920;
        public const int DesignHeight = 1080;

        public static PanelManager Panels { get; private set; }
        public static UiEventBridge Events { get; private set; }
        public static bool Initialized { get; private set; }

        public static void Init()
        {
            if (Initialized) return;
            Panels = new PanelManager();
            Events = new UiEventBridge();
            GRoot.inst.SetContentScaleFactor(DesignWidth, DesignHeight,
                UIContentScaler.ScreenMatchMode.MatchWidthOrHeight);
            Initialized = true;
            RefreshCameraStack();   // URP 叠加初次挂接（Bootstrap 期可能无主相机——场景激活时 SceneFlowDriver 会再刷）
            Debug.Log($"[UiRoot] FGUI 底座就绪：设计分辨率 {DesignWidth}x{DesignHeight}"
                      + "（FGUI 编辑器发布设置须一致；中文字体方案 4U1 定）");
        }

        /// <summary>URP 相机叠加刷新：StageCamera 以 Overlay 形态挂进当前主相机的渲染栈。
        /// 为什么必须：StageCamera 自带的 depth=1 + clearFlags=Depth 是内置管线的多相机叠加做法；
        /// URP 里多台 Base 相机各自独立出图——StageCamera 会用自身默认背景色（深蓝）竞争整屏输出，
        /// 把主相机画面整个盖掉（4U0 真台录像+隔离实验实锤：关掉 StageCamera 战台即回来）。
        /// 幂等：已在栈中则跳过；无主相机（Bootstrap 检查位）静默跳过等下个场景；主相机非 Base 不硬挂。
        /// 场景切换（Single 加载换主相机）后必须重挂——挂接点 = SceneFlowDriver 场景激活分支。</summary>
        public static void RefreshCameraStack()
        {
            Camera stageCamera = StageCamera.main;   // FGUI 静态口（Stage 相机，常驻）
            if (stageCamera == null) return;

            var stageData = stageCamera.GetUniversalAdditionalCameraData();
            stageData.renderType = CameraRenderType.Overlay;

            Camera mainCamera = Camera.main;
            if (mainCamera == null || mainCamera == stageCamera) return;
            var mainData = mainCamera.GetUniversalAdditionalCameraData();
            if (mainData.renderType != CameraRenderType.Base) return;
            if (!mainData.cameraStack.Contains(stageCamera))
                mainData.cameraStack.Add(stageCamera);
        }
    }
}
