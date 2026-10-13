using AutoChess.Runtime;   
using UnityEngine;

namespace AutoChess.Presentation
{
    /// <summary>热更区组合根。Run 的每一行都是三件事的证据：
    /// 热更代码在跑 + 热更能调 AOT 服务 + YooAsset 管线能取真货。</summary>
    public static class HotUpdateEntry
    {
        public static void Run(string message)
        {
            Debug.Log($"[HotUpdateEntry] 控制权移交成功: {message} | " +
                      $"棋盘={AutoChess.Game.GameConst.BoardWidth}x{AutoChess.Game.GameConst.BoardHeight} | " +
                      $"tick={AutoChess.Game.GameConst.TickSeconds}s");

            AssetService.LoadMainScene("Assets/Res/Scenes/Main");   // 路径制 location，去扩展名
        }
    }
}