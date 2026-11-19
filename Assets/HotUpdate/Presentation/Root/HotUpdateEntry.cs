using UnityEngine;

namespace AutoChess.Presentation
{
    /// <summary>热更区组合根。Run 的每一行都是三件事的证据：
    /// 热更代码在跑 + 热更能调 AOT 服务 + YooAsset 管线能取真货。
    /// 批次4S 起 Run 只做装配：场景流（Bootstrap→Start→Main 的推进与临时开始按钮）
    /// 整体交给 SceneFlowDriver——它挂 DontDestroyOnLoad 跨场景存活，
    /// 先去 Start 装开始界面骨架，点击开始后才进 Main（不再直跳战台）。</summary>
    public static class HotUpdateEntry
    {
        public static void Run(string message)
        {
            Debug.Log($"[HotUpdateEntry] 控制权移交成功: {message} | " +
                      $"棋盘={AutoChess.Game.GameConst.BoardWidth}x{AutoChess.Game.GameConst.BoardHeight} | " +
                      $"tick={AutoChess.Game.GameConst.TickSeconds}s");

            // 批次4U0：FGUI 底座（面板管理器+事件桥+Stage）与场景流同期装配——
            // Stage 自带 DontDestroyOnLoad，UI 穿过三段场景全程存活
            UiRoot.Init();

            // 批次4U1：真包加载首验——Package1 经 UiPackageLoader（YooAsset 生产路径，编辑器模拟与真机同一代码）
            // 入货；包常驻会话（Stage 同款生存策略，场景切换不卸载）。真包加载漏了这步 =
            // CreateObject 拿 null（DevStub 纯代码拼装暴露不了的坑，4U1 首个真面板实抓）。
            UiPackageLoader.Load(UiCatalog.PackageLocation);

            // 三段场景流（P4-4）：Bootstrap 已卸任，驱动器接管后续两跳
            var flowGo = new GameObject("SceneFlow");
            Object.DontDestroyOnLoad(flowGo);   // Single 加载会卸掉 Bootstrap，驱动器必须跨场景存活
            flowGo.AddComponent<SceneFlowDriver>();
        }
    }
}