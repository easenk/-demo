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

            // 三段场景流（P4-4）：Bootstrap 已卸任，驱动器接管后续两跳
            var flowGo = new GameObject("SceneFlow");
            Object.DontDestroyOnLoad(flowGo);   // Single 加载会卸掉 Bootstrap，驱动器必须跨场景存活
            flowGo.AddComponent<SceneFlowDriver>();
        }
    }
}