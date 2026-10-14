using System;

namespace AutoChess.Game
{
    /// <summary>
    /// 逻辑层唯一随机源（铁律4）：种子注入的 xorshift32，纯整数运算。
    /// 为什么纯整数：跨平台/跨编译优化的位级一致——浮点随机没有这个保证，
    /// 而"同种子同序列"是确定性压测与将来回放调试的地基。
    /// 逻辑层禁止另造随机源（UnityEngine.Random / DateTime.Now / Guid 都不许出现）。
    /// 阶段1 唯一消费方是暴击掷骰（CombatSystem），且只在 CanAttack 通过后调用（D9）。
    /// </summary>
    public sealed class GameRandom
    {
        /// <summary>
        /// 种子为 0 时的固定替代值（黄金分割常数，任意非零皆可）。
        /// 为什么必须避开 0：xorshift 的状态若为 0，输出恒为 0，整个序列退化成常量。
        /// 这个映射被 GameRandomTests.SeedZero_MapsToFixedNonZeroFallback 焊死。
        /// </summary>
        public const uint SeedZeroFallback = 0x9E3779B9u;

        private uint _state;

        /// <summary>原始种子原样保留（复现记账用；0 表示创建时用了替代值）。</summary>
        public uint Seed { get; }

        public GameRandom(uint seed)
        {
            Seed = seed;
            _state = seed == 0u ? SeedZeroFallback : seed;
        }

        /// <summary>[0, maxExclusive) 的整数。</summary>
        public int NextInt(int maxExclusive) => NextInt(0, maxExclusive);

        /// <summary>
        /// [minInclusive, maxExclusive) 的整数。
        /// maxExclusive &lt;= minInclusive 直接抛异常：这是程序员错误，
        /// 静默返回错误值会污染确定性断言，宁可当场炸。
        /// </summary>
        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive)
                throw new ArgumentOutOfRangeException(nameof(maxExclusive), "要求 maxExclusive > minInclusive");

            uint raw = NextUInt();
            long range = (long)maxExclusive - minInclusive;
            // 乘法取高位（无取模偏差）：(raw × range) >> 32 必落在 [0, range)
            return minInclusive + (int)((raw * (ulong)range) >> 32);
        }

        /// <summary>
        /// 百分数判定：RollPercent(25f) = 25% 概率为 true。
        /// 内部整数化：万分之一精度（10000 档），不做浮点比较——
        /// 浮点比较在不同平台可能给出不同结果，整数比较位精确（D8/D9 的前提）。
        /// 0 必不中、100 必中由整数比较自然保证。
        /// </summary>
        public bool RollPercent(float percent)
        {
            return NextInt(10000) < (int)(percent * 100f);
        }

        private uint NextUInt()
        {
            // xorshift32（G. Marsaglia）：三个移位异或步，状态自更新后即产出
            uint x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return x;
        }
    }
}