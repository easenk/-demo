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

        /// <summary>
        /// 逻辑层每秒步数（2026-10-16 定案：1/30 秒一步，对齐金铲铲逻辑帧率 30 步/秒）。
        /// 单一真源：TickSeconds 与 MaxTicks 都从这里推导——将来调步长只改这一个数。
        /// </summary>
        public const int TicksPerSecond = 30;

        /// <summary>
        /// 逻辑层固定 tick 时长（秒）= 1/30。写成分数而非 0.0333 字面量：
        /// 1/30 在二进制浮点里无法精确表示，写成算式让"每秒30步"的定义一目了然。
        /// 表现层驱动器把引擎时间翻译成它；条值累计会有 ±1 步的浮点抖动（确定性不受影响）。
        /// </summary>
        public const float TickSeconds = 1f / TicksPerSecond;

        /// <summary>
        /// 单场战斗最大步数（防死循环）= 150秒 × 30步/秒 = 4500 步。
        /// "单场最长 150 秒"的设计语义不变（2026-10-16 随步长定稿换算）。
        /// </summary>
        public const int MaxTicks = 150 * TicksPerSecond;
    }
}
