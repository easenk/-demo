using System;
using System.Collections.Generic;
using System.Linq;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    [TestFixture]
    public class SynergyTests
    {
        // ── 基建：注入表 + 摆位 + 重算 ──

        private static UnitStats Stats(int maxHp, int atk, float atkIv = 1.0f, float moveIv = 1.0f, int range = 5)
            => new UnitStats
            {
                MaxHp = maxHp, AttackPower = atk, Armor = 0, MagicResist = 0, AbilityPower = 100,
                AttackRange = range, AttackInterval = atkIv, MoveInterval = moveIv,
                CritChance = 0, CritDamage = 50,
            };

        /// <summary>注入表：N 个棋子按 tagsPerId 给标签（面板统一）——族 id 直接用开发计划 #22 的 1~6。</summary>
        private static CharacterTable Table(int maxHp, float atkIv, float moveIv, int atk, params int[][] tagsPerId)
        {
            var defs = new List<CharacterDef>();
            for (int i = 0; i < tagsPerId.Length; i++)
                defs.Add(new CharacterDef(i + 1, $"棋子{i + 1}", 1, Stats(maxHp, atk, atkIv, moveIv), tagsPerId[i]));
            return new CharacterTable(defs);
        }

        private static Unit Place(EventBus bus, BoardData board, int unitId, int charId, bool playerSide, Pos pos, UnitStats stats)
        {
            var u = new Unit(unitId, charId, playerSide, stats, bus);
            Assert.IsTrue(board.TryPlace(u, pos), $"摆位失败：{(playerSide ? "玩家" : "敌方")} {pos}");
            return u;
        }

        private static List<Unit> Roster(params Unit[] units) => new List<Unit>(units);

        private static SynergySystem NewSynergy(CharacterTable chars, EventBus bus)
            => new SynergySystem(chars, SynergyTable.Default, bus);

        private static void RunToEnd(CombatSystem combat, int maxTicks = 3000)
        {
            int guard = 0;
            while (!combat.IsOver && guard++ < maxTicks) combat.Tick();
        }

        // ── 18 棋子矩阵结构硬指标（#22③ 落表验收清单）──

        [Test]
        public void DefaultMatrix_StructureHardMetrics()
        {
            var table = CharacterTable.Default;
            Assert.AreEqual(18, table.Count, "#22②：18 种棋子");

            var perTribe = new Dictionary<int, int>();
            var lowCostPerTribe = new Dictionary<int, int>();   // 1~3 费成员数
            foreach (var def in table.All)
            {
                Assert.AreEqual(2, def.Synergies.Length, $"棋子{def.Id}（{def.DisplayName}）必须双标签");
                foreach (var tag in def.Synergies)
                {
                    perTribe.TryGetValue(tag, out var n);
                    perTribe[tag] = n + 1;
                    if (def.Cost <= 3)
                    {
                        lowCostPerTribe.TryGetValue(tag, out var m);
                        lowCostPerTribe[tag] = m + 1;
                    }
                }
            }

            Assert.AreEqual(36, perTribe.Values.Sum(), "18 × 2 = 36 个标签");
            foreach (int tribe in new[] { 1, 2, 3, 4, 5, 6 })
            {
                Assert.AreEqual(6, perTribe[tribe], $"族 {tribe} 必须正好 6 人");
                Assert.GreaterOrEqual(lowCostPerTribe[tribe], 4, $"族 {tribe} 的 1~3 费成员 ≥4（低费流不开 4 费凑 4 档）");
            }

            // 五个高费（4/5 费）棋子：双标签全部接得上 1~2 费骨架（"给五星凑阵容"永远可行）
            foreach (var def in table.All)
            {
                if (def.Cost < 4) continue;
                foreach (var tag in def.Synergies)
                {
                    bool linked = table.All.Any(other => other.Cost <= 2 && other.Synergies.Contains(tag));
                    Assert.IsTrue(linked, $"高费棋子{def.Id}（{def.DisplayName}）的标签 {tag} 必须有 1~2 费骨架");
                }
            }
        }

        // ── 表结构与构造校验 ──

        [Test]
        public void SynergyTable_Default_Structure()
        {
            var t = SynergyTable.Default;
            Assert.AreEqual(6, t.All.Count, "#22①：六族");
            for (int id = 1; id <= 6; id++)
            {
                var def = t.Get(id);
                Assert.AreEqual(id, def.Id);
                Assert.AreEqual(3, def.Tiers.Length, $"族 {id} 档位 2/4/6 三档");
                Assert.AreEqual(2, def.Tiers[0].Count);
                Assert.AreEqual(4, def.Tiers[1].Count);
                Assert.AreEqual(6, def.Tiers[2].Count);
            }
        }

        [Test]
        public void SynergyTable_Ctor_ValidationMatrix()
        {
            var tier2 = new SynergyTierDef(2, "x", traitModifiers: new[] { new StatModifier(StatKind.Armor, StatOp.Flat, 10f) });
            var tier4 = new SynergyTierDef(4, "y", traitModifiers: new[] { new StatModifier(StatKind.Armor, StatOp.Flat, 10f) });

            Assert.Throws<ArgumentNullException>(() => new SynergyTable(null));
            Assert.Throws<ArgumentException>(() => new SynergyTable(new[]   // Id 重复
            {
                new SynergyDef(1, "甲", new[] { tier2 }),
                new SynergyDef(1, "乙", new[] { tier2 }),
            }));
            Assert.Throws<ArgumentException>(() => new SynergyTable(new[]   // Id 越出羁绊号段
            {
                new SynergyDef(ModSourceId.EquipmentBase, "越段", new[] { tier2 }),
            }));
            Assert.Throws<ArgumentException>(() => new SynergyTable(new[]   // Id 0 = 非法
            {
                new SynergyDef(0, "零", new[] { tier2 }),
            }));
            Assert.Throws<ArgumentException>(() => new SynergyTable(new[]   // 档位不升序
            {
                new SynergyDef(1, "乱序", new[] { tier4, tier2 }),
            }));
            Assert.Throws<ArgumentException>(() => new SynergyTable(new[]   // 空档（无任何效果）
            {
                new SynergyDef(1, "空档", new[] { new SynergyTierDef(2, "z") }),
            }));
        }

        // ── 计数规则（D2-1：仅场上 / 同 CharId 去重）──

        [Test]
        public void Recalculate_CountsOnBoardOnly_DedupByCharId()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var chars = Table(100, 1f, 1f, 10, new[] { 5 }, new[] { 5 }, new[] { 1 });   // 两个暗影标签 + 一个蛮兽标签
            var sys = NewSynergy(chars, bus);

            var u1 = Place(bus, board, 1, 1, true, new Pos(0, 0), Stats(100, 10));   // 暗影标签
            var u2 = Place(bus, board, 2, 2, true, new Pos(1, 0), Stats(100, 10));   // 暗影标签（不同棋子）
            var u3 = Place(bus, board, 3, 3, true, new Pos(2, 0), Stats(100, 10));   // 蛮兽标签
            var dup = Place(bus, board, 4, 1, true, new Pos(3, 0), Stats(100, 10));  // u1 同 CharId 的第二份

            sys.Recalculate(Roster(u1, u2, u3, dup), board);

            Assert.AreEqual(2, sys.LastSnapshot[4].Count, "暗影（族5）= 2 个不同棋子（重复份不重复计数）");
            Assert.AreEqual(0, sys.LastSnapshot[4].TierIndex, "2 件 → 第 1 档");
            Assert.AreEqual(1, sys.LastSnapshot[0].Count, "蛮兽（族1）= 1");
        }

        [Test]
        public void Recalculate_BenchNotCounted_NoModsOnBench()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var chars = Table(100, 1f, 1f, 10, new[] { 2 }, new[] { 2 }, new[] { 2 });   // 三个铁壁标签
            var sys = NewSynergy(chars, bus);

            var t1 = Place(bus, board, 1, 1, true, new Pos(0, 0), Stats(100, 10));
            var t2 = Place(bus, board, 2, 2, true, new Pos(1, 0), Stats(100, 10));
            var bench = new Unit(3, 3, true, Stats(100, 10), bus);   // 备战席：不放棋盘
            var roster = Roster(t1, t2, bench);

            sys.Recalculate(roster, board);

            Assert.AreEqual(2, sys.LastSnapshot[1].Count, "计数只看场上：备战席不计");
            Assert.AreEqual(0, sys.LastSnapshot[1].TierIndex, "2 件 → 第 1 档");
            Assert.AreEqual(20, t1.EffectiveStats.Armor, "场上铁壁吃到护甲 +20（基础 0 → 20）");
            Assert.AreEqual(0, bench.EffectiveStats.Armor, "备战席不享受羁绊加成（2026-10-27 拍板：只挂场上）");
        }

        // ── 档位与覆盖制（2026-10-19 用户拍板：激活高档时高档增益覆盖低档）──

        [Test]
        public void Recalculate_HighestTier_OnlyActiveTierMounts()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var chars = Table(100, 1f, 1f, 100, new[] { 1 }, new[] { 1 }, new[] { 1 }, new[] { 1 });   // 4 蛮兽
            var sys = NewSynergy(chars, bus);
            var units = new List<Unit>();
            for (int i = 0; i < 4; i++)
                units.Add(Place(bus, board, i + 1, i + 1, true, new Pos(i, 0), Stats(100, 100)));

            sys.Recalculate(units, board);

            Assert.AreEqual(1, sys.LastSnapshot[0].TierIndex, "4 件 → 激活档 = 第 2 档（index 1）");

            // 覆盖制 + 完整效果集：只挂激活档（第 2 档），但 4 档条目自带完整集——攻速与攻击都在档内
            var s = units[0].EffectiveStats;
            Assert.AreEqual(115, s.AttackPower, "100 × (1+15/100) = 115（4 档条目自带）");
            Assert.AreEqual(0.88f, s.AttackInterval, 0.001f, "1.0 × (1-12/100) = 0.88（4 档条目自带低档效果）");
        }

        [Test]
        public void Recalculate_TierDowngrade_StripsHigherTier()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var chars = Table(100, 1f, 1f, 100, new[] { 1 }, new[] { 1 }, new[] { 1 }, new[] { 1 });
            var sys = NewSynergy(chars, bus);
            var units = new List<Unit>();
            for (int i = 0; i < 4; i++)
                units.Add(Place(bus, board, i + 1, i + 1, true, new Pos(i, 0), Stats(100, 100)));

            sys.Recalculate(units, board);       // 4 件：挂第 2 档（覆盖制）
            board.Remove(units[3]);              // 下场一个 → 3 件
            sys.Recalculate(units, board);       // 重算：退回第 1 档

            Assert.AreEqual(0, sys.LastSnapshot[0].TierIndex, "3 件 → 第 1 档");
            var s = units[0].EffectiveStats;
            Assert.AreEqual(100, s.AttackPower, "4 档攻击加成被按源摘干净");
            Assert.AreEqual(0.88f, s.AttackInterval, 0.001f, "退回 2 档：攻速 -12% 生效（覆盖制下的新激活档）");
        }

        [Test]
        public void Recalculate_MixedScope_SecretT6()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var chars = Table(100, 1f, 1f, 10,
                new[] { 3 }, new[] { 3 }, new[] { 3 }, new[] { 3 }, new[] { 3 }, new[] { 3 },   // 6 秘法
                new int[0]);                                                                     // 1 个无标签
            var sys = NewSynergy(chars, bus);
            var roster = new List<Unit>();
            for (int i = 0; i < 6; i++)
                roster.Add(Place(bus, board, i + 1, i + 1, true, new Pos(i, 0), Stats(100, 10)));
            var outsider = Place(bus, board, 7, 7, true, new Pos(6, 0), Stats(100, 10));
            roster.Add(outsider);

            sys.Recalculate(roster, board);

            Assert.AreEqual(2, sys.LastSnapshot[2].TierIndex, "6 件 → 第 3 档");

            var member = roster[0].EffectiveStats;
            Assert.AreEqual(145, member.AbilityPower, "6 档条目自带完整集：本族法强 +20 + 全队 +25 = 145");
            Assert.AreEqual(2, member.ManaRegenPerSecond, "6 档本族每秒回蓝 +2");

            var outStats = outsider.EffectiveStats;
            Assert.AreEqual(125, outStats.AbilityPower, "只吃全队法强 +25（本族 +20 只挂带标签者）");
            Assert.AreEqual(0, outStats.ManaRegenPerSecond, "本族回蓝不越界到队外（本族与全队的分界）");
        }

        // ── 快照事件 ──

        [Test]
        public void SynergiesChangedEvent_FiresOnChangeOnly_WithSnapshot()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var events = new List<SynergiesChangedEvent>();
            bus.Subscribe<SynergiesChangedEvent>(e => events.Add(e));

            var chars = Table(100, 1f, 1f, 10, new[] { 2 }, new[] { 2 });   // 2 铁壁
            var sys = NewSynergy(chars, bus);
            var u1 = Place(bus, board, 1, 1, true, new Pos(0, 0), Stats(100, 10));
            var u2 = Place(bus, board, 2, 2, true, new Pos(1, 0), Stats(100, 10));
            var roster = Roster(u1, u2);

            sys.Recalculate(roster, board);
            Assert.AreEqual(1, events.Count, "首算：从无到有 → 发");
            var snap = events[0].Snapshot;
            Assert.AreEqual(6, snap.Length, "快照逐族一行");
            Assert.AreEqual(2, snap[1].SynergyId);
            Assert.AreEqual(2, snap[1].Count);
            Assert.AreEqual(0, snap[1].TierIndex);
            Assert.IsNotNull(snap[1].TierDesc);
            Assert.AreEqual(-1, snap[0].TierIndex, "蛮兽未激活");
            Assert.IsNull(snap[0].TierDesc);

            sys.Recalculate(roster, board);   // 幂等重算
            Assert.AreEqual(1, events.Count, "无变化不发事件");

            board.Remove(u2);                 // 2 → 1：掉档
            sys.Recalculate(roster, board);
            Assert.AreEqual(2, events.Count, "变化才发");
            Assert.AreEqual(-1, events[1].Snapshot[1].TierIndex, "1 件：未激活");
        }

        // ── 钩子型羁绊（#22① 战斗中触发；RegisterBattleHooks 是唯一入口）──

        [Test]
        public void ShadowT2_LifestealHook_HealsAttacker()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var chars = Table(200, 1f, 1f, 50, new[] { 5 }, new[] { 5 });   // 2 暗影：atk 50
            var sys = NewSynergy(chars, bus);

            var a = Place(bus, board, 1, 1, true, new Pos(1, 1), Stats(200, 50));   // 暗影 A：离敌人最近（会被打缺血）
            var b = Place(bus, board, 2, 2, true, new Pos(0, 0), Stats(200, 50));   // 暗影 B：满血看戏
            var enemy = Place(bus, board, 10, 0, false, new Pos(3, 3), Stats(1000, 30, atkIv: 0.5f));   // 半秒一击
            var roster = Roster(a, b);

            sys.Recalculate(roster, board);
            var combat = new CombatSystem(board, bus, 11u);
            Assert.AreEqual(1, sys.RegisterBattleHooks(combat, board), "2 暗影 → 2 件档 1 个钩子");

            var heals = new List<(int UnitId, int Amount)>();
            bus.Subscribe<UnitHealedEvent>(e => heals.Add((e.UnitId, e.Amount)));
            for (int i = 0; i < 45; i++) combat.Tick();
            // 时间线：t15 敌打 A（170）→ t30 A 打敌命中 → 吸 6（176）→ t30/t45 敌续打 A

            Assert.AreEqual(1, heals.Count, "恰好一次吸血（A 首击满血静默、B 满血静默）");
            Assert.AreEqual(a.Id, heals[0].UnitId);
            Assert.AreEqual(6, heals[0].Amount, "applied 50 × 12% = 6（精确账目）");
        }

        [Test]
        public void ShadowT4_KillHealHook_HealsKiller()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var chars = Table(200, 2f, 1f, 100, new[] { 5 }, new[] { 5 }, new[] { 5 }, new[] { 5 });   // 4 暗影
            var sys = NewSynergy(chars, bus);

            // 杀手 K 近战贴脸（interval 2 秒 → 60 tick 首击），敌人先手把 K 打缺血，其余暗影还在走路
            var k = Place(bus, board, 1, 1, true, new Pos(2, 2), Stats(200, 100, atkIv: 2f, range: 1));
            Place(bus, board, 2, 2, true, new Pos(0, 0), Stats(200, 100, atkIv: 2f, range: 1));
            Place(bus, board, 3, 3, true, new Pos(1, 0), Stats(200, 100, atkIv: 2f, range: 1));
            Place(bus, board, 4, 4, true, new Pos(2, 0), Stats(200, 100, atkIv: 2f, range: 1));
            Place(bus, board, 10, 0, false, new Pos(3, 3), Stats(100, 30, atkIv: 0.5f, range: 5));   // 敌 D：先手打 K（攻击力压到 30——避免与 K 首击同 tick 的刀刃判定）
            var roster = Roster(k,   // 其余三个暗影由上面 Place 返回，这里只需名册齐整——重取：
                board.GetOccupant(new Pos(0, 0)), board.GetOccupant(new Pos(1, 0)), board.GetOccupant(new Pos(2, 0)));

            sys.Recalculate(roster, board);
            var combat = new CombatSystem(board, bus, 11u);
            Assert.AreEqual(2, sys.RegisterBattleHooks(combat, board), "4 暗影 → 激活 4 档条目自带两个钩子（吸血 + 击杀回血）");

            var heals = new List<(int UnitId, int Amount)>();
            bus.Subscribe<UnitHealedEvent>(e => heals.Add((e.UnitId, e.Amount)));
            RunToEnd(combat);
            // 时间线：t15/30/45(/60) 敌打 K（至多掉 120 血，稳活）→ t60±1 K 首击一击杀 D
            //   → 4 档条目完整集：先吸血 12 → 击杀回血 20

            Assert.AreEqual(2, heals.Count, "击杀那一下：先吸血（12）后击杀回血（20）");
            Assert.AreEqual(k.Id, heals[0].UnitId);
            Assert.AreEqual(12, heals[0].Amount, "applied 100 × 12% = 12");
            Assert.AreEqual(k.Id, heals[1].UnitId);
            Assert.AreEqual(20, heals[1].Amount, "K 最大生命 200 × 10% = 20（#22④：死亡钩子携带击杀者）");
            Assert.IsTrue(combat.PlayerWon);
        }

        [Test]
        public void ShadowFullT246_LifestealKillHealKillPush()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var chars = Table(200, 1f, 1f, 100, new[] { 5 }, new[] { 5 }, new[] { 5 }, new[] { 5 }, new[] { 5 }, new[] { 5 });   // 6 暗影
            var sys = NewSynergy(chars, bus);

            // 杀手 K：interval 0.5 秒（15 tick 首击，早于所有人）+ 离 D2 最近（会被 D2 打缺血）
            var k = Place(bus, board, 1, 1, true, new Pos(2, 2), Stats(200, 100, atkIv: 0.5f));
            var s2 = Place(bus, board, 2, 2, true, new Pos(0, 0), Stats(200, 100));
            var s3 = Place(bus, board, 3, 3, true, new Pos(1, 0), Stats(200, 100));
            var s4 = Place(bus, board, 4, 4, true, new Pos(0, 1), Stats(200, 100));
            var s5 = Place(bus, board, 5, 5, true, new Pos(1, 1), Stats(200, 100));
            var s6 = Place(bus, board, 6, 6, true, new Pos(0, 2), Stats(200, 100));
            var d1 = Place(bus, board, 10, 0, false, new Pos(3, 3), Stats(100, 0));      // 一击死的靶子
            var d2 = Place(bus, board, 11, 0, false, new Pos(4, 3), Stats(1000, 50, atkIv: 0.3f));   // 打不死 + 会打 K
            var roster = Roster(k, s2, s3, s4, s5, s6);

            sys.Recalculate(roster, board);
            var combat = new CombatSystem(board, bus, 11u);
            Assert.AreEqual(3, sys.RegisterBattleHooks(combat, board), "6 暗影 → 激活 6 档条目自带三个钩子（吸血/回血/推条）");

            var heals = new List<(int UnitId, int Amount)>();
            bus.Subscribe<UnitHealedEvent>(e => heals.Add((e.UnitId, e.Amount)));
            var attacks = new List<(int Tick, int AttackerId, int TargetId)>();
            bus.Subscribe<UnitAttackedEvent>(e => attacks.Add((combat.TickCount, e.AttackerId, e.TargetId)));
            RunToEnd(combat);
            // 时间线：t9 D2 打 K（150）→ t15 K 击杀 D1（吸血 12、击杀回血 20、推条 0.5）
            //   → t23 K 打 D2（推条生效：8 tick 而非 15）→ t30 五个暗影齐射 D2 → t60 D2 死、战斗终

            Assert.AreEqual(1, heals.Count(h => h.Amount == 20 && h.UnitId == k.Id), "击杀回血 20 恰好一次（唯一一次击杀 = D1）");
            Assert.GreaterOrEqual(heals.Count(h => h.Amount == 12 && h.UnitId == k.Id), 1, "吸血 12 至少一次");

            var killTick = attacks.First(x => x.TargetId == d1.Id).Tick;   // 击杀 D1 的那击
            var nextTick = attacks.First(x => x.AttackerId == k.Id && x.Tick > killTick).Tick;
            Assert.LessOrEqual(nextTick - killTick, 10, "推条 50%：下一击 ~8 tick 内（不推条要 15 tick）");
            Assert.IsTrue(combat.PlayerWon);
        }

        [Test]
        public void IronwallT6_TakeDamageReduction()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            // 6 铁壁（含 T）+ 1 个无标签 N；覆盖制 + 完整效果集：6 档条目自带护甲 +20 → 减伤与抗性串联结算
            var chars = Table(1000, 2f, 1f, 5,
                new[] { 2 }, new[] { 2 }, new[] { 2 }, new[] { 2 }, new[] { 2 }, new[] { 2 },
                new int[0]);
            var sys = NewSynergy(chars, bus);

            var t = Place(bus, board, 1, 1, true, new Pos(3, 2), Stats(1000, 5, atkIv: 2f));   // 铁壁 T：E2 的目标
            var n = Place(bus, board, 7, 7, true, new Pos(4, 2), Stats(1000, 5, atkIv: 2f));   // 非铁壁 N：E1 的目标
            Place(bus, board, 2, 2, true, new Pos(0, 0), Stats(1000, 5, atkIv: 2f));
            Place(bus, board, 3, 3, true, new Pos(1, 0), Stats(1000, 5, atkIv: 2f));
            Place(bus, board, 4, 4, true, new Pos(2, 0), Stats(1000, 5, atkIv: 2f));
            Place(bus, board, 5, 5, true, new Pos(0, 1), Stats(1000, 5, atkIv: 2f));
            Place(bus, board, 6, 6, true, new Pos(1, 1), Stats(1000, 5, atkIv: 2f));
            Place(bus, board, 10, 0, false, new Pos(7, 3), Stats(1000, 100, atkIv: 1f));   // E1：锁 N
            Place(bus, board, 11, 0, false, new Pos(0, 5), Stats(1000, 100, atkIv: 1f));   // E2：并列锁 T（行主序先到）
            var roster = Roster(t, n,
                board.GetOccupant(new Pos(0, 0)), board.GetOccupant(new Pos(1, 0)), board.GetOccupant(new Pos(2, 0)),
                board.GetOccupant(new Pos(0, 1)), board.GetOccupant(new Pos(1, 1)));

            sys.Recalculate(roster, board);
            var combat = new CombatSystem(board, bus, 11u);
            Assert.AreEqual(1, sys.RegisterBattleHooks(combat, board), "6 铁壁 → 只有 6 档一个钩子");

            var damages = new List<(int UnitId, int FinalDamage)>();
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add((e.UnitId, e.FinalDamage)));
            for (int i = 0; i < 45; i++) combat.Tick();   // t30：E1 打 N、E2 打 T（各自唯一一击）

            Assert.AreEqual(100, damages.First(d => d.UnitId == n.Id).FinalDamage, "非铁壁：100 全额（护甲 0、无减伤）");
            Assert.AreEqual(73, damages.First(d => d.UnitId == t.Id).FinalDamage,
                "铁壁 T：100 → 承伤 ×100/120（6 档条目自带护甲+20）= 83.33 → 再 ×(1-12%)（6 档减伤钩）= 73.33 → 取整 73");
        }

        [Test]
        public void BeastT6_StackingCapsAtTen()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var chars = Table(1000, 1f, 1f, 10, new[] { 1 }, new[] { 1 }, new[] { 1 }, new[] { 1 }, new[] { 1 }, new[] { 1 });   // 6 蛮兽
            var sys = NewSynergy(chars, bus);

            var a = Place(bus, board, 1, 1, true, new Pos(0, 0), Stats(1000, 10));
            Place(bus, board, 2, 2, true, new Pos(1, 0), Stats(1000, 10));
            Place(bus, board, 3, 3, true, new Pos(2, 0), Stats(1000, 10));
            Place(bus, board, 4, 4, true, new Pos(3, 0), Stats(1000, 10));
            Place(bus, board, 5, 5, true, new Pos(4, 0), Stats(1000, 10));
            Place(bus, board, 6, 6, true, new Pos(5, 0), Stats(1000, 10));
            Place(bus, board, 10, 0, false, new Pos(3, 3), Stats(100000, 0));   // 打不死的木桩
            var roster = Roster(a,
                board.GetOccupant(new Pos(1, 0)), board.GetOccupant(new Pos(2, 0)),
                board.GetOccupant(new Pos(3, 0)), board.GetOccupant(new Pos(4, 0)), board.GetOccupant(new Pos(5, 0)));

            sys.Recalculate(roster, board);
            var combat = new CombatSystem(board, bus, 11u);
            sys.RegisterBattleHooks(combat, board);

            var intervals = new List<float>();
            bus.Subscribe<UnitAttackedEvent>(e =>
            {
                if (e.AttackerId == a.Id)
                    intervals.Add(a.EffectiveStats.AttackInterval);
                // 出手时刻读到的是"上一击命中后"的层数（命中叠层在 OnAfterAttack，晚于出手广播）
            });
            for (int i = 0; i < 480; i++) combat.Tick();   // 叠层后出手加速，480 tick 足够跨过 10 层封顶点（木桩打不死，战斗不结束）

            // 第 k 击时刻的间隔 = 1.0 × (1 + (-12 - 3×已叠层数)/100)（6 档条目自带 -12% + 每层 -3%，百分比相加不叠乘；
            // 出手时刻读到的是"上一击命中后"的层数——命中叠层在 OnAfterAttack，晚于出手广播）
            Assert.GreaterOrEqual(intervals.Count, 11, "必须跨过 10 层封顶点");
            Assert.AreEqual(0.88f, intervals[0], 0.001f, "第 1 击：尚无叠层（条目自带的 -12% 已生效）");
            Assert.AreEqual(0.85f, intervals[1], 0.001f, "第 2 击：已叠 1 层（-12% + -3% = -15%）");
            for (int i = 10; i < intervals.Count; i++)
                Assert.AreEqual(0.58f, intervals[i], 0.002f, "第 11 击起：10 层封顶（-42%）且此后稳定");
            Assert.AreEqual(0.58f, a.EffectiveStats.AttackInterval, 0.001f, "战斗中读：封顶值稳定");
        }

        [Test]
        public void RegisterBattleHooks_CountsMixedTribes()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var chars = Table(100, 1f, 1f, 10,
                new[] { 1 }, new[] { 1 }, new[] { 1 }, new[] { 1 }, new[] { 1 }, new[] { 1 },       // 6 蛮兽
                new[] { 5 }, new[] { 5 }, new[] { 5 }, new[] { 5 }, new[] { 5 }, new[] { 5 },       // 6 暗影
                new[] { 3 }, new[] { 3 });                                                           // 2 秘法（2 档无钩子）
            var sys = NewSynergy(chars, bus);
            var roster = new List<Unit>();
            for (int id = 1; id <= 14; id++)
                roster.Add(Place(bus, board, id, id, true, new Pos((id - 1) % 8, (id - 1) / 8), Stats(100, 10)));

            sys.Recalculate(roster, board);
            var combat = new CombatSystem(board, bus, 11u);
            Assert.AreEqual(4, sys.RegisterBattleHooks(combat, board),
                "覆盖制+完整效果集：6 蛮兽（6 档条目 1 工厂）+ 6 暗影（6 档条目 3 工厂）+ 2 秘法（2 档 0 钩）= 4 个钩子");
        }
    }
}
