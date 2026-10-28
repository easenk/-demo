using System;
using System.Collections.Generic;
using System.Linq;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    /// <summary>
    /// 阶段2 批次7：策略系统——弹选管线 / PickPolicy / 重算制挂载 / 战后扣血 / 门禁 / 确定性。
    /// 分节契约（每条测试的断言消息自带语义）：
    ///  · 表构造：Id 唯一≥1、效果条目非空、类型字段自洽（修饰器带数组、金币≠0、经验&gt;0、装备带 id、战后扣血&gt;0）——建表事故构造期炸
    ///  · 弹选（#18②）：奖励关胜利→下回合备战弹 3 候选+广播；败/普通关胜不弹；已选不再进候选（#18⑤）；
    ///    池不足 3 退化（2 条表给 2、空表给 0 且不挡门禁）；同种子同操作=同候选序列（确定性）
    ///  · PickPolicy：拒绝矩阵（非备战期/无待选/越界）；金币正负同构（D2-5：负代价扣到 0 不负债）；
    ///    经验入账；装备仅记账零副作用
    ///  · 重算制挂载（2026-10-27 拍板"只有上战场的棋子才算"）：修饰器只挂场上、备战席永不携带；
    ///    下场摘干净上场补挂；场上合成体（全新 Unit）重挂；盖章 StrategyBase+序号 递增
    ///  · 战后扣血（2026-10-27 拍板 A 案）：每场战斗结束都扣（胜也扣）；DamageTaken 保持"战败惩罚"原义（胜恒 0）
    ///  · 钩子条目：选定留存、每场 StartBattle 新造一只注册、传开战瞬间冻结的场上玩家名单
    ///  · 门禁（#18⑥）：有待选开战纯拒绝四不动 → 选完解锁
    /// 测试纪律：怪一律不带矿挡位（避开晶矿门禁干扰）；R1 是奖励关（RoundTable.Default）——弹选链最短路径。
    /// </summary>
    [TestFixture]
    public class PolicyTests
    {
        // ─────────────────────── 测试基建 ───────────────────────

        private static ShopOdds AllCostOne()
        {
            var t = new int[9, 6];
            for (int level = 2; level <= 8; level++) t[level, 1] = 100;
            return new ShopOdds(t);
        }

        private static UnitStats Stats(int hp, int atk, float atkIv = 1f, float moveIv = 1f, int range = 1, int crit = 0)
            => new UnitStats
            {
                MaxHp = hp, AttackPower = atk, Armor = 0, MagicResist = 0, AbilityPower = 100,
                AttackRange = range, AttackInterval = atkIv, MoveInterval = moveIv,
                CritChance = crit, CritDamage = 50, MaxMana = 0, ManaPerHit = 0, ManaRegenPerSecond = 0,
            };

        private static CharacterDef Char(int id, int hp, int atk, float atkIv = 1f, float moveIv = 1f,
            int range = 1, int crit = 0, params int[] tags)
            => new CharacterDef(id, $"棋子{id}", 1, Stats(hp, atk, atkIv, moveIv, range, crit), tags);

        /// <summary>敌方速记：挡位恒 0（策略测试避开晶矿门禁）；crit 恒 0（表数据纪律 D2-7）。</summary>
        private static EnemySpawn Foe(int hp, int atk, Pos cell, float atkIv = 1.3f, int range = 1, string name = "怪")
            => new EnemySpawn(name, Stats(hp, atk, atkIv, 1f, range), cell, 0, 1);

        private static EnemyRoundEntry Round(int round, params EnemySpawn[] spawns)
            => new EnemyRoundEntry(round, spawns);

        // ── 策略表速记 ──

        private static PolicyDef Def(int id, string name, params PolicyEffect[] effects)
            => new PolicyDef(id, name, "占位", effects);

        /// <summary>注入用单条策略表（弹选池恒 1 → 候选恒为它，发放断言无随机歧义）。</summary>
        private static PolicyTable SoloPolicy(params PolicyEffect[] effects)
            => new PolicyTable(new[] { Def(1, "独苗", effects) });

        /// <summary>注入包：角色表必给；编成默认空表；矿池/策略表默认 Default。</summary>
        private static GameTables Tables(CharacterTable chars, EnemyTable enemies = null, PolicyTable policies = null)
            => new GameTables
            {
                Characters = chars,
                Odds = AllCostOne(),
                Rounds = RoundTable.Default,
                Enemies = enemies ?? EnemyTable.Empty,
                Synergies = SynergyTable.Default,
                Ores = OreTable.Default,
                Policies = policies ?? PolicyTable.Default,
            };

        private static CharacterTable SingleChar(int hp = 500, int atk = 100, float atkIv = 1f,
            float moveIv = 1f, int range = 1, int crit = 0, params int[] tags)
            => new CharacterTable(new[] { Char(1, hp, atk, atkIv, moveIv, range, crit, tags) });

        private static GameSession New(EventBus bus, GameTables tables, uint seed = 0u)
            => new GameSession(bus, seed, tables);

        /// <summary>买架上任意有货的一格（架空先刷新）。单角色表下买到谁恒定。</summary>
        private static void BuyAny(GameSession s)
        {
            int slot = Array.FindIndex(s.Shop.Offers, o => o.Id != 0);
            if (slot < 0)
            {
                Assert.IsTrue(s.RefreshShop(), "刷新上架");
                slot = Array.FindIndex(s.Shop.Offers, o => o.Id != 0);
            }
            Assert.GreaterOrEqual(slot, 0, "架上应有货");
            Assert.IsTrue(s.Buy(slot), "买入应成功");
        }

        /// <summary>打完当前回合（开战前先捡光矿、选掉待选策略——双门禁的链路化版）。</summary>
        private static void PlayRound(GameSession s)
        {
            while (s.OresOnGround.Count > 0)
                Assert.IsTrue(s.PickOre(s.OresOnGround[0].Id), "捡光矿（晶矿门禁）");
            if (s.HasPolicyChoice)
                Assert.IsTrue(s.PickPolicy(0), "选掉第 0 个候选（策略门禁）");
            Assert.IsTrue(s.StartBattle(), $"回合 {s.Round} 开战应成功");
            if (s.Phase == GamePhase.Battle)
            {
                int guard = 0;
                while (!s.Combat.IsOver && guard++ < 6000) s.Combat.Tick();
                Assert.IsTrue(s.SettleBattle(), "排空回调结算应成功");
            }
            Assert.IsTrue(s.Phase == GamePhase.Settlement || s.Phase == GamePhase.RunOver,
                $"回合 {s.Round} 打完应停 Settlement（终局回合为 RunOver）");
        }

        /// <summary>
        /// 标准"打完奖励关并弹选"链：R1 一只弱怪（奖励关）玩家必胜 → 结算置旗标 → 进 R2 备战弹选。
        /// 返回停在 R2 备战期的会话（候选在场、门禁生效）。
        /// </summary>
        private static GameSession WinRewardAndOffer(EventBus bus, PolicyTable policies)
        {
            var enemies = new EnemyTable(new[] { Round(1, Foe(30, 0, new Pos(4, 4), range: 5)) });
            var s = New(bus, Tables(SingleChar(hp: 500, atk: 100, range: 5), enemies, policies));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            Assert.IsTrue(s.PlaceOnBoard(s.Bench[0].Id, new Pos(4, 0)));
            PlayRound(s);   // R1 奖励关胜 → _policyDue
            Assert.IsTrue(s.StartPrep(), "进 R2 备战（弹选发生在这里）");
            return s;
        }

        // ─────────────────────── 表构造（建表事故构造期炸） ───────────────────────

        [Test]
        public void PolicyTable_Ctor_ValidatesIdsAndEffects()
        {
            Assert.Throws<ArgumentException>(() => new PolicyTable(new[] { Def(0, "零号", PolicyEffect.Gold(1)) }),
                "id 0 拒");
            Assert.Throws<ArgumentException>(() => new PolicyTable(new[]
            {
                Def(1, "甲", PolicyEffect.Gold(1)),
                Def(1, "乙", PolicyEffect.Gold(2)),
            }), "id 重复拒");
            Assert.Throws<ArgumentException>(() => new PolicyTable(new[] { Def(1, "空效果") }),
                "效果条目空拒");

            // 类型字段自洽
            Assert.Throws<ArgumentException>(() => new PolicyTable(new[] { Def(1, "空修饰器", PolicyEffect.Team()) }),
                "修饰器类条目必须带数组");
            Assert.Throws<ArgumentException>(() => new PolicyTable(new[] { Def(1, "零金币", PolicyEffect.Gold(0)) }),
                "金币 Amount=0 拒");
            Assert.Throws<ArgumentException>(() => new PolicyTable(new[] { Def(1, "负经验", PolicyEffect.Xp(-2)) }),
                "经验恒正拒");
            Assert.Throws<ArgumentException>(() => new PolicyTable(new[] { Def(1, "无装备id", PolicyEffect.Gear(0)) }),
                "装备条目必须带 id");
            Assert.Throws<ArgumentException>(() => new PolicyTable(new[] { Def(1, "零扣血", PolicyEffect.HpCost(0)) }),
                "战后扣血恒正拒");

            var ok = new PolicyTable(new[] { Def(1, "合法", PolicyEffect.Team(new StatModifier(StatKind.Armor, StatOp.Flat, 5)), PolicyEffect.HpCost(4)) });
            Assert.AreEqual(1, ok.All.Count, "合法表登记");
            Assert.AreEqual(1, ok.Get(1).Id, "Get 可查");
            Assert.AreEqual(0, ok.Get(99).Id, "查无返回 default（Id=0）");
        }

        [Test]
        public void PolicyTable_Default_SixEntriesTwoDirections()
        {
            var t = PolicyTable.Default;
            Assert.AreEqual(6, t.All.Count, "Default 六条占位");

            // 两方向形状锁（2026-10-27 拍板）：正面向 4 条 + 负面向 2 条（战后扣血 + 大额回报）
            int hpCostEntries = 0, teamEntries = 0, gearEntries = 0;
            foreach (var def in t.All)
            {
                foreach (var e in def.Effects)
                {
                    if (e.Type == PolicyEffectType.PostBattleHpLoss) hpCostEntries++;
                    if (e.Type == PolicyEffectType.TeamModifier) teamEntries++;
                    if (e.Type == PolicyEffectType.Equipment) gearEntries++;
                }
            }
            Assert.AreEqual(2, hpCostEntries, "两条负面向词条（战后扣血 4 点换大额回报）");
            Assert.GreaterOrEqual(teamEntries, 1, "至少一条全队修饰器（正面向 buff）");
            Assert.GreaterOrEqual(gearEntries, 1, "至少一条装备散件（正面向）");
        }

        // ─────────────────────── 弹选管线（#18②⑤） ───────────────────────

        [Test]
        public void Offer_RewardWin_ThreeCandidatesNextPrep()
        {
            var bus = new EventBus();
            var offered = new List<PolicyOfferedEvent>();
            bus.Subscribe<PolicyOfferedEvent>(e => offered.Add(e));

            // 注入 5 条表（候选 3 抽自 5 有区分度）
            var policies = new PolicyTable(new[]
            {
                Def(1, "甲", PolicyEffect.Gold(1)),
                Def(2, "乙", PolicyEffect.Gold(2)),
                Def(3, "丙", PolicyEffect.Gold(3)),
                Def(4, "丁", PolicyEffect.Gold(4)),
                Def(5, "戊", PolicyEffect.Gold(5)),
            });
            var s = WinRewardAndOffer(bus, policies);

            Assert.AreEqual(1, offered.Count, "恰一条弹选广播");
            Assert.AreEqual(3, offered[0].CandidatePolicyIds.Length, "三选一（#18②）");
            Assert.IsTrue(s.HasPolicyChoice, "有待选策略");
            CollectionAssert.AreEquivalent(s.PolicyCandidates, offered[0].CandidatePolicyIds,
                "只读属性与广播一致");
            var distinct = new HashSet<int>(offered[0].CandidatePolicyIds);
            Assert.AreEqual(3, distinct.Count, "候选互不重复（部分Fisher-Yates 无放回）");
        }

        [Test]
        public void Offer_NotTriggeredOnLossOrNormalWin()
        {
            var bus = new EventBus();
            var offered = new List<PolicyOfferedEvent>();
            bus.Subscribe<PolicyOfferedEvent>(e => offered.Add(e));

            // R1 奖励关败（强怪秒玩家）→ 不弹选
            var enemies = new EnemyTable(new[] { Round(1, Foe(500, 200, new Pos(4, 4), atkIv: 0.5f, range: 5)) });
            var s = New(bus, Tables(SingleChar(hp: 30, atk: 0, range: 5), enemies));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            Assert.IsTrue(s.PlaceOnBoard(s.Bench[0].Id, new Pos(4, 0)));
            PlayRound(s);
            Assert.IsTrue(s.StartPrep(), "R1 败 → R2 备战");
            Assert.IsFalse(s.HasPolicyChoice, "奖励关败不弹选");
            Assert.AreEqual(0, offered.Count);

            // R2 普通关胜 → 也不弹选（只有奖励关胜利记账）
            PlayRound(s);   // R2 空编成：构造即终局秒胜（有棋在场）
            Assert.IsTrue(s.StartPrep(), "R2 → R3 备战");
            Assert.IsFalse(s.HasPolicyChoice, "普通关胜不弹选");
            Assert.AreEqual(0, offered.Count, "全程无弹选广播");
        }

        [Test]
        public void Offer_ExcludesOwnedPolicies()
        {
            var bus = new EventBus();
            var offered = new List<PolicyOfferedEvent>();
            bus.Subscribe<PolicyOfferedEvent>(e => offered.Add(e));

            // 4 条表：第一轮选 1 条（候选 3）→ 第二轮候选 = 4 - 1 = 3 条且不含已选
            var policies = new PolicyTable(new[]
            {
                Def(1, "甲", PolicyEffect.Gold(1)),
                Def(2, "乙", PolicyEffect.Gold(2)),
                Def(3, "丙", PolicyEffect.Gold(3)),
                Def(4, "丁", PolicyEffect.Gold(4)),
            });
            var s = WinRewardAndOffer(bus, policies);
            Assert.IsTrue(s.PickPolicy(0), "第一轮选第 0 个");
            int firstPicked = s.OwnedPolicyIds[0];
            Assert.AreEqual(1, s.OwnedPolicyIds.Count);

            // R2~R4 普通关打过去（无弹选）；R5 又是奖励关（RoundTable.Default）→ 胜 → R6 备战再弹选
            while (s.Round < 5) { PlayRound(s); Assert.IsTrue(s.StartPrep()); }
            PlayRound(s);   // R5 奖励关胜
            Assert.IsTrue(s.StartPrep(), "进 R6 备战（第二轮弹选）");

            Assert.AreEqual(2, offered.Count, "第二轮弹选恰一条");
            var secondCandidates = offered[1].CandidatePolicyIds;
            Assert.AreEqual(3, secondCandidates.Length, "剩 4-1=3 条全进候选");
            CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 4 }.Where(id => id != firstPicked).ToArray(),
                secondCandidates, "候选 = 全表 - 已选（#18⑤）");
        }

        [Test]
        public void Offer_PoolSmallerThan3_Degrades()
        {
            var bus = new EventBus();
            var offered = new List<PolicyOfferedEvent>();
            bus.Subscribe<PolicyOfferedEvent>(e => offered.Add(e));

            // 2 条表 → 候选恰 2（退化不报错）
            var two = new PolicyTable(new[]
            {
                Def(1, "甲", PolicyEffect.Gold(1)),
                Def(2, "乙", PolicyEffect.Gold(2)),
            });
            var s = WinRewardAndOffer(bus, two);
            Assert.AreEqual(1, offered.Count);
            Assert.AreEqual(2, offered[0].CandidatePolicyIds.Length, "池不足 3 有多少给多少");
            Assert.IsTrue(s.HasPolicyChoice);
        }

        [Test]
        public void Offer_EmptyTable_SilentNoGate()
        {
            var bus = new EventBus();
            var offered = new List<PolicyOfferedEvent>();
            bus.Subscribe<PolicyOfferedEvent>(e => offered.Add(e));

            var s = WinRewardAndOffer(bus, new PolicyTable(new PolicyDef[0]));   // 空表
            Assert.AreEqual(0, offered.Count, "空池静默：不广播弹选");
            Assert.IsFalse(s.HasPolicyChoice, "无候选 → 门禁不挡");
            Assert.IsTrue(s.StartBattle(), "空表可直接开战（R2 空编成秒胜）");
        }

        [Test]
        public void Offer_SameSeedSameOps_SameCandidates()
        {
            (GameSession s, List<PolicyOfferedEvent> offered) Play(uint seed)
            {
                var bus = new EventBus();
                var offered = new List<PolicyOfferedEvent>();
                bus.Subscribe<PolicyOfferedEvent>(e => offered.Add(e));
                var policies = new PolicyTable(new[]
                {
                    Def(1, "甲", PolicyEffect.Gold(1)),
                    Def(2, "乙", PolicyEffect.Gold(2)),
                    Def(3, "丙", PolicyEffect.Gold(3)),
                    Def(4, "丁", PolicyEffect.Gold(4)),
                    Def(5, "戊", PolicyEffect.Gold(5)),
                    Def(6, "己", PolicyEffect.Gold(6)),
                });
                var s = WinRewardAndOffer(bus, policies);
                return (s, offered);
            }

            var a = Play(777u);
            var b = Play(777u);
            CollectionAssert.AreEqual(a.offered[0].CandidatePolicyIds, b.offered[0].CandidatePolicyIds,
                "同种子同操作 → 同候选序列（部分Fisher-Yates 确定性）");
        }

        // ─────────────────────── PickPolicy 拒绝矩阵与发放 ───────────────────────

        [Test]
        public void PickPolicy_GatedByPhase()
        {
            var s = New(new EventBus(), Tables(SingleChar(), null, SoloPolicy(PolicyEffect.Gold(1))));
            Assert.IsFalse(s.PickPolicy(0), "PreRun 拒");

            // Battle 期拒（互杀链；此时无待选——门禁保证有候选根本开不了战）
            var enemies = new EnemyTable(new[] { Round(1, Foe(10000, 0, new Pos(4, 4), range: 5)) });
            var inBattle = New(new EventBus(), Tables(SingleChar(hp: 500, atk: 0, range: 5), enemies));
            Assert.IsTrue(inBattle.StartRun());
            Assert.IsTrue(inBattle.Buy(0));
            Assert.IsTrue(inBattle.PlaceOnBoard(inBattle.Bench[0].Id, new Pos(4, 0)));
            Assert.IsTrue(inBattle.StartBattle());
            Assert.IsFalse(inBattle.PickPolicy(0), "Battle 拒");
        }

        [Test]
        public void PickPolicy_RejectionMatrix_PureRejection()
        {
            var bus = new EventBus();
            var picked = new List<PolicyPickedEvent>();
            bus.Subscribe<PolicyPickedEvent>(e => picked.Add(e));

            var s = WinRewardAndOffer(bus, SoloPolicy(PolicyEffect.Gold(10)));
            int goldBefore = s.Eco.Gold;

            Assert.IsFalse(s.PickPolicy(-1), "负格号拒");
            Assert.IsFalse(s.PickPolicy(s.PolicyCandidates.Count), "越界格号拒");
            Assert.AreEqual(goldBefore, s.Eco.Gold, "拒绝：钱包不动");
            Assert.AreEqual(0, picked.Count, "拒绝：不广播");
            Assert.IsTrue(s.HasPolicyChoice, "拒绝：候选仍在");

            Assert.IsTrue(s.PickPolicy(0), "合法格号成功");
            Assert.AreEqual(1, picked.Count);
            // 无待选后再选：拒
            int goldAfter = s.Eco.Gold;
            Assert.IsFalse(s.PickPolicy(0), "无待选拒（候选已消费）");
            Assert.AreEqual(goldAfter, s.Eco.Gold, "无待选拒：钱包不动");
        }

        [Test]
        public void PickPolicy_GoldPositiveAndNegative_ClampedToZero()
        {
            // 正金币：+10 到账
            var bus = new EventBus();
            var s = WinRewardAndOffer(bus, SoloPolicy(PolicyEffect.Gold(10)));
            int goldBefore = s.Eco.Gold;
            Assert.IsTrue(s.PickPolicy(0));
            Assert.AreEqual(goldBefore + 10, s.Eco.Gold, "正金币入账");

            // 负金币代价：-15 从约 8 金（3 开局 - 1 买棋 + 6 结算收入）扣到 0（D2-5：扣到 0 为止不产生负债）
            var bus2 = new EventBus();
            var gold = new List<GoldChangedEvent>();
            bus2.Subscribe<GoldChangedEvent>(e => gold.Add(e));
            var s2 = WinRewardAndOffer(bus2, SoloPolicy(PolicyEffect.Gold(-15)));
            int goldBefore2 = s2.Eco.Gold;
            Assert.Less(goldBefore2, 15, "前置：钱包不足 15 金（SpendClamped 只能扣到 0）");
            Assert.IsTrue(s2.PickPolicy(0));
            Assert.AreEqual(0, s2.Eco.Gold, "扣到 0 为止，不产生负债（D2-5）");
            Assert.AreEqual(-goldBefore2, gold.Last().Delta, "实扣 = 钱包余额（不足部分被钳掉）");
        }

        [Test]
        public void PickPolicy_XpAddsExperience()
        {
            var s = WinRewardAndOffer(new EventBus(), SoloPolicy(PolicyEffect.Xp(4)));
            int xpBefore = s.Eco.XpTotal;   // R1 胜已发被动经验 +2
            Assert.IsTrue(s.PickPolicy(0));
            Assert.AreEqual(xpBefore + 4, s.Eco.XpTotal, "经验 +4");
        }

        [Test]
        public void PickPolicy_Equipment_BookkeepingOnly()
        {
            var bus = new EventBus();
            var picked = new List<PolicyPickedEvent>();
            bus.Subscribe<PolicyPickedEvent>(e => picked.Add(e));

            var policies = new PolicyTable(new[] { Def(1, "散件", PolicyEffect.Gear(7)) });
            var s = WinRewardAndOffer(bus, policies);
            int goldBefore = s.Eco.Gold;
            int xpBefore = s.Eco.XpTotal;
            var playerUnit = s.PlayerBoard[0];
            Assert.IsTrue(s.PickPolicy(0));

            Assert.AreEqual(1, picked.Count, "恰一条选定广播（装备消费者阶段3 按 PolicyId 查表入包，#12⑧ 只给散件）");
            Assert.AreEqual(7, policies.Get(1).Effects[0].EquipmentId, "表内装备 id 原样可查（阶段3 消费者的数据源）");
            Assert.AreEqual(goldBefore, s.Eco.Gold, "装备条目不碰钱包");
            Assert.AreEqual(xpBefore, s.Eco.XpTotal, "装备条目不碰经验");
            Assert.AreEqual(0, playerUnit.Modifiers.Count, "装备条目不挂修饰器（穿戴是阶段3 的事）");
        }

        // ─────────────────────── 重算制挂载（2026-10-27 拍板"只有上战场的棋子才算"） ───────────────────────

        [Test]
        public void PickPolicy_TeamModifier_OnlyOnBoard_RecalculatedOnRosterChange()
        {
            var bus = new EventBus();
            var s = WinRewardAndOffer(bus, SoloPolicy(
                PolicyEffect.Team(new StatModifier(StatKind.AttackPower, StatOp.Percent, 5))));

            // 场上 1 棋（买棋上场打 R1）+ 再买 1 棋进备战席
            BuyAny(s);
            var benchUnit = s.Bench[0];
            Assert.IsFalse(benchUnit.IsOnBoard, "第二张棋买进备战席（未上场）");

            Assert.IsTrue(s.PickPolicy(0));
            var boardUnit = s.PlayerBoard[0];
            Assert.AreEqual(105, boardUnit.EffectiveStats.AttackPower, "场上棋吃到攻 +5%（100×1.05）");
            Assert.AreEqual(1, boardUnit.Modifiers.Count, "恰一条修饰器");
            Assert.AreEqual(ModSourceId.StrategyBase + 1, boardUnit.Modifiers[0].SourceId,
                "盖章 StrategyBase + 实例序号（第一条 = 2001）");

            Assert.AreEqual(0, benchUnit.Modifiers.Count, "备战席棋永不携带（只挂场上，2026-10-27 拍板）");
            Assert.AreEqual(100, benchUnit.EffectiveStats.AttackPower, "备战席面板不动");

            // 下场：摘干净
            Assert.IsTrue(s.TakeOffBoard(boardUnit.Id));
            Assert.AreEqual(0, boardUnit.Modifiers.Count, "下场即摘干净");
            Assert.AreEqual(100, boardUnit.EffectiveStats.AttackPower, "下场后面板回原");

            // 重新上场：补挂（重算制，不是买入制）
            Assert.IsTrue(s.PlaceOnBoard(boardUnit.Id, new Pos(4, 0)));
            Assert.AreEqual(105, boardUnit.EffectiveStats.AttackPower, "上场补挂");

            // 备战席棋上场：同样吃到（人员变动触发重算）
            Assert.IsTrue(s.TakeOffBoard(boardUnit.Id));
            Assert.IsTrue(s.PlaceOnBoard(benchUnit.Id, new Pos(4, 0)));
            Assert.AreEqual(105, benchUnit.EffectiveStats.AttackPower, "换人上场：新上场者补挂");
            Assert.AreEqual(0, boardUnit.Modifiers.Count, "下场者被摘干净");
        }

        [Test]
        public void PickPolicy_SecondPick_SecondInstanceStamp()
        {
            var bus = new EventBus();
            var picked = new List<PolicyPickedEvent>();
            bus.Subscribe<PolicyPickedEvent>(e => picked.Add(e));

            // 两条修饰器策略：第一轮选一条、第二轮（R5 胜后）选另一条 → 实例章递增 2001/2002
            var policies = new PolicyTable(new[]
            {
                Def(1, "攻", PolicyEffect.Team(new StatModifier(StatKind.AttackPower, StatOp.Percent, 5))),
                Def(2, "甲", PolicyEffect.Team(new StatModifier(StatKind.Armor, StatOp.Flat, 5))),
            });
            var s = WinRewardAndOffer(bus, policies);
            Assert.IsTrue(s.PickPolicy(0));
            var first = picked[0];

            while (s.Round < 5) { PlayRound(s); Assert.IsTrue(s.StartPrep()); }
            PlayRound(s);   // R5 奖励关胜
            Assert.IsTrue(s.StartPrep(), "R6 备战：第二次弹选（剩 1 条 → 候选 1）");
            Assert.IsTrue(s.PickPolicy(0), "选另一条");

            Assert.AreEqual(2, picked.Count);
            Assert.AreEqual(ModSourceId.StrategyBase + 1, first.InstanceSourceId, "第一条实例章 2001");
            Assert.AreEqual(ModSourceId.StrategyBase + 2, picked[1].InstanceSourceId, "第二条实例章 2002（递增）");

            var unit = s.PlayerBoard[0];
            Assert.AreEqual(2, unit.Modifiers.Count, "两条策略的修饰器并存（两个实例章）");
            Assert.AreEqual(105, unit.EffectiveStats.AttackPower, "攻 +5% 生效");
            Assert.AreEqual(5, unit.EffectiveStats.Armor, "甲 +5 生效");
        }

        [Test]
        public void PickPolicy_TeamModifier_MergedUnitReattaches()
        {
            // 场上合成链：先上场 1 张、再买 2 张备战（锚点=场上首张、材料=备战两张）→ 合成体是全新 Unit
            // → 重算必须把策略修饰器补挂到合成体身上（与羁绊 Merge_TriggersSynergyRecalc 同款契约）
            var policies = SoloPolicy(
                PolicyEffect.Team(new StatModifier(StatKind.AttackPower, StatOp.Percent, 5)));
            var enemies = new EnemyTable(new[] { Round(1, Foe(30, 0, new Pos(4, 4), range: 5)) });
            var s = New(new EventBus(), Tables(SingleChar(hp: 100, atk: 100, range: 5), enemies, policies));
            Assert.IsTrue(s.StartRun());
            s.Eco.AddGold(50);
            BuyAny(s);   // id1 → 上场
            Assert.IsTrue(s.PlaceOnBoard(s.Bench[0].Id, new Pos(4, 0)));
            BuyAny(s);   // id2 → 备战
            BuyAny(s);   // id3 → 备战；三张凑满 → 锚点=场上 id1，材料=备战 id2/id3（boardChanged=true）
            var merged = s.PlayerBoard.Single(u => u.Star == 2);
            Assert.AreEqual(0, merged.Modifiers.Count, "选策略前：合成体（备战期）无策略修饰器");

            PlayRound(s);   // R1 奖励关胜 → 进 R2 备战弹选
            Assert.IsTrue(s.StartPrep());
            Assert.IsTrue(s.PickPolicy(0), "选攻 +5%");

            // 合成体在场（上场者）→ 选定即挂上（150×1.05=157.5 → 取整 158）
            Assert.AreEqual(158, merged.EffectiveStats.AttackPower,
                "场上合成体被重算补挂（StatsForStar(2) 攻 150 × 1.05）");
            Assert.AreEqual(1, merged.Modifiers.Count, "恰一条实例章修饰器");
        }

        // ─────────────────────── 战后扣血（2026-10-27 拍板 A 案：每场都扣，胜也扣） ───────────────────────

        [Test]
        public void PickPolicy_PostBattleHpLoss_EveryBattleBothWinAndLoss()
        {
            var bus = new EventBus();
            var hp = new List<HpChangedEvent>();
            var ended = new List<RoundEndedEvent>();
            bus.Subscribe<HpChangedEvent>(e => hp.Add(e));
            bus.Subscribe<RoundEndedEvent>(e => ended.Add(e));

            // 注入表：战后扣血 4 + 金币 +20（亡命赌约式）；R1 奖励关胜、R2 普通关胜、R3 强怪败
            var enemies = new EnemyTable(new[]
            {
                Round(1, Foe(30, 0, new Pos(4, 4), range: 5)),
                Round(3, Foe(500, 200, new Pos(4, 4), atkIv: 0.5f, range: 5)),
            });
            var s = New(bus, Tables(SingleChar(hp: 500, atk: 100, range: 5), enemies,
                SoloPolicy(PolicyEffect.HpCost(4), PolicyEffect.Gold(20))));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            Assert.IsTrue(s.PlaceOnBoard(s.Bench[0].Id, new Pos(4, 0)));

            PlayRound(s);   // R1 奖励关胜
            Assert.IsTrue(s.StartPrep(), "R2 备战（弹选）");
            Assert.IsTrue(s.PickPolicy(0), "选亡命赌约");
            int goldAfterPick = s.Eco.Gold;
            Assert.Greater(goldAfterPick, 0, "金币 +20 已入账");

            // R2 普通关胜：胜也扣 4（A 案核心断言）
            PlayRound(s);
            Assert.AreEqual(GameSession.StartHp - 4, s.PlayerHp, "胜场也扣战后血 4 点");
            Assert.AreEqual(0, ended.First(e => e.Round == 2).DamageTaken, "DamageTaken 保持战败惩罚原义（胜恒 0，策略代价不进此字段）");

            // R3 强怪败：战败扣血（3+存活敌人数）+ 战后扣血 4
            Assert.IsTrue(s.StartPrep());
            PlayRound(s);   // R3 败（怪 200 攻 0.5s 一击秒 500 血？不——500 血 3 击；玩家 100 攻杀不死 500 血怪 → 败）
            var r3 = ended.First(e => e.Round == 3);
            Assert.IsFalse(r3.PlayerWon, "R3 战败");
            int battleLoss = r3.DamageTaken;   // 3 + 1 存活 = 4
            Assert.AreEqual(GameSession.StartHp - 4 - battleLoss - 4, s.PlayerHp,
                $"R3 败 = 战败扣 {battleLoss} + 策略战后扣 4");
            var lastHpEvent = hp.Last(e => e.Delta < 0);
            Assert.AreEqual(-4, lastHpEvent.Delta, "策略战后扣血单独发一条 HpChangedEvent（表现层可区分'抽成'演出）");
        }

        // ─────────────────────── 钩子条目（表结构+管线阶段2 备好，内容阶段3） ───────────────────────

        /// <summary>记录型测试钩子：OnBattleStart 计数、SourceId 透传。</summary>
        private sealed class RecordHook : IBattleHook
        {
            public static int BattleStartCount;
            public int SourceId { get; }

            public RecordHook(int sourceId) { SourceId = sourceId; }

            public void OnBattleStart(BattleContext ctx) { BattleStartCount++; }
            public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft) { }
            public void OnAfterAttack(BattleContext ctx, in AttackResult result) { }
            public void OnDamaged(BattleContext ctx, in DamageResult result) { }
            public void OnDeath(BattleContext ctx, in DeathInfo death) { }
            public void OnTickEnd(BattleContext ctx) { }
        }

        [Test]
        public void PickPolicy_HookFactory_RegisteredFreshEachBattle()
        {
            RecordHook.BattleStartCount = 0;   // 静态计数：每条测试自重置（EditMode 串行执行）
            int factoryCalls = 0;
            int lastMemberCount = -1;

            // 工厂：每次调用计数 + 记录收到的名单人数 + 造一只记录钩子（SourceId 用实例章的形状值）
            IBattleHook Factory(IReadOnlyList<Unit> members)
            {
                factoryCalls++;
                lastMemberCount = members.Count;
                return new RecordHook(9001);
            }

            // 复合条目：效果（经验 +2）与钩子正交共存（同一条目既发效果又带钩子）
            var policies = new PolicyTable(new[]
            {
                Def(1, "钩子", new PolicyEffect(PolicyEffectType.Xp, 2, null, 0, Factory)),
            });
            var enemies = new EnemyTable(new[]
            {
                Round(1, Foe(30, 0, new Pos(4, 4), range: 5)),   // R1 奖励关（真打）
                Round(2, Foe(30, 0, new Pos(4, 4), range: 5)),   // R2 普通关（真打——钩子触发场）
            });
            var s = New(new EventBus(), Tables(SingleChar(hp: 500, atk: 100, range: 5), enemies, policies));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            Assert.IsTrue(s.PlaceOnBoard(s.Bench[0].Id, new Pos(4, 0)));
            PlayRound(s);   // R1 奖励关胜（钩子策略未选——工厂从未被调）
            Assert.IsTrue(s.StartPrep());
            Assert.AreEqual(0, factoryCalls, "选定时刻不实例化——留存的只是工厂");
            int xpBaseline = s.Eco.XpTotal;
            Assert.IsTrue(s.PickPolicy(0), "选钩子策略");
            Assert.AreEqual(xpBaseline + 2, s.Eco.XpTotal, "复合条目效果侧同步发放（经验 +2，与钩子正交）");

            // 第一场真打（R2 有怪）：工厂新造一只注册、OnBattleStart 在战斗内触发
            Assert.AreEqual(0, factoryCalls, "开战前工厂零调用");
            PlayRound(s);   // R2 普通关真打
            Assert.AreEqual(1, factoryCalls, "开战时工厂新造一只（留存工厂每场消费）");
            Assert.AreEqual(1, lastMemberCount, "传开战瞬间冻结的场上玩家名单（1 棋在场）");
            Assert.AreEqual(1, RecordHook.BattleStartCount, "钩子在真打起来的战斗里触发一次");

            // 第二场空编成秒胜：StartBattle 仍会注册（工厂再造一只）但构造即终局从不 Tick——钩子永不触发
            Assert.IsTrue(s.StartPrep());
            PlayRound(s);   // R3 空编成秒胜
            Assert.AreEqual(2, factoryCalls, "每场都新造（空场也注册——注册在 StartBattle，不挑战斗形态）");
            Assert.AreEqual(1, RecordHook.BattleStartCount, "空场秒胜不 Tick——OnBattleStart 永不触发（钩子骨架契约）");
        }
    }
}
