namespace AutoChess.Game
{
    /// <summary>
    /// 单位编号分配器：单调自增 1, 2, 3 ...
    /// 为什么从 1 开始：0 恒表示"无/非法"（ID 体系约定），default(int) 天然安全。
    /// 每局一个实例（非 static）：GameSession 持有它；测试各 new 各的，互不污染。
    /// </summary>
    public sealed class IdAllocator
    {
        private int _last;

        /// <summary>返回下一个新 id（从 1 开始，永不回退）。</summary>
        public int Next() => ++_last;
    }
}