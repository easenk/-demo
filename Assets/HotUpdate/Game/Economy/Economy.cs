using System;

namespace AutoChess.Game
{
    /// <summary>
    /// 一场战斗结算的三笔分账明细（决策 #21①：结算收入 = 固定段位 + 利息 + 连胜，三笔分开记账）。
    /// 为什么分账：结算 UI 要分行展示（固定 +5 / 利息 +1 / 连胜 +3），测试要逐笔断言——
    /// 一笔糊涂账既不好看也不好审。
    /// </summary>
    public readonly struct IncomeBill
    {
        /// <summary>固定段位：基座 5 + 已通关精英关数（#21①：通 R6 后每场 +6、通 R11 后每场 +7；精英败档位不涨）。</summary>
        public readonly int FixedGold;

        /// <summary>利息：结算瞬间按钱包余额算（每 10 金 +1、封顶 5）。</summary>
        public readonly int InterestGold;

        /// <summary>连胜：2 连 +1、3 连 +2、4 连及以上 +3 封顶；败场恒 0（连胜清零）。</summary>
        public readonly int StreakGold;

        /// <summary>三笔合计（本次入账总额）。</summary>
        public readonly int TotalGold;

        /// <summary>记账后的当前连胜数（UI 展示"连胜 x"用）。</summary>
        public readonly int WinStreak;

        public IncomeBill(int fixedGold, int interestGold, int streakGold, int totalGold, int winStreak)
        {
            FixedGold = fixedGold;
            InterestGold = interestGold;
            StreakGold = streakGold;
            TotalGold = totalGold;
            WinStreak = winStreak;
        }
    }

    /// <summary>
    /// 经济系统：金币钱包 + 等级/经验 + 连胜账本 + 战斗结算收入（数值 = 决策 #21，2026-10-17 定案）。
    /// 纯 C#：不认识引擎、不持随机——千场模拟时它跟着 GameSession 一起被狂 new。
    ///
    /// 纪律：支出走 TrySpend（不够 = 纯拒绝），收入走 AddGold；每次变动都广播，
    /// 表现层只听事件不轮询（private set 让"直接改"无从谈起）。
    /// 金币不变量：Gold 恒 ≥ 0（构造即校验、TrySpend 拒绝超额、SpendClamped 钳 0），利息公式因此无需防负。
    /// </summary>
    public sealed class Economy
    {
        // ── 决策 #21 数值锚点（第一版；阶段3 千场压测按 #21⑩ 标定）──

        /// <summary>起始金币 3（#21④）。GameSession（批次5）构造 Economy 时引用它——单一真源。</summary>
        public const int DefaultStartGold = 3;

        /// <summary>利息步长：每攒 10 金 +1（#21②）。</summary>
        public const int InterestPer = 10;

        /// <summary>利息封顶 5（银行 50 封顶——存钱流天花板，鼓励花钱）。</summary>
        public const int InterestCap = 5;

        /// <summary>固定段位基座：R1~R6 每场结算 +5（#21①）。</summary>
        public const int BaseBattleIncome = 5;

        /// <summary>每通关一关精英，固定段位 +1（#21①：通 R6 → +6、通 R11 → +7；精英败档位不涨由调用方保证）。</summary>
        public const int EliteClearIncomeBonus = 1;

        /// <summary>每场战斗结束被动经验 +2（#21⑤，结算时发放）。</summary>
        public const int PassiveXpPerBattle = 2;

        /// <summary>买一次经验的金币：4 金 = 4 经验，不限购（#21⑤）。</summary>
        public const int XpCost = 4;

        /// <summary>买一次获得的经验。</summary>
        public const int XpPerBuy = 4;

        /// <summary>等级上限（等级 = 上场位数，#21⑤）：8 = 满级；开局 2 级。</summary>
        public const int MaxLevel = 8;

        /// <summary>
        /// 升级门槛（累计经验，#21⑤）：下标 = 目标等级，值 = 升到该级所需的累计经验。
        /// L3=4 / L4=10 / L5=16 / L6=24 / L7=38 / L8=54（下标 0/1/2 恒 0：开局 2 级，更低级不存在）。
        /// 节奏验算（#21⑤ 的数学）：被动经验 10 场 ×2 = 20 → R11 备战再买 34 金（34 XP）恰好 54 满级；
        /// 低费流 R7 备战被动 12 + 买 12 金 = 24 恰好 6 级；R12 再补 4 金 = 38 恰好 7 级。
        /// </summary>
        private static readonly int[] CumulativeXpToLevel = { 0, 0, 0, 4, 10, 16, 24, 38, 54 };

        private readonly EventBus _bus;
        private int _streak;

        public int Gold { get; private set; }

        /// <summary>当前等级（开局 2 = 上场位 2 起步）。</summary>
        public int Level { get; private set; } = 2;

        /// <summary>累计经验（升级判定基准；战绩/存档的直接数据源）。</summary>
        public int XpTotal { get; private set; }

        /// <summary>当前连胜数（败场清零）。</summary>
        public int WinStreak => _streak;

        /// <summary>当前级内已攒经验（派生：累计 - 本级门槛；UI 经验条分子）。</summary>
        public int XpProgress => XpTotal - CumulativeXpToLevel[Level];

        /// <summary>距下一级还差多少经验（派生；满级恒 0）。</summary>
        public int XpToNextLevel => Level >= MaxLevel ? 0 : CumulativeXpToLevel[Level + 1] - XpTotal;

        /// <summary>当前金币能吃到的利息（每 10 金 +1，封顶 5）。Gold 恒 ≥ 0，无需防负。</summary>
        public int Interest => GameMath.Min(InterestCap, Gold / InterestPer);

        /// <summary>当前连胜的加成（#21③：2 连 +1、3 连 +2、4 连及以上 +3 封顶；不足 2 连为 0）。</summary>
        public int StreakBonus => _streak >= 4 ? 3 : _streak == 3 ? 2 : _streak == 2 ? 1 : 0;

        public Economy(EventBus bus, int startGold)
        {
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
            if (startGold < 0)
                throw new ArgumentOutOfRangeException(nameof(startGold), "开局金币必须 ≥ 0（钱包不变量：Gold 恒 ≥ 0）");
            Gold = startGold;
        }

        /// <summary>支出：非正数 / 不够 = 纯拒绝（false）——一分不扣、不发事件。</summary>
        public bool TrySpend(int amount)
        {
            if (amount <= 0 || Gold < amount) return false;
            Gold -= amount;
            _bus.Publish(new GoldChangedEvent(Gold, -amount));
            return true;
        }

        /// <summary>收入：加钱并广播（≤0 的收入是配置错误，直接无视——账本只记真发生的事实）。</summary>
        public void AddGold(int amount)
        {
            if (amount <= 0) return;
            Gold += amount;
            _bus.Publish(new GoldChangedEvent(Gold, amount));
        }

        /// <summary>
        /// 扣款（钳 0 版）：扣到 0 为止、返回实扣金额——策略负金币代价的专用入口（D2-5）。
        /// 为什么不走 TrySpend：策略候选必须"可选"（门禁逼玩家选完才能开战），
        /// 若"钱不够就纯拒绝"会产生死锁（3 个候选都付不起 → 选不了 → 开不了战）；
        /// 也不允许负余额（利息/买人/刷新就得全面防负数，为一类条目不值得）。
        /// 编表纪律封漏洞：代价条目建议 ≤5 金，"钱包空时白嫖代价"至多发生一次（已选不再进候选）。
        /// 阶段2 批次1 暂无生产调用方（策略在批次7），接口先备好、测试先锁死。
        /// </summary>
        public int SpendClamped(int amount)
        {
            if (amount <= 0 || Gold <= 0) return 0;
            int applied = GameMath.Min(Gold, amount);
            Gold -= applied;
            _bus.Publish(new GoldChangedEvent(Gold, -applied));
            return applied;
        }

        /// <summary>
        /// 买经验：4 金 = 4 XP，不限购（#21⑤）。两个独立拒绝条件：没钱、已满级——
        /// 满级检查必须在扣钱之前：先扣钱再发现白买，钱就成了空气（"失败必须沉默"的原因）。
        /// </summary>
        public bool BuyXp()
        {
            if (Level >= MaxLevel) return false;   // 满级：买了也白买 → 先拒绝，钱不扣
            if (!TrySpend(XpCost)) return false;    // 没钱：纯拒绝
            AddXp(XpPerBuy);
            return true;
        }

        /// <summary>
        /// 加经验（结算被动 + 购买都走这里）。执行时间线：
        ///   1) 累计账本 XpTotal += xp；
        ///   2) 逐级推进：累计 ≥ 下一级门槛就升一级（连升用 while，余量自然滚入）；
        ///   3) 事件序焊死：LevelUpEvent 逐级先发，最后一次性发 XpChangedEvent 携带最终进度——
        ///      表现层先收"升了几级"，再收"经验条现在是多少"，一条经验条数据从不自相矛盾。
        /// 满级后经验直接丢弃且静默（8 级已是上限，丢弃不产生任何事实）。
        /// </summary>
        public void AddXp(int xp)
        {
            if (xp <= 0 || Level >= MaxLevel) return;

            XpTotal += xp;
            while (Level < MaxLevel && XpTotal >= CumulativeXpToLevel[Level + 1])
            {
                Level++;
                _bus.Publish(new LevelUpEvent(Level));
            }
            _bus.Publish(new XpChangedEvent(Level, XpProgress, XpToNextLevel));
        }

        /// <summary>
        /// 一场战斗的结算入口（#21①⑤）：连胜记账 → 三笔分账入账 → 被动经验。
        /// 执行时间线（事件序焊死，测试逐条盯）：
        ///   1) 连胜记账：胜 +1、败清零（败场自然无连胜笔）；
        ///   2) 三笔分账：固定段位（5 + 精英通关数）+ 利息（按结算前余额）+ 连胜加成；
        ///   3) 广播 IncomeSettledEvent（三笔明细，UI 分行展示）→ AddGold 入账（GoldChangedEvent）
        ///      → 被动经验 +2（升级则 LevelUp/Xp 事件随后）。
        /// eliteClears = 本场之前已通关的精英关数（0~2，GameSession 批次5 传入）。
        /// 每场结算都发钱（败场也有固定段位 + 利息——"每场结算 +5"的口径，金铲铲同款）。
        /// </summary>
        public IncomeBill SettleBattleIncome(bool won, int eliteClears)
        {
            if (eliteClears < 0 || eliteClears > 2)
                throw new ArgumentOutOfRangeException(nameof(eliteClears), "已通关精英关数只可能是 0~2（R6/R11 各一关）");

            _streak = won ? _streak + 1 : 0;

            int fixedGold = BaseBattleIncome + eliteClears * EliteClearIncomeBonus;
            int interestGold = Interest;                    // 按结算前余额算（入账前的钱包）
            int streakGold = won ? StreakBonus : 0;
            var bill = new IncomeBill(fixedGold, interestGold, streakGold,
                fixedGold + interestGold + streakGold, _streak);

            _bus.Publish(new IncomeSettledEvent(bill));
            AddGold(bill.TotalGold);
            AddXp(PassiveXpPerBattle);
            return bill;
        }
    }
}
