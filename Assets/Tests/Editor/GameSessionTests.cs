using System;
using System.Collections.Generic;
using System.Linq;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    /// <summary>
    /// 阶段2 批次5：GameSession 状态机 + EnterPrep 六步 + 备战期操作全链路 + 遭遇生成 + Settle 结算 + 评级 + 战斗种子。
    /// 分节契约（每条测试的断言消息自带语义）：
    ///  · 状态机：非法转移纯拒绝；拒绝时四不动（钱/池/名册/事件）；SettleBattle=#23 排空闸门回调的落点
    ///  · EnterPrep：回合开始广播 / 敌方退场 / 幸存者拉回（切比雪夫最近+扫描序先到）/ 死者原位复活+被占让位 / 免费上架 / 羁绊自愈
    ///  · 买卖：满席先于扣钱拒（D2-8=9）/ 退金 SellPrice / 份数折算 / 表外与未知 id 拒
    ///  · 上下场：板位=等级（#21⑤）/ 半场与占用拒 / 下场腾位
    ///  · 合成：凑 3 升星 / 格位继承 / 连锁 3 星 / 合成触发羁绊重算
    ///  · 遭遇：编成表生成（charId=0、star=1）/ 同格兜底首个空格 / 空编成构造即终局秒胜
    ///  · Settle：战败扣血 = DamageBase + 存活敌人数（2026-10-27 拍板；奖励关豁免 0；Boss 败清零判负）/
    ///    精英胜涨段败不涨 / 结算窗事件序 Income→Gold→(升级)→Xp→RoundEnded /
    ///    Boss 限时回血 35/10 封顶 100（#21⑨）/ 终局评级 S/A/B/C
    ///  · 种子（D2-6）：同种子同操作同战斗 / 种子从会话流抽取 / 战斗掷骰不耗会话流
    /// </summary>
    [TestFixture]
    public class GameSessionTests
    {
        // ─────────────────────── 测试基建：全可控、确定性的注入表 ───────────────────────

        /// <summary>概率表：全等级一费 100%（角色表只放 1 费角色 → 上架结果恒定可断言）。</summary>
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

        /// <summary>敌方速记：crit 恒 0（表数据纪律 D2-7）、攻速 1.3s 可覆盖。</summary>
        private static EnemySpawn Foe(int hp, int atk, Pos cell, float atkIv = 1.3f, int range = 1, string name = "怪")
            => new EnemySpawn(name, Stats(hp, atk, atkIv, 1f, range), cell, 0, 1);

        private static EnemyRoundEntry Round(int round, params EnemySpawn[] spawns)
            => new EnemyRoundEntry(round, spawns);

        /// <summary>N 只怪塞满敌方半场（血尽链构造；i%8 列、3+i/8 行恰好铺 24 格）。</summary>
        private static EnemySpawn[] Swarm(int count)
        {
            var list = new List<EnemySpawn>();
            for (int i = 0; i < count; i++)
                list.Add(Foe(30, 0, new Pos(i % 8, 3 + i / 8)));
            return list.ToArray();
        }

        /// <summary>注入包：角色表必给；编成默认空表（全回合空编成 = 构造即终局秒胜链）；回合/羁绊/矿池用 Default；
        /// 策略表给空表——奖励关胜后弹选拿 0 候选静默，既有链路不被弹选门禁/负面词条随机干扰（策略行为在 PolicyTests 锁）。</summary>
        private static GameTables Tables(CharacterTable chars, EnemyTable enemies = null)
            => new GameTables
            {
                Characters = chars,
                Odds = AllCostOne(),
                Rounds = RoundTable.Default,
                Enemies = enemies ?? EnemyTable.Empty,
                Synergies = SynergyTable.Default,
                Ores = OreTable.Default,
                Policies = new PolicyTable(new PolicyDef[0]),
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

        /// <summary>买指定角色（架上找，找不到刷新；固定 seed 下确定）。调用方负责注资（刷新 2 金/次）。</summary>
        private static void BuyChar(GameSession s, int charId)
        {
            int guard = 0;
            while (true)
            {
                Assert.Less(guard++, 200, $"架上找不到角色 {charId}——刷新异常多");
                int slot = Array.FindIndex(s.Shop.Offers, o => o.Id == charId);
                if (slot >= 0) { Assert.IsTrue(s.Buy(slot), $"买角色 {charId} 应成功"); return; }
                Assert.IsTrue(s.RefreshShop(), "注资后刷新必成功");
            }
        }

        /// <summary>买 n 张互不相同的角色（避开合成——合成会让席数不单调；满席测试的前置）。内部注资。</summary>
        private static void BuyDistinct(GameSession s, int n)
        {
            s.Eco.AddGold(200);   // 测试注资：覆盖买入 + 多轮刷新
            var got = new HashSet<int>();
            int guard = 0;
            while (got.Count < n)
            {
                Assert.Less(guard++, 400, "贪心买不同角色的循环异常长");
                int slot = -1;
                var offers = s.Shop.Offers;
                for (int i = 0; i < offers.Length; i++)
                    if (offers[i].Id != 0 && !got.Contains(offers[i].Id)) { slot = i; break; }
                if (slot < 0) { Assert.IsTrue(s.RefreshShop()); continue; }
                Assert.IsTrue(s.Buy(slot));
                got.Add(offers[slot].Id);
            }
        }

        /// <summary>打完当前回合（空编成时 StartBattle 已内联结算，驱动循环零次）。</summary>
        private static void PlayRound(GameSession s)
        {
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

        /// <summary>推进到目标回合的备战期（顺路打完途经回合）。</summary>
        private static void RunToPrep(GameSession s, int targetRound)
        {
            if (s.Phase == GamePhase.PreRun) Assert.IsTrue(s.StartRun());
            while (s.Round < targetRound)
            {
                PlayRound(s);
                Assert.IsTrue(s.StartPrep());
            }
        }

        /// <summary>从当前状态打到终局（Settlement 入口先推进备战再打）。</summary>
        private static void RunToEnd(GameSession s)
        {
            if (s.Phase == GamePhase.PreRun) Assert.IsTrue(s.StartRun());
            while (s.Phase != GamePhase.RunOver)
            {
                if (s.Phase == GamePhase.Settlement) { Assert.IsTrue(s.StartPrep()); continue; }
                PlayRound(s);
            }
        }

        private static int CountBenchOf(GameSession s) => s.Bench.Count;

        // ─────────────────────── 状态机：非法转移纯拒绝 ───────────────────────

        [Test]
        public void StartRun_FromPreRunOnly_EntersR1Prep()
        {
            var s = New(new EventBus(), Tables(SingleChar()));
            Assert.AreEqual(GamePhase.PreRun, s.Phase, "构造后停 PreRun");
            Assert.IsTrue(s.StartRun(), "PreRun → R1 备战应成功");
            Assert.AreEqual(GamePhase.Prep, s.Phase);
            Assert.AreEqual(1, s.Round);
            Assert.IsFalse(s.StartRun(), "Prep 期再 StartRun 必须纯拒绝");
            Assert.AreEqual(1, s.Round, "拒绝时回合不动");
        }

        [Test]
        public void PhaseGates_AllOpsRejectedInPreRun()
        {
            var s = New(new EventBus(), Tables(SingleChar()));
            Assert.IsFalse(s.StartBattle());
            Assert.IsFalse(s.StartPrep());
            Assert.IsFalse(s.SettleBattle());
            Assert.IsFalse(s.BuyXp());
            Assert.IsFalse(s.RefreshShop());
            Assert.IsFalse(s.Buy(0));
            Assert.IsFalse(s.Sell(1));
            Assert.IsFalse(s.PlaceOnBoard(1, new Pos(0, 0)));
            Assert.IsFalse(s.MoveOnBoard(1, new Pos(1, 0)));
            Assert.IsFalse(s.TakeOffBoard(1));
            Assert.IsFalse(s.PickOre(1), "PreRun 捡矿拒（批次6）");
            Assert.IsFalse(s.PickPolicy(0), "PreRun 选策略拒（批次7）");
            Assert.AreEqual(0, s.Bench.Count + s.PlayerBoard.Count + s.EnemyRoster.Count, "全部拒绝：名册不动");
            Assert.AreEqual(GameSession.StartGold, s.Eco.Gold, "全部拒绝：钱包不动");
        }

        [Test]
        public void PhaseGates_AllOpsRejectedInBattle()
        {
            var bus = new EventBus();
            // R1 一只互杀不死的怪 → 战斗持续，Battle 相位稳定可测
            var enemies = new EnemyTable(new[] { Round(1, Foe(10000, 0, new Pos(4, 4), atkIv: 1f)) });
            var s = New(bus, Tables(SingleChar(hp: 500, atk: 0, range: 5), enemies));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            Assert.IsTrue(s.PlaceOnBoard(1, new Pos(4, 0)));
            Assert.IsTrue(s.StartBattle());
            Assert.AreEqual(GamePhase.Battle, s.Phase);

            // 战斗期：全部备战期操作纯拒绝 + 未分晓时结算也拒（#23：结算等排空回调）
            Assert.IsFalse(s.BuyXp());
            Assert.IsFalse(s.RefreshShop());
            Assert.IsFalse(s.Buy(0));
            Assert.IsFalse(s.Sell(1));
            Assert.IsFalse(s.PlaceOnBoard(1, new Pos(1, 0)));
            Assert.IsFalse(s.MoveOnBoard(1, new Pos(1, 0)));
            Assert.IsFalse(s.TakeOffBoard(1));
            Assert.IsFalse(s.PickOre(1), "Battle 期捡矿拒（批次6）");
            Assert.IsFalse(s.PickPolicy(0), "Battle 期选策略拒（批次7）");
            Assert.IsFalse(s.SettleBattle(), "战斗未分晓（IsOver=false）时结算必须拒绝");
            Assert.AreEqual(GameSession.StartGold - 1, s.Eco.Gold, "战斗期买入被拒：钱包只有开局买棋那一笔");
        }

        [Test]
        public void StartBattle_RequiresPrepPhase()
        {
            var s = New(new EventBus(), Tables(SingleChar()));
            Assert.IsFalse(s.StartBattle(), "PreRun 拒");
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.StartBattle());
            Assert.IsFalse(s.StartBattle(), "已内联结算为 Settlement：再开战拒");
            Assert.IsFalse(s.StartBattle(), "Settlement 期开战拒（必须先 StartPrep）");
        }

        [Test]
        public void StartPrep_RequiresSettlementPhase()
        {
            var s = New(new EventBus(), Tables(SingleChar()));
            Assert.IsFalse(s.StartPrep(), "PreRun 拒");
            Assert.IsTrue(s.StartRun());
            Assert.IsFalse(s.StartPrep(), "Prep 拒");
            Assert.IsTrue(s.StartBattle());
            Assert.IsTrue(s.StartPrep(), "Settlement → 下一回合备战应成功");
            Assert.AreEqual(2, s.Round);
        }

        [Test]
        public void SettleBattle_RejectedUntilBattleOver()
        {
            // 互杀不死 → MaxTicks 兜底分晓；结算随即放行（一次性）
            var enemies = new EnemyTable(new[] { Round(1, Foe(10000, 0, new Pos(4, 4), atkIv: 1f)) });
            var s = New(new EventBus(), Tables(SingleChar(hp: 500, atk: 0, range: 5), enemies));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            Assert.IsTrue(s.PlaceOnBoard(1, new Pos(4, 0)));
            Assert.IsTrue(s.StartBattle());
            Assert.IsFalse(s.SettleBattle(), "未分晓前拒绝");
            int guard = 0;
            while (!s.Combat.IsOver && guard++ < 6000) s.Combat.Tick();
            Assert.IsTrue(s.Combat.IsOver, "MaxTicks 兜底必然分晓");
            Assert.IsTrue(s.SettleBattle(), "分晓后排空回调放行");
            Assert.AreEqual(GamePhase.Settlement, s.Phase);
            Assert.IsFalse(s.SettleBattle(), "Settlement 期再结算拒（一次性）");
            // R1 是奖励关：败北豁免不掉血
            Assert.AreEqual(GameSession.StartHp, s.PlayerHp);
        }

        // ─────────────────────── EnterPrep 六步 ───────────────────────

        [Test]
        public void StartRun_EntersR1Prep_BroadcastsAndRolls()
        {
            var bus = new EventBus();
            var started = new List<RoundStartedEvent>();
            var rolled = new List<ShopRolledEvent>();
            var synergies = new List<SynergiesChangedEvent>();
            var gold = new List<GoldChangedEvent>();
            bus.Subscribe<RoundStartedEvent>(e => started.Add(e));
            bus.Subscribe<ShopRolledEvent>(e => rolled.Add(e));
            bus.Subscribe<SynergiesChangedEvent>(e => synergies.Add(e));
            bus.Subscribe<GoldChangedEvent>(e => gold.Add(e));

            var s = New(bus, Tables(SingleChar()));
            Assert.IsTrue(s.StartRun());

            Assert.AreEqual(1, started.Count, "RoundStartedEvent 恰好一条");
            Assert.AreEqual(1, started[0].RoundNumber);
            Assert.AreEqual(RoundType.Reward, started[0].Type, "R1 = 奖励关");
            Assert.AreEqual(1, rolled.Count, "免费上架恰好一次");
            Assert.AreEqual(ShopSystem.Slots, rolled[0].CharIds.Length);
            for (int i = 0; i < rolled[0].CharIds.Length; i++)
                Assert.AreEqual(1, rolled[0].CharIds[i], "单角色表：5 格全是角色 1");
            Assert.AreEqual(GameSession.StartGold, s.Eco.Gold, "开局钱包 = 起始 3 金");
            Assert.AreEqual(0, gold.Count, "开局钱包不走 AddGold——无 GoldChangedEvent");
            Assert.AreEqual(1, synergies.Count, "羁绊首算广播全零快照");
            Assert.AreEqual(6, synergies[0].Snapshot.Length);
        }

        [Test]
        public void StartBattle_EmptyEncounter_InstantWinSettles()
        {
            var bus = new EventBus();
            var ended = new List<RoundEndedEvent>();
            var battleEnded = new List<BattleEndedEvent>();
            bus.Subscribe<RoundEndedEvent>(e => ended.Add(e));
            bus.Subscribe<BattleEndedEvent>(e => battleEnded.Add(e));

            var s = New(bus, Tables(SingleChar()));   // 空编成表
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));                  // 空场+空敌 = 判负；玩家须有人在场
            Assert.IsTrue(s.PlaceOnBoard(1, new Pos(4, 0)));
            Assert.IsTrue(s.StartBattle());
            Assert.AreEqual(GamePhase.Settlement, s.Phase, "构造即终局直接结算（战斗根本没发生）");
            Assert.AreEqual(0, battleEnded.Count, "空场战斗从不 Tick：BattleEndedEvent 永不广播");
            Assert.AreEqual(1, ended.Count);
            Assert.IsTrue(ended[0].PlayerWon, "敌方空编成 → 玩家胜利");
            Assert.AreEqual(0, ended[0].DamageTaken);
            Assert.AreEqual(Economy.BaseBattleIncome, ended[0].GoldGained, "固定段位 5");
        }

        [Test]
        public void StartBattle_SpawnsEnemiesFromTable()
        {
            var bus = new EventBus();
            var enemies = new EnemyTable(new[]
            {
                Round(1, Foe(50, 10, new Pos(3, 4)), Foe(60, 20, new Pos(5, 5), atkIv: 1.1f)),
            });
            var s = New(bus, Tables(SingleChar(hp: 500, atk: 0, range: 5), enemies));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            Assert.IsTrue(s.PlaceOnBoard(1, new Pos(4, 0)));
            Assert.IsTrue(s.StartBattle());

            var foe = s.EnemyRoster.ToArray();
            Assert.AreEqual(2, foe.Length, "按编成表生成两只");
            foreach (var u in foe)
            {
                Assert.AreEqual(0, u.CharId, "敌方 charId=0（不入角色表）");
                Assert.AreEqual(1, u.Star);
                Assert.IsTrue(u.IsOnBoard);
            }
            Assert.AreEqual(new Pos(3, 4), foe[0].Pos, "摆位 = 编成表写死");
            Assert.AreEqual(new Pos(5, 5), foe[1].Pos);
            Assert.AreEqual(GamePhase.Battle, s.Phase);
        }

        [Test]
        public void StartBattle_CellConflict_FallsBackToFirstFreeEnemyCell()
        {
            var bus = new EventBus();
            var enemies = new EnemyTable(new[]
            {
                Round(1, Foe(50, 0, new Pos(3, 4)), Foe(50, 0, new Pos(3, 4))),   // 同格编成
            });
            var s = New(bus, Tables(SingleChar(hp: 500, atk: 0, range: 5), enemies));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.StartBattle());
            var foe = s.EnemyRoster.ToArray();
            Assert.AreEqual(2, foe.Length, "兜底不丢怪");
            Assert.AreEqual(new Pos(3, 4), foe[0].Pos);
            Assert.AreEqual(new Pos(0, 3), foe[1].Pos, "第二只兜底到敌方半场首个空格（行主序 (0,3)）");
        }

        [Test]
        public void EnterPrep_EnemiesLeaveBoardAndRoster()
        {
            var bus = new EventBus();
            var removed = new List<UnitRemovedEvent>();
            bus.Subscribe<UnitRemovedEvent>(e => removed.Add(e));

            // R1 两只怪 + 玩家空场 → 构造即终局判负，敌方活着留在棋盘 → EnterPrep 整体退场
            var enemies = new EnemyTable(new[]
            {
                Round(1, Foe(500, 0, new Pos(3, 4)), Foe(500, 0, new Pos(5, 4))),
            });
            var s = New(bus, Tables(SingleChar(), enemies));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.StartBattle());
            Assert.AreEqual(2, s.EnemyRoster.Count, "战后敌方还在名册");

            Assert.IsTrue(s.StartPrep());
            Assert.AreEqual(0, s.EnemyRoster.Count, "敌方整体退场（名册清空）");
            Assert.AreEqual(2, removed.Count, "两只各发一条 UnitRemovedEvent");
            Assert.IsNull(s.Board.GetOccupant(new Pos(3, 4)), "棋盘敌半场清空");
            Assert.IsNull(s.Board.GetOccupant(new Pos(5, 4)));
        }

        [Test]
        public void EnterPrep_SurvivorReturnsToPlayerHalf()
        {
            var bus = new EventBus();
            // 近战棋越境击杀：range 1 → 走到 (4,3)（敌方半场）才够得着 (4,4)
            var enemies = new EnemyTable(new[] { Round(1, Foe(50, 0, new Pos(4, 4), atkIv: 1f)) });
            var s = New(bus, Tables(SingleChar(hp: 500, atk: 100, range: 1), enemies));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            Assert.IsTrue(s.PlaceOnBoard(1, new Pos(4, 2)));
            Assert.IsTrue(s.StartBattle());
            int guard = 0;
            while (!s.Combat.IsOver && guard++ < 6000) s.Combat.Tick();
            Assert.IsTrue(s.SettleBattle(), "分晓后结算");
            Assert.IsTrue(s.Combat.PlayerWon);
            var survivor = s.PlayerBoard.Single();
            Assert.AreEqual(new Pos(4, 3), survivor.Pos, "战后越境在敌方半场（走到贴脸才击杀）");

            Assert.IsTrue(s.StartPrep());
            Assert.AreEqual(new Pos(3, 2), survivor.Pos,
                "拉回切比雪夫最近空格；(3,2) 距 1 且扫描序先到（先于 (4,2)）——与索敌同一套确定性规则");
            Assert.LessOrEqual(survivor.Pos.Y, BoardData.PlayerHalfRows - 1, "回到玩家半场");
            Assert.IsTrue(survivor.IsOnBoard);
        }

        [Test]
        public void EnterPrep_DeadRevivesAtOriginalCell()
        {
            var bus = new EventBus();
            var restored = new List<UnitRestoredEvent>();
            bus.Subscribe<UnitRestoredEvent>(e => restored.Add(e));

            // 恶狼（先手半秒一击、200 伤）先杀 30 血的 A；B 远程 t30 一击杀狼
            var enemies = new EnemyTable(new[] { Round(1, Foe(50, 200, new Pos(4, 4), atkIv: 0.5f, range: 5, name: "恶狼")) });
            var chars = new CharacterTable(new[]
            {
                Char(1, 30, 0, range: 1),                          // A：炮灰，站在狼的索敌最近处
                Char(2, 500, 100, atkIv: 1f, range: 5),           // B：远程输出
            });
            var s = New(bus, Tables(chars, enemies));
            s.Eco.AddGold(50);   // 注资：BuyChar 的刷新循环需要
            Assert.IsTrue(s.StartRun());
            BuyChar(s, 1);
            BuyChar(s, 2);
            var a = s.Bench.First(u => u.CharId == 1);
            var b = s.Bench.First(u => u.CharId == 2);
            Assert.IsTrue(s.PlaceOnBoard(a.Id, new Pos(4, 2)));
            Assert.IsTrue(s.PlaceOnBoard(b.Id, new Pos(3, 1)));

            Assert.IsTrue(s.StartBattle());
            int guard = 0;
            while (!s.Combat.IsOver && guard++ < 6000) s.Combat.Tick();
            Assert.IsTrue(s.SettleBattle(), "分晓后结算");
            Assert.IsTrue(s.Combat.PlayerWon);
            Assert.IsFalse(a.IsAlive, "A 战斗中阵亡");

            Assert.IsTrue(s.StartPrep());
            Assert.IsTrue(a.IsAlive, "上一场参战者死亡后复活");
            Assert.IsTrue(a.IsOnBoard);
            Assert.AreEqual(new Pos(4, 2), a.Pos, "死者优先原位复活");
            Assert.AreEqual(30, a.CurrentHp, "复活即满血（RestoreFullHp）");
            Assert.AreEqual(500, b.CurrentHp, "幸存者同样满血归位");
            Assert.AreEqual(2, restored.Count, "A、B 各一条恢复事件");
        }

        [Test]
        public void EnterPrep_DeadDisplacedToNearestFreeCell()
        {
            var bus = new EventBus();
            // 恶狼 t15 杀 A(4,2)；B 走到 (4,2)（A 的死格）再 t60 杀狼 → 复活时 A 原位被 B 占
            var enemies = new EnemyTable(new[] { Round(1, Foe(50, 200, new Pos(4, 4), atkIv: 0.5f, range: 5, name: "恶狼")) });
            var chars = new CharacterTable(new[]
            {
                Char(1, 30, 0, range: 1),
                Char(2, 500, 200, atkIv: 1f, moveIv: 1f, range: 2),
            });
            var s = New(bus, Tables(chars, enemies));
            s.Eco.AddGold(50);
            Assert.IsTrue(s.StartRun());
            BuyChar(s, 1);
            BuyChar(s, 2);
            var a = s.Bench.First(u => u.CharId == 1);
            var b = s.Bench.First(u => u.CharId == 2);
            Assert.IsTrue(s.PlaceOnBoard(a.Id, new Pos(4, 2)));
            Assert.IsTrue(s.PlaceOnBoard(b.Id, new Pos(4, 1)));

            Assert.IsTrue(s.StartBattle());
            int guard = 0;
            while (!s.Combat.IsOver && guard++ < 6000) s.Combat.Tick();
            Assert.IsTrue(s.SettleBattle(), "分晓后结算");
            Assert.IsTrue(s.Combat.PlayerWon);
            Assert.AreEqual(new Pos(4, 2), b.Pos, "B 走进了 A 的死格");

            Assert.IsTrue(s.StartPrep());
            Assert.IsTrue(a.IsAlive);
            Assert.AreEqual(new Pos(4, 2), b.Pos, "幸存者已在自己半场不动");
            Assert.AreEqual(new Pos(3, 1), a.Pos,
                "死者原位被占 → 让位到切比雪夫最近空格（并列取扫描序先到 = (3,1)）");
        }

        [Test]
        public void EnterPrep_FreeRoll_NoGoldCharge()
        {
            var s = New(new EventBus(), Tables(SingleChar()));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.StartBattle());   // R1 空编成 → 内联结算
            Assert.AreEqual(GamePhase.Settlement, s.Phase);
            int goldAfterSettle = s.Eco.Gold;

            Assert.IsTrue(s.StartPrep());
            Assert.AreEqual(goldAfterSettle, s.Eco.Gold, "进备战免费上架——不再扣钱");
            Assert.AreEqual(GamePhase.Prep, s.Phase);
            Assert.AreEqual(2, s.Round);
        }

        // ─────────────────────── 买卖与买经验 ───────────────────────

        [Test]
        public void BuyXp_GatedByPhase_AndCharges()
        {
            var s = New(new EventBus(), Tables(SingleChar()));
            Assert.IsFalse(s.BuyXp(), "PreRun 拒");
            Assert.IsTrue(s.StartRun());
            Assert.AreEqual(2, s.Eco.Level, "开局 2 级");
            s.Eco.AddGold(10);   // 开局 3 金买不起 4 金经验——测试注资

            Assert.IsTrue(s.BuyXp(), "备战期买经验成功");
            Assert.AreEqual(GameSession.StartGold + 10 - Economy.XpCost, s.Eco.Gold, "注资 10 后扣 4 金");
            Assert.AreEqual(Economy.XpPerBuy, s.Eco.XpTotal);
            Assert.AreEqual(3, s.Eco.Level, "4 XP 达 L3 门槛（#21⑤ 修订版）");
        }

        [Test]
        public void Buy_AcquiresIntoBench_Events()
        {
            var bus = new EventBus();
            var acquired = new List<UnitAcquiredEvent>();
            var bought = new List<ShopBoughtEvent>();
            bus.Subscribe<UnitAcquiredEvent>(e => acquired.Add(e));
            bus.Subscribe<ShopBoughtEvent>(e => bought.Add(e));

            var s = New(bus, Tables(SingleChar()));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));

            Assert.AreEqual(1, s.Bench.Count, "入名册");
            var u = s.Bench[0];
            Assert.AreEqual(1, u.CharId);
            Assert.AreEqual(1, u.Star);
            Assert.IsFalse(u.IsOnBoard, "买入落备战席");
            Assert.AreEqual(GameSession.StartGold - 1, s.Eco.Gold);
            Assert.AreEqual(1, acquired.Count, "名册事实广播恰一条");
            Assert.AreEqual(u.Id, acquired[0].UnitId);
            Assert.AreEqual(1, bought.Count, "商店事实在名册事实之前");
        }

        [Test]
        public void Buy_BenchFull_RejectedBeforeCharge()
        {
            var bus = new EventBus();
            var acquired = new List<UnitAcquiredEvent>();
            bus.Subscribe<UnitAcquiredEvent>(e => acquired.Add(e));

            // 9 角色表 + 贪心买 9 张互不相同（避开合成）→ 席满 9（D2-8 拍板值）
            var chars = new CharacterTable(Enumerable.Range(1, 9).Select(i => Char(i, 500, 100)).ToArray());
            var s = New(bus, Tables(chars));
            Assert.IsTrue(s.StartRun());
            BuyDistinct(s, GameSession.BenchCapacity);
            Assert.AreEqual(GameSession.BenchCapacity, CountBenchOf(s));
            int acquiredCount = acquired.Count;

            int goldBefore = s.Eco.Gold;
            int anySlot = Array.FindIndex(s.Shop.Offers, o => o.Id != 0);
            if (anySlot >= 0)
            {
                Assert.IsFalse(s.Buy(anySlot), "满员买入纯拒绝");
                Assert.AreEqual(goldBefore, s.Eco.Gold, "余位检查先于扣钱：钱包不动");
                Assert.AreEqual(acquiredCount, acquired.Count, "名册事实不发");
            }
        }

        [Test]
        public void Sell_OneStar_RefundsSellPrice()
        {
            var bus = new EventBus();
            var sold = new List<UnitSoldEvent>();
            bus.Subscribe<UnitSoldEvent>(e => sold.Add(e));

            var s = New(bus, Tables(SingleChar()));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            var u = s.Bench[0];
            int goldBeforeSell = s.Eco.Gold;

            Assert.IsTrue(s.Sell(u.Id));
            Assert.AreEqual(0, s.Bench.Count + s.PlayerBoard.Count, "名册移除");
            Assert.AreEqual(goldBeforeSell + 1, s.Eco.Gold, "1 星退金 = 1×1 - 0 = 1");
            Assert.AreEqual(1, sold.Count);
            Assert.AreEqual(1, sold[0].Gold);
            Assert.AreEqual(1, sold[0].Star);
        }

        [Test]
        public void Sell_TwoStar_SellPriceAccountsCopies()
        {
            // 买 3 张合 2 星再卖：SellPrice(2) = 3×1 - 1 = 2（总投入-(星级-1)，升星折损）
            var s = New(new EventBus(), Tables(SingleChar()));
            Assert.IsTrue(s.StartRun());
            BuyAny(s);
            BuyAny(s);
            BuyAny(s);
            var merged = s.Bench.Single();
            Assert.AreEqual(2, merged.Star);

            Assert.IsTrue(s.Sell(merged.Id));
            Assert.AreEqual(0, s.Bench.Count + s.PlayerBoard.Count);
            Assert.AreEqual(GameSession.StartGold - 3 + 2, s.Eco.Gold, "2 星退金 2（SellPrice 含份数折算）");
        }

        [Test]
        public void Sell_OnBoard_RemovedFromBoard()
        {
            var bus = new EventBus();
            var removed = new List<UnitRemovedEvent>();
            bus.Subscribe<UnitRemovedEvent>(e => removed.Add(e));

            var s = New(bus, Tables(SingleChar()));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            var u = s.Bench[0];
            Assert.IsTrue(s.PlaceOnBoard(u.Id, new Pos(0, 0)));

            Assert.IsTrue(s.Sell(u.Id));
            Assert.AreEqual(0, s.Bench.Count + s.PlayerBoard.Count, "名册移除");
            Assert.AreEqual(1, removed.Count, "场上卖先移出棋盘");
            Assert.AreEqual(u.Id, removed[0].UnitId);
            Assert.IsNull(s.Board.GetOccupant(new Pos(0, 0)));
        }

        [Test]
        public void Sell_UnknownUnit_Rejected()
        {
            var s = New(new EventBus(), Tables(SingleChar()));
            Assert.IsTrue(s.StartRun());
            Assert.IsFalse(s.Sell(999), "未知 unitId 纯拒绝");
            Assert.AreEqual(GameSession.StartGold, s.Eco.Gold, "拒绝：钱包不动");
        }

        // ─────────────────────── 上下场与板位 ───────────────────────

        [Test]
        public void PlaceOnBoard_LimitedByLevel()
        {
            var chars = new CharacterTable(Enumerable.Range(1, 3).Select(i => Char(i, 500, 100)).ToArray());
            var s = New(new EventBus(), Tables(chars));
            Assert.IsTrue(s.StartRun());
            BuyDistinct(s, 3);
            var bench = s.Bench.ToArray();
            Assert.AreEqual(3, bench.Length);

            Assert.IsTrue(s.PlaceOnBoard(bench[0].Id, new Pos(0, 0)), "L2 板位 2：第 1 张上场");
            Assert.IsTrue(s.PlaceOnBoard(bench[1].Id, new Pos(1, 0)), "第 2 张上场");
            Assert.IsFalse(s.PlaceOnBoard(bench[2].Id, new Pos(2, 0)), "板位 = 等级，L2 第 3 张拒绝");

            Assert.IsTrue(s.BuyXp(), "买经验升 L3（板位 3）");
            Assert.AreEqual(3, s.Eco.Level);
            Assert.IsTrue(s.PlaceOnBoard(bench[2].Id, new Pos(2, 0)), "板位扩容后放行");
            Assert.AreEqual(3, s.Board.CountOnBoard(true));
        }

        [Test]
        public void PlaceOnBoard_HalfAndOccupied_Rejected()
        {
            var s = New(new EventBus(), Tables(SingleChar()));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            var u = s.Bench[0];

            Assert.IsFalse(s.PlaceOnBoard(u.Id, new Pos(0, 3)), "敌方半场拒");
            Assert.IsFalse(s.PlaceOnBoard(u.Id, new Pos(99, 0)), "越界拒");
            Assert.IsFalse(s.PlaceOnBoard(u.Id, new Pos(8, 0)), "越界拒（棋盘宽 8）");
            Assert.IsFalse(s.PlaceOnBoard(999, new Pos(0, 0)), "未知 unitId 拒");
            Assert.IsTrue(s.PlaceOnBoard(u.Id, new Pos(0, 0)));
            BuyAny(s);
            var u2 = s.Bench.First(x => x != u);
            Assert.IsFalse(s.PlaceOnBoard(u2.Id, new Pos(0, 0)), "已占用格拒");
            Assert.IsTrue(s.PlaceOnBoard(u2.Id, new Pos(1, 0)));
        }

        [Test]
        public void MoveOnBoard_WithinPlayerHalfOnly()
        {
            var s = New(new EventBus(), Tables(SingleChar()));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            var u = s.Bench[0];
            Assert.IsTrue(s.PlaceOnBoard(u.Id, new Pos(0, 0)));

            Assert.IsFalse(s.MoveOnBoard(u.Id, new Pos(0, 3)), "备战拖拽不能越进敌方半场");
            Assert.IsFalse(s.MoveOnBoard(u.Id, new Pos(5, 5)), "越境拒");
            Assert.IsFalse(s.MoveOnBoard(999, new Pos(1, 0)), "未知 id 拒");
            Assert.IsTrue(s.MoveOnBoard(u.Id, new Pos(1, 0)), "己方半场内挪位成功");
            Assert.AreEqual(new Pos(1, 0), u.Pos);

            Assert.IsTrue(s.TakeOffBoard(u.Id), "下场成功");
            Assert.IsFalse(u.IsOnBoard);
            Assert.IsFalse(s.MoveOnBoard(u.Id, new Pos(2, 0)), "不在板上没得挪");
        }

        [Test]
        public void TakeOffBoard_FreesSlot_AllowsReplace()
        {
            var chars = new CharacterTable(Enumerable.Range(1, 3).Select(i => Char(i, 500, 100)).ToArray());
            var s = New(new EventBus(), Tables(chars));
            Assert.IsTrue(s.StartRun());
            BuyDistinct(s, 3);
            var bench = s.Bench.ToArray();
            Assert.IsTrue(s.PlaceOnBoard(bench[0].Id, new Pos(0, 0)));
            Assert.IsTrue(s.PlaceOnBoard(bench[1].Id, new Pos(1, 0)));
            Assert.IsFalse(s.PlaceOnBoard(bench[2].Id, new Pos(2, 0)), "板位满");

            Assert.IsTrue(s.TakeOffBoard(bench[0].Id), "下场腾位");
            Assert.AreEqual(1, s.Board.CountOnBoard(true));
            Assert.IsTrue(s.PlaceOnBoard(bench[2].Id, new Pos(2, 0)), "腾出的板位可再上场");
            Assert.IsFalse(s.TakeOffBoard(999), "未知 id 拒");
        }

        // ─────────────────────── 合成 ───────────────────────

        [Test]
        public void Merge_ThreeCopies_MakeTwoStar()
        {
            var bus = new EventBus();
            var merged = new List<UnitMergedEvent>();
            bus.Subscribe<UnitMergedEvent>(e => merged.Add(e));

            var s = New(bus, Tables(SingleChar(hp: 100, atk: 40)));
            Assert.IsTrue(s.StartRun());
            BuyAny(s);
            BuyAny(s);
            Assert.AreEqual(2, s.Bench.Count, "两张不合成");

            BuyAny(s);
            Assert.AreEqual(1, s.Bench.Count, "第三张凑满 → 合 1");
            Assert.AreEqual(1, merged.Count);
            var result = s.Bench[0];
            Assert.AreEqual(2, result.Star);
            Assert.AreEqual(2, merged[0].NewStar);
            Assert.AreEqual(3, merged[0].ConsumedUnitIds.Length);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, merged[0].ConsumedUnitIds, "名册序（买入顺序）消费");
            Assert.AreEqual(result.Id, merged[0].ResultUnitId);
            // StatsForStar(2)：hp ×1.8 = 180、atk ×1.5 = 60（星级只放大量与攻）
            Assert.AreEqual(180, result.CurrentHp, "合成体满血出厂");
            Assert.AreEqual(60, result.EffectiveStats.AttackPower);
        }

        [Test]
        public void Merge_InheritsBoardCell()
        {
            var bus = new EventBus();
            var mergedEvents = new List<UnitMergedEvent>();
            bus.Subscribe<UnitMergedEvent>(e => mergedEvents.Add(e));

            var s = New(bus, Tables(SingleChar()));
            Assert.IsTrue(s.StartRun());
            BuyAny(s);   // id1 → 上场
            var first = s.Bench[0];
            Assert.IsTrue(s.PlaceOnBoard(first.Id, new Pos(0, 0)));
            BuyAny(s);   // id2 → 备战
            BuyAny(s);   // id3 → 备战；场上 1 + 备战 2 → 锚点=场上，材料=备战两张

            Assert.AreEqual(1, s.PlayerBoard.Count, "三张并一张：场上锚点原位升星");
            Assert.AreEqual(0, s.Bench.Count, "两张材料全部从备战席消耗（场上零损耗）");
            var mergedUnit = s.PlayerBoard[0];
            Assert.IsTrue(mergedUnit.IsOnBoard, "合成体继承在架状态");
            Assert.AreEqual(new Pos(0, 0), mergedUnit.Pos, "锚点（场上首张）格位保留");
            Assert.AreSame(mergedUnit, s.Board.GetOccupant(new Pos(0, 0)), "格位占用账本一致");
            Assert.AreEqual(1, mergedEvents.Count);
            // 消费顺序：锚点 + 材料（备战优先；id2/id3 是后买的两张备战材料）
            CollectionAssert.AreEqual(new[] { first.Id, 2, 3 }, mergedEvents[0].ConsumedUnitIds,
                "锚点在前，材料按备战序");
        }

        [Test]
        public void Merge_UsesBoardMaterialOnlyWhenBenchInsufficient()
        {
            var bus = new EventBus();
            var mergedEvents = new List<UnitMergedEvent>();
            bus.Subscribe<UnitMergedEvent>(e => mergedEvents.Add(e));

            var s = New(bus, Tables(SingleChar(hp: 100, atk: 40)));
            Assert.IsTrue(s.StartRun());
            s.Eco.AddGold(50);
            BuyAny(s);   // id1
            BuyAny(s);   // id2
            Assert.IsTrue(s.PlaceOnBoard(1, new Pos(0, 0)));
            Assert.IsTrue(s.PlaceOnBoard(2, new Pos(1, 0)));   // 场上 2 张（L2 板位满）

            BuyAny(s);   // id3：总 3 张 → 锚点=id1（场上首），材料=id3（备战）+ id2（场上——备战不足才补）

            Assert.AreEqual(1, s.PlayerBoard.Count, "场上 3 并 1");
            Assert.AreEqual(0, s.Bench.Count);
            var result = s.PlayerBoard[0];
            Assert.AreEqual(2, result.Star);
            Assert.AreEqual(new Pos(0, 0), result.Pos, "锚点格位保留（(1,0) 上的 id2 被消耗，格腾空）");
            Assert.IsNull(s.Board.GetOccupant(new Pos(1, 0)), "被消耗的场上材料让出格位");
            CollectionAssert.AreEqual(new[] { 1, 3, 2 }, mergedEvents[0].ConsumedUnitIds,
                "消费顺序：锚点 → 备战材料 → 场上材料（材料永远备战优先）");
        }

        [Test]
        public void Merge_BenchOnly_DoesNotRecalculate()
        {
            var bus = new EventBus();
            var synergyEvents = new List<SynergiesChangedEvent>();
            bus.Subscribe<SynergiesChangedEvent>(e => synergyEvents.Add(e));

            // id1/id2 带蛮兽标签（上场激活 2 件档）；id3 无标签（备战合成用，不干扰计数）
            var chars = new CharacterTable(new[]
            {
                Char(1, 500, 100, tags: 1),
                Char(2, 500, 100, tags: 1),
                Char(3, 500, 100),
            });
            var s = New(bus, Tables(chars));
            s.Eco.AddGold(50);
            Assert.IsTrue(s.StartRun());
            BuyChar(s, 1);
            BuyChar(s, 2);
            Assert.IsTrue(s.PlaceOnBoard(s.Bench.First(u => u.CharId == 1).Id, new Pos(0, 0)));
            Assert.IsTrue(s.PlaceOnBoard(s.Bench.First(u => u.CharId == 2).Id, new Pos(1, 0)));
            Assert.AreEqual(0, SynergyTierIndexOf(s, 1), "场上两个蛮兽 → 2 件档激活");
            int eventsAfterActivate = synergyEvents.Count;

            // 购买 + 备战席合成：场上成员没变 → 不重算（无事件、快照不动）
            BuyChar(s, 3);
            BuyChar(s, 3);
            BuyChar(s, 3);
            Assert.AreEqual(1, s.Bench.Count(u => u.CharId == 3 && u.Star == 2), "备战席合成 2 星");
            Assert.AreEqual(eventsAfterActivate, synergyEvents.Count, "仅备战席的合成不触发重算");
            Assert.AreEqual(2, SynergyCountOf(s, 1), "快照计数不动");
        }

        [Test]
        public void Buy_BenchOnly_NoRecalc_BenchNoMods_BoardKeepsMods()
        {
            var bus = new EventBus();
            var synergyEvents = new List<SynergiesChangedEvent>();
            bus.Subscribe<SynergiesChangedEvent>(e => synergyEvents.Add(e));

            var chars = new CharacterTable(new[]
            {
                Char(1, 500, 100, tags: 1),
                Char(2, 500, 100, tags: 1),
                Char(3, 500, 100, tags: 1),
            });
            var s = New(bus, Tables(chars));
            s.Eco.AddGold(50);
            Assert.IsTrue(s.StartRun());
            BuyChar(s, 1);
            BuyChar(s, 2);
            Assert.IsTrue(s.PlaceOnBoard(s.Bench.First(u => u.CharId == 1).Id, new Pos(0, 0)));
            Assert.IsTrue(s.PlaceOnBoard(s.Bench.First(u => u.CharId == 2).Id, new Pos(1, 0)));
            var onBoard1 = s.PlayerBoard.First(u => u.CharId == 1);
            Assert.AreEqual(0.88f, onBoard1.EffectiveStats.AttackInterval, 0.001f, "场上激活 2 件蛮兽：攻速 +12%");
            int eventsAfterActivate = synergyEvents.Count;

            // 购买只进备战席：不重算、备战席不挂任何修饰器
            BuyChar(s, 3);
            var benchUnit = s.Bench.Single(u => u.CharId == 3);
            Assert.AreEqual(eventsAfterActivate, synergyEvents.Count, "购买不触发重算");
            Assert.AreEqual(1f, benchUnit.EffectiveStats.AttackInterval, 0.0001f, "备战席不享受羁绊加成");
            Assert.AreEqual(0.88f, onBoard1.EffectiveStats.AttackInterval, 0.001f, "场上单位加成不受购买影响");

            // 下场即摘干净、重新上场补挂（备战席只摘不挂的闭环）
            Assert.IsTrue(s.TakeOffBoard(onBoard1.Id));
            Assert.AreEqual(1, SynergyCountOf(s, 1), "下场后场上只剩 1 蛮兽");
            Assert.AreEqual(1f, onBoard1.EffectiveStats.AttackInterval, 0.0001f, "下场即摘干净（备战席不享受）");
            Assert.IsTrue(s.PlaceOnBoard(onBoard1.Id, new Pos(0, 0)));
            Assert.AreEqual(0.88f, onBoard1.EffectiveStats.AttackInterval, 0.001f, "重新上场补挂");

            // 备战席卖出同样不重算
            int eventsBeforeSell = synergyEvents.Count;
            Assert.IsTrue(s.Sell(benchUnit.Id));
            Assert.AreEqual(eventsBeforeSell, synergyEvents.Count, "备战席卖出不触发重算");
        }

        [Test]
        public void Merge_ChainsToThreeStar()
        {
            var bus = new EventBus();
            var merged = new List<UnitMergedEvent>();
            bus.Subscribe<UnitMergedEvent>(e => merged.Add(e));

            var s = New(bus, Tables(SingleChar(hp: 100, atk: 40)));
            Assert.IsTrue(s.StartRun());
            s.Eco.AddGold(50);   // 9 张买入 + 刷新资金
            for (int i = 0; i < 9; i++) BuyAny(s);

            Assert.AreEqual(1, s.Bench.Count, "9 张一路连锁成 1 张");
            var result = s.Bench[0];
            Assert.AreEqual(3, result.Star);
            Assert.AreEqual(4, merged.Count, "3 次 2 星合成 + 1 次 3 星合成");
            CollectionAssert.AreEqual(new[] { 2, 2, 2, 3 }, merged.Select(m => m.NewStar).ToArray());
            Assert.AreEqual(324, result.CurrentHp, "StatsForStar(3)：100 × 1.8² = 324（复利，取整一次）");
        }

        [Test]
        public void Merge_TriggersSynergyRecalc()
        {
            var bus = new EventBus();
            var synergyEvents = new List<SynergiesChangedEvent>();
            bus.Subscribe<SynergiesChangedEvent>(e => synergyEvents.Add(e));

            // 两个不同角色都带蛮兽标签（同 CharId 去重——单角色两张只计 1 件）：
            // 买 id1 ×3 合成 2 星（备战席、计数 0 无激活）→ 上场合成体 + 买 id2 上场 → 计数 2 → 激活 2 件档；
            // 合成体是全新 Unit（出厂无修饰器）——重算必须把 2 件档攻速 +12% 补挂到它身上
            var chars = new CharacterTable(new[]
            {
                Char(1, 500, 100, tags: 1),
                Char(2, 500, 100, tags: 1),
            });
            var s = New(bus, Tables(chars));
            s.Eco.AddGold(50);
            Assert.IsTrue(s.StartRun());
            BuyChar(s, 1);
            BuyChar(s, 1);
            BuyChar(s, 1);   // 凑 3 → 合成 2 星
            var merged = s.Bench.Single();
            Assert.AreEqual(2, merged.Star);
            Assert.AreEqual(1f, merged.EffectiveStats.AttackInterval, 0.0001f,
                "合成后、上场前：场上计数 0，合成体尚无羁绊修饰器");

            Assert.IsTrue(s.PlaceOnBoard(merged.Id, new Pos(0, 0)), "合成体上场（计数 1）");
            BuyChar(s, 2);
            var second = s.Bench.First(u => u.CharId == 2);
            Assert.IsTrue(s.PlaceOnBoard(second.Id, new Pos(1, 0)), "第二棋子上场（计数 2 → 激活）");

            Assert.AreEqual(0, SynergyTierIndexOf(s, 1), "两个不同蛮兽棋子 → 2 件档");
            Assert.AreEqual(0.88f, merged.EffectiveStats.AttackInterval, 0.001f,
                "合成体被重算补挂 2 件档攻速 +12%（1.0 × 0.88）——新 Unit 不吃暗亏");
            Assert.AreEqual(0.88f, second.EffectiveStats.AttackInterval, 0.001f);
            Assert.GreaterOrEqual(synergyEvents.Count, 2, "首算 + 升档至少两次变化广播");
        }

        private static int SynergyTierIndexOf(GameSession s, int synergyId)
            => s.Synergies.First(x => x.SynergyId == synergyId).TierIndex;

        private static int SynergyCountOf(GameSession s, int synergyId)
            => s.Synergies.First(x => x.SynergyId == synergyId).Count;

        // ─────────────────────── Settle 结算 ───────────────────────

        [Test]
        public void Settle_NormalLoss_BasePlusSurvivors()
        {
            var bus = new EventBus();
            var hp = new List<HpChangedEvent>();
            var ended = new List<RoundEndedEvent>();
            bus.Subscribe<HpChangedEvent>(e => hp.Add(e));
            bus.Subscribe<RoundEndedEvent>(e => ended.Add(e));

            // R2 两只怪 + 玩家空场败：扣 = DamageBase(3) + 存活敌人数(2) = 5（2026-10-27 拍板公式）
            var enemies = new EnemyTable(new[] { Round(2, Foe(500, 0, new Pos(3, 4)), Foe(500, 0, new Pos(5, 4))) });
            var s = New(bus, Tables(SingleChar(), enemies));
            RunToPrep(s, 2);
            Assert.IsTrue(s.StartBattle());   // 空场 → 构造即终局判负（内联结算）

            Assert.AreEqual(GameSession.StartHp - 5, s.PlayerHp, "DamageBase + 存活数");
            Assert.AreEqual(1, hp.Count);
            Assert.AreEqual(-5, hp[0].Delta);
            var r2 = ended.Last();
            Assert.AreEqual(2, r2.Round);
            Assert.IsFalse(r2.PlayerWon);
            Assert.AreEqual(5, r2.DamageTaken);
            Assert.AreEqual(Economy.BaseBattleIncome, r2.GoldGained, "败场也有固定段位收入（金铲铲口径）");
        }

        [Test]
        public void Settle_RewardLoss_ZeroDamage()
        {
            var bus = new EventBus();
            var hp = new List<HpChangedEvent>();
            bus.Subscribe<HpChangedEvent>(e => hp.Add(e));

            // R1 奖励关两只怪 + 空场败：豁免条款——奖励关败不掉血
            var enemies = new EnemyTable(new[] { Round(1, Foe(500, 0, new Pos(3, 4)), Foe(500, 0, new Pos(5, 4))) });
            var s = New(bus, Tables(SingleChar(), enemies));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.StartBattle());

            Assert.AreEqual(GameSession.StartHp, s.PlayerHp, "奖励关败不掉血");
            Assert.AreEqual(0, hp.Count, "无扣血事件");
        }

        [Test]
        public void Settle_EliteWin_PromotesIncomeTier()
        {
            var bus = new EventBus();
            var bills = new List<IncomeSettledEvent>();
            bus.Subscribe<IncomeSettledEvent>(e => bills.Add(e));

            // R6 一只弱怪（玩家远程棋 t30 一击杀）；其余回合空编成秒胜
            var enemies = new EnemyTable(new[] { Round(6, Foe(50, 0, new Pos(4, 4))) });
            var s = New(bus, Tables(SingleChar(hp: 500, atk: 100, range: 5), enemies));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            Assert.IsTrue(s.PlaceOnBoard(1, new Pos(4, 0)));

            RunToEnd(s);
            Assert.IsTrue(s.RunOver, "14 回合全胜通关");

            var fixedGoldByRound = bills.Select(b => b.Bill.FixedGold).ToArray();
            Assert.AreEqual(14, fixedGoldByRound.Length, "每场结算一条分账");
            for (int i = 0; i < 6; i++)
                Assert.AreEqual(5, fixedGoldByRound[i], $"R{i + 1} 固定段位 5（R6 场本身仍按旧段位入账）");
            for (int i = 6; i < 11; i++)
                Assert.AreEqual(6, fixedGoldByRound[i], $"R{i + 1} 起段位 +1（精英 R6 已通关）");
            for (int i = 11; i < 14; i++)
                Assert.AreEqual(7, fixedGoldByRound[i], $"R{i + 1} 起段位再 +1（R11 也是精英关：空编成秒胜同样算通关涨段）");
        }

        [Test]
        public void Settle_EliteLoss_TierStays()
        {
            var bus = new EventBus();
            var bills = new List<IncomeSettledEvent>();
            bus.Subscribe<IncomeSettledEvent>(e => bills.Add(e));

            // R6 一只强怪（玩家空场败）→ 精英败：档位不涨（#21⑨）
            var enemies = new EnemyTable(new[] { Round(6, Foe(5000, 200, new Pos(4, 4), range: 5)) });
            var s = New(bus, Tables(SingleChar(), enemies));
            RunToPrep(s, 6);
            Assert.IsTrue(s.StartBattle());   // 空场败（内联结算）

            RunToEnd(s);   // R7~R14 空编成秒胜打完
            var fixedGold = bills.Select(b => b.Bill.FixedGold).ToArray();
            Assert.AreEqual(14, fixedGold.Length, "血没尽的败局也能打完全程（血尽链在别条测试锁）");
            Assert.AreEqual(5, fixedGold[6], "R7 场固定段位仍 5（R6 败不涨段）");
        }

        [Test]
        public void Settle_EventsOrder_IncomeGoldXpRoundEnded()
        {
            var bus = new EventBus();
            var log = new List<string>();
            // 序号用 log.Count——log.Clear() 时序号随之归零（订阅器按 log 内位置打点）
            bus.Subscribe<IncomeSettledEvent>(e => log.Add($"{log.Count}:Income"));
            bus.Subscribe<GoldChangedEvent>(e => log.Add($"{log.Count}:Gold({e.Delta})"));
            bus.Subscribe<LevelUpEvent>(e => log.Add($"{log.Count}:LevelUp({e.Level})"));
            bus.Subscribe<XpChangedEvent>(e => log.Add($"{log.Count}:Xp"));
            bus.Subscribe<RoundEndedEvent>(e => log.Add($"{log.Count}:RoundEnded"));
            bus.Subscribe<HpChangedEvent>(e => log.Add($"{log.Count}:Hp"));

            // R1 空编成秒胜（被动 +2 XP）→ R2 一只弱怪胜（再 +2 = L3 门槛 4 → 升级）：
            // 结算窗事件序 = Income → Gold → LevelUp → Xp → RoundEnded（9.3 焊死）
            var enemies = new EnemyTable(new[] { Round(2, Foe(50, 0, new Pos(4, 4))) });
            var s = New(bus, Tables(SingleChar(hp: 500, atk: 100, range: 5), enemies));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            Assert.IsTrue(s.PlaceOnBoard(1, new Pos(4, 0)));
            PlayRound(s);   // R1 秒胜
            Assert.IsTrue(s.StartPrep(), "R1 → R2 备战");
            log.Clear();
            PlayRound(s);   // R2：驱动 + 结算——log 只含 R2 结算窗

            CollectionAssert.AreEqual(new[]
            {
                "0:Income", "1:Gold(6)", "2:LevelUp(3)", "3:Xp", "4:RoundEnded",
            }, log, "结算窗事件序（收入 5 + 连胜 1 = 6；被动经验恰好升 L3）");
        }

        [Test]
        public void Settle_R14Win_FullHp_SRating()
        {
            var bus = new EventBus();
            var hpEvents = new List<HpChangedEvent>();
            var finished = new List<RunFinishedEvent>();
            bus.Subscribe<HpChangedEvent>(e => hpEvents.Add(e));
            bus.Subscribe<RunFinishedEvent>(e => finished.Add(e));

            var enemies = new EnemyTable(new[] { Round(14, Foe(30, 0, new Pos(4, 4), atkIv: 1f)) });
            var s = New(bus, Tables(SingleChar(hp: 500, atk: 100, range: 5), enemies));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            Assert.IsTrue(s.PlaceOnBoard(1, new Pos(4, 0)));
            RunToEnd(s);

            Assert.IsTrue(s.RunOver, "赢下 R14 → 终局");
            Assert.AreEqual(GameSession.StartHp, s.PlayerHp, "全程无扣血");
            Assert.AreEqual(0, hpEvents.Count, "满血无 HpChangedEvent");
            Assert.AreEqual(RunRating.S, s.FinalRating, "满血通关 = S");
            Assert.AreEqual(1, finished.Count);
            Assert.AreEqual(RunRating.S, finished[0].Rating);
            Assert.AreEqual(14, finished[0].RoundsCompleted);
            Assert.AreEqual(100, finished[0].FinalHp);
        }

        [Test]
        public void Settle_BossWinFast_Heals35()
        {
            var bus = new EventBus();
            var hpEvents = new List<HpChangedEvent>();
            bus.Subscribe<HpChangedEvent>(e => hpEvents.Add(e));

            // R12/R13 空场败各扣 7+24=31 → 血 38；R14 弱 Boss t30 快杀 → 限时回血 +35 → 73
            var enemies = new EnemyTable(new[]
            {
                Round(12, Swarm(24)),
                Round(13, Swarm(24)),
                Round(14, Foe(30, 0, new Pos(4, 4), atkIv: 1f, name: "深渊魔王")),
            });
            var s = New(bus, Tables(SingleChar(hp: 500, atk: 100, range: 5), enemies));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            int playerId = s.Bench[0].Id;
            Assert.IsTrue(s.PlaceOnBoard(playerId, new Pos(4, 0)));
            RunToPrep(s, 12);   // R1~R11 空编成：棋在场 → 全秒胜不掉血

            Assert.IsTrue(s.TakeOffBoard(playerId), "R12 备战下棋 → 空场败");
            PlayRound(s);   // R12 败 31
            Assert.IsTrue(s.StartPrep());
            PlayRound(s);   // R13 败 31
            Assert.IsTrue(s.StartPrep());
            Assert.AreEqual(GameSession.StartHp - 62, s.PlayerHp, "两场惨败各扣 31");
            Assert.IsTrue(s.PlaceOnBoard(playerId, new Pos(4, 0)), "R14 重新上场");
            PlayRound(s);

            var heal = hpEvents.Last();
            Assert.AreEqual(35, heal.Delta, "≤2700 步击杀 Boss → +35（#21⑨）");
            Assert.AreEqual(73, s.PlayerHp, "38 + 35 = 73");
            Assert.AreEqual(RunRating.S, s.FinalRating, "73 ≥ 50 → S");
        }

        [Test]
        public void Settle_BossWinSlow_Heals10()
        {
            var bus = new EventBus();
            var hpEvents = new List<HpChangedEvent>();
            bus.Subscribe<HpChangedEvent>(e => hpEvents.Add(e));

            // Boss 血 10000、玩家 100 攻 1s/击 → 100 击 ≈ 3000 步 > 2700 限时线 → 超时获胜只回 10
            var enemies = new EnemyTable(new[]
            {
                Round(12, Swarm(24)),
                Round(13, Swarm(24)),
                Round(14, Foe(10000, 0, new Pos(4, 4), atkIv: 1f, range: 1, name: "深渊魔王")),
            });
            var s = New(bus, Tables(SingleChar(hp: 500, atk: 100, range: 5), enemies));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            int playerId = s.Bench[0].Id;
            Assert.IsTrue(s.PlaceOnBoard(playerId, new Pos(4, 0)));
            RunToPrep(s, 12);   // R1~R11 空编成：棋在场 → 全秒胜不掉血

            Assert.IsTrue(s.TakeOffBoard(playerId), "R12 备战下棋 → 空场败");
            PlayRound(s);   // R12 败 31
            Assert.IsTrue(s.StartPrep());
            PlayRound(s);   // R13 败 31
            Assert.IsTrue(s.StartPrep());
            Assert.IsTrue(s.PlaceOnBoard(playerId, new Pos(4, 0)));
            PlayRound(s);

            Assert.Greater(s.Combat.TickCount, GameSession.BossTimeLimitTicks, "这场确实拖过了 90 秒线");
            var heal = hpEvents.Last();
            Assert.AreEqual(10, heal.Delta, "超时获胜只回 10（#21⑨）");
            Assert.AreEqual(48, s.PlayerHp, "38 + 10 = 48");
            Assert.AreEqual(RunRating.A, s.FinalRating, "48 ≥ 25 → A");
        }

        [Test]
        public void Settle_BossLoss_ZeroHpGameOver()
        {
            var bus = new EventBus();
            var hpEvents = new List<HpChangedEvent>();
            var finished = new List<RunFinishedEvent>();
            var ended = new List<RoundEndedEvent>();
            bus.Subscribe<HpChangedEvent>(e => hpEvents.Add(e));
            bus.Subscribe<RunFinishedEvent>(e => finished.Add(e));
            bus.Subscribe<RoundEndedEvent>(e => ended.Add(e));

            // R14 强 Boss + 空场败：血量清零判负（不走 DamageBase+存活数公式）
            var enemies = new EnemyTable(new[] { Round(14, Foe(500, 200, new Pos(4, 4), range: 5, name: "深渊魔王")) });
            var s = New(bus, Tables(SingleChar(), enemies));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            int playerId = s.Bench[0].Id;
            Assert.IsTrue(s.PlaceOnBoard(playerId, new Pos(4, 0)));
            RunToPrep(s, 14);   // R1~R13 空编成：棋在场 → 全秒胜，血 100 进 R14
            Assert.AreEqual(GameSession.StartHp, s.PlayerHp, "R14 前无扣血");

            Assert.IsTrue(s.TakeOffBoard(playerId), "R14 备战下棋 → 空场对 Boss");
            Assert.IsTrue(s.StartBattle());

            Assert.IsTrue(s.RunOver, "Boss 败 = 判负终局");
            Assert.AreEqual(0, s.PlayerHp, "血量清零");
            Assert.AreEqual(-GameSession.StartHp, hpEvents.Last().Delta, "一条事件把 100 扣到 0");
            Assert.AreEqual(100, ended.Last().DamageTaken, "RoundEndedEvent 摊平清零量");
            Assert.AreEqual(RunRating.C, s.FinalRating);
            Assert.AreEqual(13, finished[0].RoundsCompleted, "打到 R14 没赢 = 完成 13 回合");
            Assert.AreEqual(0, finished[0].FinalHp);
        }

        [Test]
        public void Settle_HpDepletedMidRun_GameOverC()
        {
            var bus = new EventBus();
            var hpEvents = new List<HpChangedEvent>();
            var ended = new List<RoundEndedEvent>();
            var finished = new List<RunFinishedEvent>();
            bus.Subscribe<HpChangedEvent>(e => hpEvents.Add(e));
            bus.Subscribe<RoundEndedEvent>(e => ended.Add(e));
            bus.Subscribe<RunFinishedEvent>(e => finished.Add(e));

            // 惨败链：R2~R4 各 24 只怪（每场扣 3+24=27）、R5 奖励关 2 只（豁免 0）、R6 精英 24 只（8+24=32）
            var enemies = new EnemyTable(new[]
            {
                Round(2, Swarm(24)),
                Round(3, Swarm(24)),
                Round(4, Swarm(24)),
                Round(5, Foe(30, 0, new Pos(3, 4)), Foe(30, 0, new Pos(5, 4))),
                Round(6, Swarm(24)),
            });
            var s = New(bus, Tables(SingleChar(), enemies));
            RunToEnd(s);

            Assert.IsTrue(s.RunOver, "血尽终局");
            Assert.AreEqual(0, s.PlayerHp);
            CollectionAssert.AreEqual(new[] { -27, -27, -27, -19 },
                hpEvents.Select(e => e.Delta).ToArray(),
                "27×3 场（3+24）→ R5 豁免 0 → 血 19 → R6 扣 32 钳 0（实扣 19）");
            Assert.AreEqual(0, ended.First(e => e.Round == 5).DamageTaken, "奖励关败豁免在链路里成立");
            Assert.AreEqual(RunRating.C, s.FinalRating);
            Assert.AreEqual(5, finished[0].RoundsCompleted, "死于 R6 → 完成 5 回合");
        }

        [Test]
        public void RateRun_PureFunction_Tiers()
        {
            Assert.AreEqual(RunRating.C, GameSession.RateRun(false, 100), "未通关恒 C");
            Assert.AreEqual(RunRating.C, GameSession.RateRun(false, 0));
            Assert.AreEqual(RunRating.S, GameSession.RateRun(true, 50), "50 血线（含）");
            Assert.AreEqual(RunRating.A, GameSession.RateRun(true, 49));
            Assert.AreEqual(RunRating.A, GameSession.RateRun(true, 25), "25 血线（含）");
            Assert.AreEqual(RunRating.B, GameSession.RateRun(true, 24));
            Assert.AreEqual(RunRating.B, GameSession.RateRun(true, 1));
        }

        // ─────────────────────── 战斗种子确定性（D2-6） ───────────────────────

        [Test]
        public void BattleSeed_SameSeedSameOps_IdenticalBattle()
        {
            GameSession Make(EventBus bus, List<UnitDamagedEvent> damages)
            {
                // 编成 R1 一只 500 血怪；玩家 crit 5% 棋（战斗内真掷骰）
                var enemies = new EnemyTable(new[] { Round(1, Foe(500, 30, new Pos(4, 4), atkIv: 1f, range: 5)) });
                var s = New(bus, Tables(SingleChar(hp: 500, atk: 100, range: 5, crit: 5), enemies));
                Assert.IsTrue(s.StartRun());
                Assert.IsTrue(s.Buy(0));
                Assert.IsTrue(s.PlaceOnBoard(1, new Pos(4, 0)));
                Assert.IsTrue(s.StartBattle());
                int guard = 0;
                while (!s.Combat.IsOver && guard++ < 6000) s.Combat.Tick();
                Assert.IsTrue(s.SettleBattle());
                return s;
            }

            var damagesA = new List<UnitDamagedEvent>();
            var a = Make(new EventBus(), damagesA);
            var damagesB = new List<UnitDamagedEvent>();
            var b = Make(new EventBus(), damagesB);

            Assert.AreEqual(a.LastBattleSeed, b.LastBattleSeed, "同种子同操作 → 同战斗种子");
            Assert.AreEqual(a.Combat.TickCount, b.Combat.TickCount, "逐 tick 一致（步数一致）");
            Assert.AreEqual(a.Combat.PlayerWon, b.Combat.PlayerWon);
            CollectionAssert.AreEqual(
                damagesA.Select(e => (e.UnitId, e.FinalDamage)).ToArray(),
                damagesB.Select(e => (e.UnitId, e.FinalDamage)).ToArray(),
                "伤害事件流逐条一致（同种子重演 = 看录像的地基）");
        }

        [Test]
        public void BattleSeed_ConsumedFromSessionRandom()
        {
            // A 直接开战；B 同种子但先手动刷一次商店（会话随机多消耗一次上架）→ 种子不同
            var a = New(new EventBus(), Tables(SingleChar()), 77u);
            Assert.IsTrue(a.StartRun());
            Assert.IsTrue(a.StartBattle());

            var b = New(new EventBus(), Tables(SingleChar()), 77u);
            Assert.IsTrue(b.StartRun());
            Assert.IsTrue(b.RefreshShop());
            Assert.IsTrue(b.StartBattle());

            Assert.AreNotEqual(a.LastBattleSeed, b.LastBattleSeed,
                "战斗种子从会话随机流抽取——会话消耗不同则种子不同（D2-6 分家的证明）");
        }

        [Test]
        public void BattleSeed_InValidRange()
        {
            var s = New(new EventBus(), Tables(SingleChar()), 12345u);
            Assert.IsTrue(s.StartRun());
            var seeds = new List<uint>();
            for (int i = 0; i < 3; i++)
            {
                Assert.IsTrue(s.StartBattle());
                seeds.Add(s.LastBattleSeed);
                Assert.GreaterOrEqual(s.LastBattleSeed, 1u, "种子 ∈ [1, int.MaxValue)——0 非法");
                Assert.IsTrue(s.StartPrep());
            }
            Assert.IsTrue(seeds[0] != seeds[1] || seeds[1] != seeds[2], "三场种子不应全同（抽的是不同随机位）");
        }

        [Test]
        public void BattleRandoms_IndependentFromSessionRandom()
        {
            // 同种子、同结构、只差暴击率：A 必暴（战斗流掷骰多）、B 无暴击（0 掷）——
            // 战斗内掷骰走 CombatSystem 自持的种子流，不许污染会话流 → 两次免费上架序列仍一致
            GameTables CritTables(int crit)
            {
                var enemies = new EnemyTable(new[] { Round(1, Foe(90, 0, new Pos(4, 4), atkIv: 1f, range: 5)) });
                return Tables(SingleChar(hp: 500, atk: 60, range: 5, crit: crit), enemies);
            }

            (GameSession s, List<int> offers) Play(int crit)
            {
                var s = New(new EventBus(), CritTables(crit), 42u);
                Assert.IsTrue(s.StartRun());
                Assert.IsTrue(s.Buy(0));
                Assert.IsTrue(s.PlaceOnBoard(1, new Pos(4, 0)));
                Assert.IsTrue(s.StartBattle());
                int guard = 0;
                while (!s.Combat.IsOver && guard++ < 6000) s.Combat.Tick();
                Assert.IsTrue(s.SettleBattle());
                Assert.IsTrue(s.StartPrep());
                return (s, s.Shop.Offers.Select(o => o.Id).ToList());
            }

            var a = Play(100);   // 必暴：90×1.5=135 一击杀（t30，掷 1 次）
            var b = Play(0);     // 无暴：90 伤两击（t60，0 掷）
            Assert.AreEqual(30, a.s.Combat.TickCount, "暴击场 t30 分晓");
            Assert.AreEqual(60, b.s.Combat.TickCount, "无暴场 t60 分晓（战斗过程确实不同）");
            CollectionAssert.AreEqual(a.offers, b.offers,
                "战斗掷骰不耗会话流——第二次免费上架序列仍逐格一致");
        }

        // ─────────────────────── GameTables 注入包 ───────────────────────

        [Test]
        public void GameTables_Default_BundlesSevenTables()
        {
            var t = GameTables.Default;
            Assert.AreSame(CharacterTable.Default, t.Characters, "Default = 各表 Default 打包（同一引用）");
            Assert.AreSame(ShopOdds.Default, t.Odds);
            Assert.AreSame(RoundTable.Default, t.Rounds);
            Assert.AreSame(EnemyTable.Default, t.Enemies);
            Assert.AreSame(SynergyTable.Default, t.Synergies);
            Assert.AreSame(OreTable.Default, t.Ores, "批次6 起矿池入包");
            Assert.AreSame(PolicyTable.Default, t.Policies, "批次7 起策略表入包——七表齐（阶段2 全量）");
            Assert.AreEqual(18, t.Characters.Count);
            Assert.AreEqual(14, t.Rounds.Count);
        }
    }
}
