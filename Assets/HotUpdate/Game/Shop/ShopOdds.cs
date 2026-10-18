using System;

namespace AutoChess.Game
{
    /// <summary>
    /// 商店概率表：等级 → 五个费用档各占多少百分点（每行合计恒等于 100，决策 #21⑥）。
    /// 实例 + 可注入双源模式：Default 给测试与兜底，正式内容阶段4 由 SO 资产搬运工重建后注入。
    ///
    /// #21⑥ 概率表 v1 锚定值：L2 一费 100%（开局纯 1 费）；L6 三费率峰值 30%（三费钉在 6 级）；
    /// L8 五费 8%（五费摸得到）。不对称设计：升等级先让出一费份额给二三费，
    /// 四五费在 L6 起才明显进场（保级压力 vs 追高费的取舍点）。
    /// </summary>
    public sealed class ShopOdds
    {
        private readonly int[,] _table;   // 行=等级(0~8)，列=费用(0~5)；下标 0 行/列占位不用

        public ShopOdds(int[,] table)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));

            // 行合计恒 100 是"按百分点配额落格"的前提：某行少 1 个百分点，
            // 出价就会偶尔落空——建表事故构造期炸掉（fail fast）。
            for (int level = 2; level <= 8; level++)
            {
                int sum = 0;
                for (int cost = 1; cost <= 5; cost++) sum += table[level, cost];
                if (sum != 100)
                    throw new ArgumentException(
                        $"等级 {level} 的概率行合计必须为 100（当前 {sum}）", nameof(table));
            }
            _table = table;
        }

        public static ShopOdds Default { get; } = new ShopOdds(DefaultTable());

        private static int[,] DefaultTable() => new int[9, 6]
        {
            //      cost0  1费  2费  3费  4费  5费   （列下标=费用，cost0 列占位不用）
            /*0*/ {    0,    0,   0,   0,   0,   0 },
            /*1*/ {    0,    0,   0,   0,   0,   0 },
            /*2*/ {    0,  100,   0,   0,   0,   0 },
            /*3*/ {    0,   75,  25,   0,   0,   0 },
            /*4*/ {    0,   60,  30,  10,   0,   0 },
            /*5*/ {    0,   45,  35,  20,   0,   0 },
            /*6*/ {    0,   25,  35,  30,  10,   0 },   // 三费率峰值 30 钉在 6 级（#21⑥）
            /*7*/ {    0,   15,  25,  35,  22,   3 },
            /*8*/ {    0,   10,  20,  30,  32,   8 },   // 五费 8% 摸得到（#21⑥）
        };

        /// <summary>某等级某费用档的概率（百分点）。越界访问一律返回 0——概率表不该抛异常，"不可能"就是 0。</summary>
        public int Get(int level, int cost)
        {
            if (level < 2 || level > 8 || cost < 1 || cost > 5) return 0;
            return _table[level, cost];
        }

        /// <summary>某等级"概率 &gt; 0"的费用档列表（抽取时的合法档位，升序）。</summary>
        public int[] OpenCosts(int level)
        {
            int count = 0;
            for (int cost = 1; cost <= 5; cost++)
                if (Get(level, cost) > 0) count++;
            var costs = new int[count];
            int i = 0;
            for (int cost = 1; cost <= 5; cost++)
                if (Get(level, cost) > 0) costs[i++] = cost;
            return costs;
        }
    }
}
