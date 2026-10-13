namespace AutoChess.Game
{
    /// <summary>
    /// 逻辑层全局常量：棋盘尺寸、tick 时长等。
    /// 位于纯C#逻辑层程序集（AutoChess.Game），零 UnityEngine 引用。
    /// </summary>
    public static class GameConst
    {
        /// <summary>棋盘宽度（列数）。2026-10-12 决策#8：8列×6行=48格，与参考工程战台一致。</summary>
        public const int BoardWidth = 8;

        /// <summary>棋盘高度（行数）</summary>
        public const int BoardHeight = 6;

        /// <summary>逻辑层固定 tick 时长（秒）。表现层驱动器把引擎时间翻译成它。</summary>
        public const float TickSeconds = 0.5f;

        /// <summary>单场战斗最大 tick 数（防死循环）</summary>
        public const int MaxTicks = 300;
    }
}
