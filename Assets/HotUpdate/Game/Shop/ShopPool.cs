using System;
using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// 卡池：全角色复制件的公共仓库（每角色份数 = 决策 #21⑦：1费 18 / 2费 12 / 3费 15 / 4费 8 / 5费 5）。
    /// "18 种 229 份"在批次4 的 18 棋子表入库后自动成立——池按注入的 CharacterTable 派生：
    /// 表里每个 1 费角色占 18 份、每个 2 费 12 份……
    ///
    /// 抽取模型（决策 #21⑦，参考工程验证过的"上架即预订"上游）：
    ///   Roll() 只是出价：按概率表掷费位、再按费位内剩余份数加权选单位——不动份数；
    ///   TakeOut() 才是真买：扣 1 份（上架预订用）。
    /// 两条设计规则因此免费成立：
    ///   · 未购买的卡回池——它们从未离开过池子；
    ///   · 同刷五格可重复同一角色——五格各自独立出价，互不知晓，
    ///     但重复数 ≤ 池内真实份数（预订制保证，货架永不说谎）。
    ///
    /// 随机纪律：GameRandom 构造注入——同种子同抽取序列（确定性链的一环），
    /// 与参考工程的 System.Random 区分（引擎外随机也必须可种子）。
    /// </summary>
    public sealed class ShopPool
    {
        /// <summary>
        /// 费用 → 每角色份数（#21⑦）。4费 8 份、5费 5 份均 &lt; 9 →
        /// 三星四费/五星在设计上不可能（"低费流输出=三星三费、速升流输出=二星五星"用份数天花板锁死）。
        /// </summary>
        public static readonly int[] CopiesByCost = { 0, 18, 12, 15, 8, 5 };

        private readonly Dictionary<int, int> _copies = new Dictionary<int, int>();
        private readonly GameRandom _random;
        private readonly CharacterTable _characters;
        private readonly ShopOdds _odds;

        public ShopPool(GameRandom random, CharacterTable characters, ShopOdds odds)
        {
            _random = random ?? throw new ArgumentNullException(nameof(random));
            _characters = characters ?? throw new ArgumentNullException(nameof(characters));
            _odds = odds ?? throw new ArgumentNullException(nameof(odds));

            foreach (var def in characters.All)
            {
                if (def.Cost < 1 || def.Cost > 5)
                    throw new ArgumentException(
                        $"棋子 {def.Id}（{def.DisplayName}）费用必须为 1~5（当前 {def.Cost}）", nameof(characters));
                _copies[def.Id] = CopiesByCost[def.Cost];
            }
        }

        /// <summary>某角色剩余份数（0 = 已被买干；未知 id 也是 0）。</summary>
        public int CopiesLeft(int charId) => _copies.TryGetValue(charId, out var n) ? n : 0;

        /// <summary>全池剩余总份数（账目测试用：出价不动、买定减一、卖回加一，永不漂移）。</summary>
        public int TotalLeft
        {
            get
            {
                int sum = 0;
                foreach (var n in _copies.Values) sum += n;
                return sum;
            }
        }

        /// <summary>
        /// 两段式出价（决策 #21⑦；不扣份数）。开不出 → 返回 Id=0 哨兵（CharacterDef.None 语义）。
        /// 第一段：按等级概率行掷费用档——只在世界"存活档"（概率 &gt; 0 且档内总份数 &gt; 0）里掷，
        ///   掷骰上界 = 存活档概率之和：死亡档位的概率按比例摊还给存活档（金铲铲语义）——
        ///   不学参考工程固定掷 [0,100) 的写法（那会让死亡档的概率质量全砸给兜底的最后一个存活档，高费偏置）；
        ///   该等级所有档全空 → 不掷骰直接返回空，不无限死磕。
        /// 第二段：档内按剩余份数加权掷具体角色——谁的余量多谁容易出，"抽干了就不来"。
        /// 随机消耗点与行为一一对应：成功出价恒消耗 2 次掷骰（费位一次 + 档内一次），空出价消耗 0 次。
        /// </summary>
        public CharacterDef Roll(int level)
        {
            // 合法档位 = 概率>0 且档内总份数>0。先算好，避免"抽中空档再重来"的循环反复扫表。
            var costs = _odds.OpenCosts(level);
            var odds = new List<int>(costs.Length);
            var alive = new List<int>(costs.Length);
            foreach (var cost in costs)
            {
                int copiesInCost = 0;
                foreach (var def in _characters.GetByCost(cost))
                    copiesInCost += CopiesLeft(def.Id);
                if (copiesInCost > 0) { odds.Add(_odds.Get(level, cost)); alive.Add(cost); }
            }
            if (alive.Count == 0) return CharacterDef.None;   // 护栏：能开的档全空 → 这格开不出来

            int aliveSum = 0;
            for (int i = 0; i < alive.Count; i++) aliveSum += odds[i];
            int roll = _random.NextInt(aliveSum);               // 存活档概率和内均匀落格（摊还式）
            int acc = 0;
            int chosen = alive[alive.Count - 1];                // 兜底：数学上到不了（roll < aliveSum），防意外
            for (int i = 0; i < alive.Count; i++)
            {
                acc += odds[i];
                if (roll < acc) { chosen = alive[i]; break; }
            }

            // 第二段：档内按剩余份数加权
            var candidates = _characters.GetByCost(chosen);
            int total = 0;
            foreach (var def in candidates) total += CopiesLeft(def.Id);
            if (total <= 0) return CharacterDef.None;           // 竞态护栏（第一段已过滤，防御性再查）

            roll = _random.NextInt(total);
            acc = 0;
            foreach (var def in candidates)
            {
                acc += CopiesLeft(def.Id);
                if (roll < acc) return def;
            }
            return candidates[candidates.Count - 1];            // 数学上到不了这里（合计 = total），兜底
        }

        /// <summary>买定离手：扣 1 份。扣不动（已 0 / 未知 id）返回 false——纯拒绝。</summary>
        public bool TakeOut(int charId)
        {
            if (CopiesLeft(charId) <= 0) return false;
            _copies[charId]--;
            return true;
        }

        /// <summary>
        /// 卖回回池：加 1 份，返回是否真加了。拒收两种情况（false、池不动）：
        /// 未知 id；已达出厂份数封顶——回池的份数必须有出处，往满额池塞卡 = 凭空造卡。
        /// 返回 bool 与 TakeOut 对称：失败必须能被调用方感知，账本才记得住"事实"而非"尝试"。
        /// </summary>
        public bool Return(int charId)
        {
            if (!_copies.ContainsKey(charId)) return false;
            int cap = CopiesByCost[_characters.Get(charId).Cost];
            if (_copies[charId] >= cap) return false;
            _copies[charId]++;
            return true;
        }
    }
}
