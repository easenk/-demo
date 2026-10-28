using System;
using System.Collections.Generic;
using System.Linq;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    /// <summary>
    /// 阶段2 批次6：晶矿系统——掉落记账 / 留存 / PickOre / 开战门禁 / 三类发放路径 / 确定性。
    /// 分节契约（每条测试的断言消息自带语义）：
    ///  · 表构造：挡位 1~3 且三挡齐全不重复、池非空、奖励 id 唯一 ≥1、池内 id 必须已登记、
    ///    类型字段自洽（金币/经验 Amount&gt;0、装备 EquipmentId&gt;0）——建表事故构造期炸
    ///  · 掉落记账：只记编成挡位&gt;0（#17③ 不查回合类型）、死亡格正确、事件字段全、不占棋盘格（#17⑤）、
    ///    玩家单位死亡永不掉、默认编成端到端（R1 矿鼠必掉 / R2 野狼不掉=填表纪律）
    ///  · 留存与门禁：战败已掉的矿照样留（#17⑧）、未死的怪不掉、跨 EnterPrep 留存、
    ///    场上有矿 StartBattle 纯拒绝四不动（#17⑥）、捡完解锁、非备战期 PickOre 全拒
    ///  · 发放三路径（2026-10-27 拍板：掉落物只有金币/经验/装备散件）：
    ///    金币加钱、经验加经验、装备仅广播事件（EquipmentId 原样传递、无其他副作用）
    ///  · 确定性：同种子同操作序列 = 同掉落物 id + 同抽取结果（回放重演的地基之一）
    /// 测试纪律：矿 id 与单位 id 共用 IdAllocator 号池——一律取 OresOnGround[0].Id / Bench[0].Id，禁止猜数字。
    /// </summary>
    [TestFixture]
    public class OreTests
    {
        // ─────────────────────── 测试基建 ───────────────────────

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

        /// <summary>敌方速记：挡位可传（默认 0 不掉）、crit 恒 0（表数据纪律 D2-7）。</summary>
        private static EnemySpawn Foe(int hp, int atk, Pos cell, int oreTier = 0,
            float atkIv = 1.3f, int range = 1, string name = "怪")
            => new EnemySpawn(name, Stats(hp, atk, atkIv, 1f, range), cell, oreTier, 1);

        private static EnemyRoundEntry Round(int round, params EnemySpawn[] spawns)
            => new EnemyRoundEntry(round, spawns);

        // ── 矿池速记 ──

        private static OreDef Gold(int id, int amount) => new OreDef(id, OreRewardType.Gold, "金币占位", amount, 0);
        private static OreDef Xp(int id, int amount) => new OreDef(id, OreRewardType.Xp, "经验占位", amount, 0);
        private static OreDef Gear(int id, int equipId) => new OreDef(id, OreRewardType.Equipment, "装备占位", 0, equipId);

        /// <summary>注入用矿池：T1 传定制条目，T2/T3 自动补占位（OreTable 三挡齐全校验的要求）。</summary>
        private static OreTable Pool(params OreDef[] tier1)
        {
            var defs = new List<OreDef>(tier1) { Gold(21, 1), Gold(31, 1) };
            var ids = new List<int>();
            foreach (var d in tier1) ids.Add(d.Id);
            return new OreTable(defs, new[]
            {
                new OreTierDef(1, ids.ToArray()),
                new OreTierDef(2, new[] { 21 }),
                new OreTierDef(3, new[] { 31 }),
            });
        }

        /// <summary>注入包：角色表必给；编成默认空表；矿池默认 Default；策略表给空表（批次7 起弹选管线必读此表——
        /// 空表=奖励关胜后 0 候选静默，OreTests 链路不被策略门禁干扰）。</summary>
        private static GameTables Tables(CharacterTable chars, EnemyTable enemies = null, OreTable ores = null)
            => new GameTables
            {
                Characters = chars,
                Odds = AllCostOne(),
                Rounds = RoundTable.Default,
                Enemies = enemies ?? EnemyTable.Empty,
                Synergies = SynergyTable.Default,
                Ores = ores ?? OreTable.Default,
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

        /// <summary>捡光场上全部晶矿（门禁解锁 helper；测试链路推进用）。</summary>
        private static void DrainOres(GameSession s)
        {
            int guard = 0;
            while (s.OresOnGround.Count > 0)
            {
                Assert.Less(guard++, 100, "捡矿循环异常长");
                Assert.IsTrue(s.PickOre(s.OresOnGround[0].Id), "备战期捡矿应成功");
            }
        }

        /// <summary>打完当前回合（开战前先捡光上一场残留——StartBattle 门禁；空编成时内联结算）。</summary>
        private static void PlayRound(GameSession s)
        {
            DrainOres(s);
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

        /// <summary>标准"掉一颗矿"链：R1 单只挡位怪（range 5 原地不移动），玩家远程击杀。
        /// 返回 (会话, 矿实例 id, 掉落事件, 开矿事件收集器)。进阶断言在调用方续写。</summary>
        private static GameSession DropOne(EventBus bus, int tier, OreTable ores, out int oreId)
        {
            var enemies = new EnemyTable(new[] { Round(1, Foe(30, 0, new Pos(4, 4), oreTier: tier, range: 5)) });
            var s = New(bus, Tables(SingleChar(hp: 500, atk: 100, range: 5), enemies, ores));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            Assert.IsTrue(s.PlaceOnBoard(s.Bench[0].Id, new Pos(4, 0)));
            PlayRound(s);   // 杀怪 → 掉 1 矿 → 结算
            Assert.AreEqual(1, s.OresOnGround.Count, "恰好掉一颗");
            oreId = s.OresOnGround[0].Id;
            Assert.IsTrue(s.StartPrep(), "进 R2 备战（矿跨期留存）");
            return s;
        }

        // ─────────────────────── 表构造（建表事故构造期炸） ───────────────────────

        [Test]
        public void OreTable_Ctor_ValidatesTierShape()
        {
            OreDef[] OneGold = { Gold(11, 1) };
            // 挡位越界
            Assert.Throws<ArgumentException>(() => new OreTable(OneGold, new[] { new OreTierDef(0, new[] { 11 }) }),
                "挡位 0 拒");
            Assert.Throws<ArgumentException>(() => new OreTable(OneGold, new[] { new OreTierDef(4, new[] { 11 }) }),
                "挡位 4 拒");
            // 池空 / null
            Assert.Throws<ArgumentException>(() => new OreTable(OneGold, new[] { new OreTierDef(1, new int[0]) }),
                "空池拒");
            Assert.Throws<ArgumentException>(() => new OreTable(OneGold, new[] { new OreTierDef(1, null) }),
                "null 池拒");
            // 三挡缺一 / 挡位重复
            Assert.Throws<ArgumentException>(() => new OreTable(OneGold, new[]
            {
                new OreTierDef(1, new[] { 11 }),
                new OreTierDef(2, new[] { 11 }),
            }), "缺挡位 3 拒（编成表任何挡位都可能出现，缺挡 = 开矿期运行时炸）");
            Assert.Throws<ArgumentException>(() => new OreTable(OneGold, new[]
            {
                new OreTierDef(1, new[] { 11 }),
                new OreTierDef(1, new[] { 11 }),
                new OreTierDef(3, new[] { 11 }),
            }), "挡位重复拒");
            // 合法三挡通过（同 id 可进多挡池——高档池含低档条目属编表自由）
            var ok = new OreTable(OneGold, new[]
            {
                new OreTierDef(1, new[] { 11 }),
                new OreTierDef(2, new[] { 11 }),
                new OreTierDef(3, new[] { 11 }),
            });
            Assert.AreEqual(1, ok.GetTier(3).OreIds.Length, "合法表三挡可查");
        }

        [Test]
        public void OreTable_Ctor_ValidatesOreIds()
        {
            // 定义 id 非法
            Assert.Throws<ArgumentException>(() => new OreTable(new[] { Gold(0, 1) },
                new[] { new OreTierDef(1, new[] { 0 }) }), "id 0 拒");
            Assert.Throws<ArgumentException>(() => new OreTable(new[] { Gold(-1, 1) },
                new[] { new OreTierDef(1, new[] { -1 }) }), "负 id 拒");
            // 定义 id 重复
            Assert.Throws<ArgumentException>(() => new OreTable(new[] { Gold(11, 1), Gold(11, 2) }, new[]
            {
                new OreTierDef(1, new[] { 11 }),
                new OreTierDef(2, new[] { 11 }),
                new OreTierDef(3, new[] { 11 }),
            }), "重复 id 拒");
            // 池内 id 未在定义集登记
            Assert.Throws<ArgumentException>(() => new OreTable(new[] { Gold(11, 1) }, new[]
            {
                new OreTierDef(1, new[] { 11 }),
                new OreTierDef(2, new[] { 11 }),
                new OreTierDef(3, new[] { 99 }),
            }), "池内未登记 id 拒");
        }

        [Test]
        public void OreTable_Ctor_ValidatesTypeFields()
        {
            Assert.Throws<ArgumentException>(() => new OreTable(new[] { Gold(11, 0) }, new[]
            {
                new OreTierDef(1, new[] { 11 }),
                new OreTierDef(2, new[] { 11 }),
                new OreTierDef(3, new[] { 11 }),
            }), "金币 Amount=0 拒");
            Assert.Throws<ArgumentException>(() => new OreTable(new[] { Xp(11, -2) }, new[]
            {
                new OreTierDef(1, new[] { 11 }),
                new OreTierDef(2, new[] { 11 }),
                new OreTierDef(3, new[] { 11 }),
            }), "经验 Amount&lt;0 拒");
            Assert.Throws<ArgumentException>(() => new OreTable(new[] { Gear(11, 0) }, new[]
            {
                new OreTierDef(1, new[] { 11 }),
                new OreTierDef(2, new[] { 11 }),
                new OreTierDef(3, new[] { 11 }),
            }), "装备 EquipmentId=0 拒");
        }

        [Test]
        public void OreTable_Default_ThreeTiersFilled()
        {
            var t = OreTable.Default;
            for (int tier = 1; tier <= 3; tier++)
            {
                OreTierDef pool = t.GetTier(tier);
                Assert.Greater(pool.OreIds.Length, 0, $"挡位 {tier} 池非空");
                foreach (int oreId in pool.OreIds)
                {
                    OreDef def = t.GetOre(oreId);
                    Assert.AreNotEqual(0, def.Id, $"挡位 {tier} 池内 id {oreId} 必须查得到（构造校验兜底复核）");
                }
            }
        }

        // ─────────────────────── 掉落记账（#17③⑤：编成挡位驱动、不占格、纯事件） ───────────────────────

        [Test]
        public void Drop_RecordsDeathCellAndTier_NoBoardOccupancy()
        {
            var bus = new EventBus();
            var dropped = new List<OreDroppedEvent>();
            bus.Subscribe<OreDroppedEvent>(e => dropped.Add(e));

            // 两只不同挡位的原地怪（range 5 对射不移动）→ 死在自己格上
            var enemies = new EnemyTable(new[]
            {
                Round(1, Foe(30, 0, new Pos(3, 4), oreTier: 1, range: 5, name: "甲"),
                       Foe(30, 0, new Pos(5, 4), oreTier: 3, range: 5, name: "乙")),
            });
            var s = New(bus, Tables(SingleChar(hp: 500, atk: 100, range: 5), enemies));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            Assert.IsTrue(s.PlaceOnBoard(s.Bench[0].Id, new Pos(4, 0)));
            PlayRound(s);

            Assert.AreEqual(2, s.OresOnGround.Count, "两只挡位怪全灭 → 恰掉两颗");
            Assert.AreEqual(2, dropped.Count, "各广播一条 OreDroppedEvent");
            var tiers = new HashSet<int> { dropped[0].Tier, dropped[1].Tier };
            CollectionAssert.AreEqual(new[] { 1, 3 }, tiers.OrderBy(x => x).ToArray(), "挡位按编成各带各的");
            foreach (var e in dropped)
            {
                OreDrop drop = s.OresOnGround.First(d => d.Id == e.OreId);
                Assert.AreEqual(drop.Tier, e.Tier, "事件挡位 = 掉落物挡位");
                Assert.AreEqual(drop.Cell, e.Cell, "事件位置 = 掉落物位置");
                Assert.IsNull(s.Board.GetOccupant(e.Cell), "掉落物不占棋盘格（#17⑤）——死亡格无占用记录");
            }
        }

        [Test]
        public void Drop_TierZeroMonsterNeverDrops()
        {
            var bus = new EventBus();
            var dropped = new List<OreDroppedEvent>();
            bus.Subscribe<OreDroppedEvent>(e => dropped.Add(e));

            var enemies = new EnemyTable(new[] { Round(1, Foe(30, 0, new Pos(4, 4), range: 5)) });
            var s = New(bus, Tables(SingleChar(hp: 500, atk: 100, range: 5), enemies));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            Assert.IsTrue(s.PlaceOnBoard(s.Bench[0].Id, new Pos(4, 0)));
            PlayRound(s);

            Assert.AreEqual(0, s.OresOnGround.Count, "挡位 0 的怪死不掉（账本只记 OreTier>0）");
            Assert.AreEqual(0, dropped.Count, "无掉落事件");
        }

        [Test]
        public void Drop_PlayerUnitDeathNeverDrops()
        {
            var bus = new EventBus();
            var dropped = new List<OreDroppedEvent>();
            bus.Subscribe<OreDroppedEvent>(e => dropped.Add(e));

            // 带矿强怪秒杀玩家棋 → 玩家全灭败；怪活着不掉、玩家棋死了也永不掉（账本只记敌方）
            var enemies = new EnemyTable(new[] { Round(1, Foe(500, 200, new Pos(4, 4), oreTier: 1, atkIv: 0.5f, range: 5)) });
            var s = New(bus, Tables(SingleChar(hp: 30, atk: 0, range: 5), enemies));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            Assert.IsTrue(s.PlaceOnBoard(s.Bench[0].Id, new Pos(4, 0)));
            PlayRound(s);   // t15 敌一击秒玩家 → 敌胜

            Assert.IsFalse(s.Combat.PlayerWon, "玩家棋全灭 → 战败");
            Assert.AreEqual(0, s.OresOnGround.Count, "玩家单位死亡永不掉矿；带矿怪活着也不掉（#17⑧）");
            Assert.AreEqual(0, dropped.Count, "无掉落事件");
        }

        [Test]
        public void Drop_DefaultEnemies_RewardRoundsDrop_NormalRoundsDoNot()
        {
            var bus = new EventBus();
            var dropped = new List<OreDroppedEvent>();
            bus.Subscribe<OreDroppedEvent>(e => dropped.Add(e));

            // 默认编成端到端：R1 奖励关晶矿鼠 ×2（挡位 1，100% 掉，#20①）→ 必掉 2 颗；
            // R2 普通关野狼 OreTier=0（填表纪律 #17③——掉落判断不查回合类型，非奖励关不掉全靠编成写 0）
            var s = New(bus, Tables(SingleChar(hp: 500, atk: 100, range: 5), EnemyTable.Default));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            Assert.IsTrue(s.PlaceOnBoard(s.Bench[0].Id, new Pos(4, 0)));

            PlayRound(s);   // R1：杀两鼠 → 掉 2
            Assert.AreEqual(2, s.OresOnGround.Count, "R1 晶矿鼠必掉（100% 掉落）");
            Assert.AreEqual(2, dropped.Count, "两只鼠各广播一条");
            foreach (var e in dropped)
                Assert.AreEqual(1, e.Tier, "默认编成 R1 矿鼠挡位 1");

            Assert.IsTrue(s.StartPrep(), "R1 → R2 备战（打完停 Settlement，捡矿在备战期）");
            int droppedAfterR1 = dropped.Count;
            PlayRound(s);   // R2：门禁逼玩家先捡光 2 颗 → 再战野狼 → 狼不掉
            Assert.AreEqual(0, s.OresOnGround.Count, "R2 野狼全灭后无新掉落（OreTier=0 填表纪律端到端）");
            Assert.AreEqual(droppedAfterR1, dropped.Count, "普通关无掉落事件");
        }

        // ─────────────────────── 留存与门禁（#17⑥⑧） ───────────────────────

        [Test]
        public void Drop_PartialLoss_OresPersistAndBlockNextBattle()
        {
            var bus = new EventBus();
            var dropped = new List<OreDroppedEvent>();
            bus.Subscribe<OreDroppedEvent>(e => dropped.Add(e));

            // 战败链：A（挡位 1、血 30）被玩家 t30 一击杀掉矿；B（挡位 1、血 5000、10 秒首击、全图射程）
            // t300 一击秒玩家 → 敌胜——B 存活不掉。已掉之矿照样留（#17⑧：输了少赚但已到手的矿不消失）
            var enemies = new EnemyTable(new[]
            {
                Round(1, Foe(30, 0, new Pos(4, 3), oreTier: 1, range: 5, name: "A"),
                       Foe(5000, 200, new Pos(7, 4), oreTier: 1, atkIv: 10f, range: 8, name: "B")),
            });
            var s = New(bus, Tables(SingleChar(hp: 30, atk: 30, range: 3), enemies));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            Assert.IsTrue(s.PlaceOnBoard(s.Bench[0].Id, new Pos(4, 0)));
            PlayRound(s);

            Assert.IsFalse(s.Combat.PlayerWon, "B 反杀玩家 → 战败");
            Assert.AreEqual(1, s.OresOnGround.Count, "恰一颗：A 死掉、B 活着不掉");
            int oreId = s.OresOnGround[0].Id;
            Assert.AreEqual(1, s.OresOnGround[0].Tier, "掉的是 A 的挡位 1 矿");
            Assert.AreEqual(1, dropped.Count, "恰一条掉落事件");
            Assert.AreEqual(GamePhase.Settlement, s.Phase, "战败后停结算位");

            // 结算期（非备战期）捡矿被拒
            Assert.IsFalse(s.PickOre(oreId), "Settlement 期捡矿拒（PickOre 仅 Prep）");

            // 跨期留存：进 R2 备战，矿还在 → 开战被门禁拒（#17⑥）
            Assert.IsTrue(s.StartPrep());
            Assert.AreEqual(1, s.OresOnGround.Count, "EnterPrep 不清晶矿列表（跨期留存）");
            Assert.IsFalse(s.StartBattle(), "场上有未拾取晶矿 → 开战纯拒绝");
            Assert.AreEqual(GamePhase.Prep, s.Phase, "拒绝时相位不动");
            Assert.AreEqual(0, s.EnemyRoster.Count, "拒绝时敌方未生成");

            // 捡完解锁
            Assert.IsTrue(s.PickOre(oreId), "备战期捡矿成功");
            Assert.AreEqual(0, s.OresOnGround.Count, "唯一出口：捡走即移出列表");
            Assert.IsTrue(s.StartBattle(), "捡完晶矿 → 开战放行");
        }

        [Test]
        public void StartBattle_BlockedWhileOreOnGround_PureRejection()
        {
            var ores = Pool(Gold(11, 100));
            // 掉 1 矿 → 进 R2 备战 → 门禁四不动断言
            var bus = new EventBus();
            var s = DropOne(bus, 1, ores, out int id);
            uint seedBefore = s.LastBattleSeed;
            Assert.IsFalse(s.StartBattle(), "有矿拒");
            Assert.AreEqual(GamePhase.Prep, s.Phase, "相位不动");
            Assert.AreEqual(0, s.EnemyRoster.Count, "敌方未生成");
            Assert.AreEqual(seedBefore, s.LastBattleSeed, "种子字段不动（会话随机流零消耗）");
            Assert.IsTrue(s.PickOre(id), "捡走");
            Assert.IsTrue(s.StartBattle(), "捡完解锁开战");
        }

        [Test]
        public void PickOre_GatedByPhase()
        {
            var ores = Pool(Gold(11, 100));

            // PreRun：构造即拒
            var s = New(new EventBus(), Tables(SingleChar(), null, ores));
            Assert.IsFalse(s.PickOre(1), "PreRun 拒");

            // Battle：战斗中拒（矿列表必空——门禁保证有矿根本开不了战，未知 id 同样拒）
            var busy = new EventBus();
            var enemies = new EnemyTable(new[] { Round(1, Foe(10000, 0, new Pos(4, 4), range: 5)) });
            var inBattle = New(busy, Tables(SingleChar(hp: 500, atk: 0, range: 5), enemies));
            Assert.IsTrue(inBattle.StartRun());
            Assert.IsTrue(inBattle.Buy(0));
            Assert.IsTrue(inBattle.PlaceOnBoard(inBattle.Bench[0].Id, new Pos(4, 0)));
            Assert.IsTrue(inBattle.StartBattle());
            Assert.AreEqual(GamePhase.Battle, inBattle.Phase);
            Assert.IsFalse(inBattle.PickOre(1), "Battle 拒");
            // Settlement 期的拒与 Prep 放行已由 PartialLoss 链锁（Settlement 拒 → StartPrep 后捡成功）
        }

        [Test]
        public void PickOre_UnknownOrAlreadyPickedId_Rejected()
        {
            var bus = new EventBus();
            var gold = new List<GoldChangedEvent>();
            var opened = new List<OreOpenedEvent>();
            bus.Subscribe<GoldChangedEvent>(e => gold.Add(e));
            bus.Subscribe<OreOpenedEvent>(e => opened.Add(e));

            var ores = Pool(Gold(11, 100));
            var s = DropOne(bus, 1, ores, out int oreId);
            int goldEventsSoFar = gold.Count;   // 基线：DropOne 链内已有买棋/结算收入的金币事件
            int goldBeforeUnknown = s.Eco.Gold;

            Assert.IsFalse(s.PickOre(oreId + 1000), "未知 id 拒");
            Assert.AreEqual(goldEventsSoFar, gold.Count, "未知 id 拒：钱包不动");
            Assert.AreEqual(goldBeforeUnknown, s.Eco.Gold);
            Assert.AreEqual(0, opened.Count, "未知 id 拒：不开矿");

            int goldBefore = s.Eco.Gold;
            Assert.IsTrue(s.PickOre(oreId), "已知 id 成功");
            Assert.AreEqual(goldBefore + 100, s.Eco.Gold, "金币到账");
            Assert.AreEqual(1, opened.Count, "恰一条开矿事件");

            int goldAfterOpen = s.Eco.Gold;
            Assert.IsFalse(s.PickOre(oreId), "已捡的 id 再捡拒（成功即移出列表）");
            Assert.AreEqual(goldAfterOpen, s.Eco.Gold, "重复捡拒：钱包不再动");
            Assert.AreEqual(1, opened.Count, "重复捡拒：不再开矿");
        }

        [Test]
        public void PickOre_PicksEachOreById_PerTierPools()
        {
            var bus = new EventBus();
            var opened = new List<OreOpenedEvent>();
            bus.Subscribe<OreOpenedEvent>(e => opened.Add(e));

            // 两挡各一颗：T1 池 = 金 +100；T2 池 = 金 +200（Pool 的 T2 占位换金 +200）
            var ores = new OreTable(
                new[] { Gold(11, 100), Gold(21, 200), Gold(31, 1) },
                new[]
                {
                    new OreTierDef(1, new[] { 11 }),
                    new OreTierDef(2, new[] { 21 }),
                    new OreTierDef(3, new[] { 31 }),
                });
            var enemies = new EnemyTable(new[]
            {
                Round(1, Foe(30, 0, new Pos(3, 4), oreTier: 1, range: 5, name: "低挡"),
                       Foe(30, 0, new Pos(5, 4), oreTier: 2, range: 5, name: "高挡")),
            });
            var s = New(bus, Tables(SingleChar(hp: 500, atk: 100, range: 5), enemies, ores));
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0));
            Assert.IsTrue(s.PlaceOnBoard(s.Bench[0].Id, new Pos(4, 0)));
            PlayRound(s);
            Assert.AreEqual(2, s.OresOnGround.Count, "两颗都在场");
            Assert.IsTrue(s.StartPrep(), "打完停 Settlement → 进 R2 备战后才能捡");

            // 按 Id 各捡：各查各挡位的池（拾取以 Id 为键、不以格位为键——同格多矿的合法性根基）
            int idT1 = s.OresOnGround.First(d => d.Tier == 1).Id;
            int idT2 = s.OresOnGround.First(d => d.Tier == 2).Id;
            int goldBefore = s.Eco.Gold;
            Assert.IsTrue(s.PickOre(idT1));
            Assert.IsTrue(s.PickOre(idT2));
            Assert.AreEqual(goldBefore + 100 + 200, s.Eco.Gold, "两颗各按各挡位池发放");
            CollectionAssert.AreEqual(new[] { 100, 200 }, opened.Select(e => e.Amount).ToArray(),
                "开矿事件按捡的顺序摊平奖励");
            Assert.AreEqual(0, s.OresOnGround.Count, "全捡空");
        }

        // ─────────────────────── 发放三路径（2026-10-27 拍板：只有金币/经验/装备散件） ───────────────────────

        [Test]
        public void Open_Gold_AddsGoldAndBroadcastsAllFields()
        {
            var bus = new EventBus();
            var opened = new List<OreOpenedEvent>();
            var gold = new List<GoldChangedEvent>();
            bus.Subscribe<OreOpenedEvent>(e => opened.Add(e));
            bus.Subscribe<GoldChangedEvent>(e => gold.Add(e));

            var ores = Pool(Gold(11, 100));
            var s = DropOne(bus, 1, ores, out int oreId);   // 死亡格 (4,4)、挡位 1
            int goldBefore = s.Eco.Gold;
            Assert.IsTrue(s.PickOre(oreId));

            var e1 = opened.Single();
            Assert.AreEqual(oreId, e1.OreId, "同一颗掉落物实例");
            Assert.AreEqual(1, e1.Tier);
            Assert.AreEqual(new Pos(4, 4), e1.Cell, "开矿位置 = 死亡格");
            Assert.AreEqual(11, e1.OreDefId, "抽中的定义 id（单条池必中）");
            Assert.AreEqual(OreRewardType.Gold, e1.RewardType);
            Assert.AreEqual(100, e1.Amount);
            Assert.AreEqual(0, e1.EquipmentId, "金币类装备 id 恒 0");
            Assert.AreEqual(goldBefore + 100, s.Eco.Gold, "钱包 +100");
            Assert.AreEqual(100, gold.Last().Delta, "金币事件在前（先到账、后陈述开矿）");
        }

        [Test]
        public void Open_Xp_AddsExperience()
        {
            var bus = new EventBus();
            var opened = new List<OreOpenedEvent>();
            bus.Subscribe<OreOpenedEvent>(e => opened.Add(e));

            var ores = Pool(Xp(11, 4));
            var s = DropOne(bus, 1, ores, out int oreId);
            Assert.AreEqual(2, s.Eco.XpTotal, "捡前已有结算被动经验 +2（#21⑤）");
            int goldBefore = s.Eco.Gold;
            Assert.IsTrue(s.PickOre(oreId));

            var e1 = opened.Single();
            Assert.AreEqual(OreRewardType.Xp, e1.RewardType);
            Assert.AreEqual(4, e1.Amount);
            Assert.AreEqual(6, s.Eco.XpTotal, "2 + 4 = 6 经验");
            Assert.AreEqual(3, s.Eco.Level, "4 已过 L3 门槛 → L3（#21⑤）");
            Assert.AreEqual(goldBefore, s.Eco.Gold, "经验矿不碰钱包");
        }

        [Test]
        public void Open_Equipment_EventOnly_NoOtherSideEffects()
        {
            var bus = new EventBus();
            var opened = new List<OreOpenedEvent>();
            var gold = new List<GoldChangedEvent>();
            bus.Subscribe<OreOpenedEvent>(e => opened.Add(e));
            bus.Subscribe<GoldChangedEvent>(e => gold.Add(e));

            var ores = Pool(Gear(11, 7));
            var s = DropOne(bus, 1, ores, out int oreId);
            int goldBefore = s.Eco.Gold;
            int xpBefore = s.Eco.XpTotal;
            var playerUnit = s.PlayerBoard[0];
            Assert.IsTrue(s.PickOre(oreId));

            var e1 = opened.Single();
            Assert.AreEqual(OreRewardType.Equipment, e1.RewardType);
            Assert.AreEqual(7, e1.EquipmentId, "装备散件 id 原样传递（阶段3 背包消费者按它入包）");
            Assert.AreEqual(0, e1.Amount, "装备类 Amount 恒 0");
            Assert.AreEqual(goldBefore, s.Eco.Gold, "装备矿不碰钱包");
            Assert.AreEqual(xpBefore, s.Eco.XpTotal, "装备矿不碰经验");
            Assert.AreEqual(0, playerUnit.Modifiers.Count, "装备矿不挂任何修饰器（穿戴是阶段3 的事）");
        }

        // ─────────────────────── 确定性（同种子同操作 = 同奖励） ───────────────────────

        [Test]
        public void Open_SameSeedSameOps_SameReward()
        {
            // 三条金币池（抽取才有区分度）：同种子同操作 → 同掉落物 id + 同抽取结果
            var ores = Pool(Gold(11, 1), Gold(12, 2), Gold(13, 3));

            (int oreId, int goldGained) Play()
            {
                var s = New(new EventBus(), Tables(SingleChar(hp: 500, atk: 100, range: 5),
                    new EnemyTable(new[] { Round(1, Foe(30, 0, new Pos(4, 4), oreTier: 1, range: 5)) }), ores), 777u);
                Assert.IsTrue(s.StartRun());
                Assert.IsTrue(s.Buy(0));
                Assert.IsTrue(s.PlaceOnBoard(s.Bench[0].Id, new Pos(4, 0)));
                Assert.IsTrue(s.StartBattle());
                int guard = 0;
                while (!s.Combat.IsOver && guard++ < 6000) s.Combat.Tick();
                Assert.IsTrue(s.SettleBattle());
                Assert.IsTrue(s.StartPrep());
                int id = s.OresOnGround[0].Id;
                int goldBefore = s.Eco.Gold;
                Assert.IsTrue(s.PickOre(id));
                return (id, s.Eco.Gold - goldBefore);
            }

            var a = Play();
            var b = Play();
            Assert.AreEqual(a.oreId, b.oreId, "同种子同操作 → 同掉落物 id（IdAllocator 序一致）");
            Assert.AreEqual(a.goldGained, b.goldGained, "同种子同操作 → 同抽取结果（同档池同一条）");
            Assert.IsTrue(a.goldGained >= 1 && a.goldGained <= 3, "三条池抽中其一（1/2/3 金）");
        }
    }
}
