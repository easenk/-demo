using System;
using System.Collections.Generic;
using System.Linq;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    /// <summary>
    /// 阶段3 批次5：战斗钩子内容补全（阶段3-接口设计 第七章，决策 #16）。
    ///
    /// 锁死的契约：
    ///  · CombatHookLibrary 七钩子：固定穿透/百分比穿透/增伤/减伤（成员制+全队制）/击碎削抗（叠层封顶）/普攻吸血（成员制）/击杀回复（成员制）
    ///  · 全部经既有六挂点生效——CombatSystem 零改动（本批生产代码只新增一个文件）
    ///  · 改伤四乘区（穿透/增伤/减伤）挂 OnBeforeAttack：普攻与技能共享（批次2 口径）
    ///  · 击碎挂 OnDamaged（守方视角"被伤害时叠削抗"）：普攻与技能命中都触发
    ///  · 吸血挂 OnAfterAttack（攻方收尾）：仅普攻触发（技能不触发，批次2 口径）
    ///  · 击杀回复挂 OnDeath：普攻与技能击杀都触发（HandleKill 共用收尾）
    ///  · 击碎 = 守方负向抗性修饰器（抗性可压负 → 负值公式 2-100/(100-抗性)，决策 #16②）
    ///  · 回归红线：不注册任何钩子时与批次4 行为逐字节一致（既有 397 条全绿即证）
    ///  · 不引入新随机源（全部无随机）
    /// </summary>
    [TestFixture]
    public class CombatHookLibraryTests
    {
        // ── 基建 ──

        private static UnitStats Stats(int hp = 1000, int atk = 100, int armor = 0, int mr = 0)
            => new UnitStats
            {
                MaxHp = hp, AttackPower = atk, Armor = armor, MagicResist = mr, AbilityPower = 0,
                AttackRange = 5, AttackInterval = 1f, MoveInterval = 1f,
                CritChance = 0, CritDamage = 50, MaxMana = 0, ManaPerHit = 0, ManaRegenPerSecond = 0,
            };

        private static Unit Place(EventBus bus, BoardData board, int id, bool playerSide, Pos pos, UnitStats stats)
        {
            var u = new Unit(id, 0, playerSide, stats, bus);
            // 摆位钳己方半场（玩家 row 0~2、敌方 row 3~5）——敌方必须摆 row 3~5，测试统一用 (1,4)/(3,4)
            Pos cell = playerSide ? pos : new Pos(pos.X, pos.Y + 3);
            Assert.IsTrue(board.TryPlace(u, cell), $"摆位失败 {cell}");
            return u;
        }

        // ── 出手前：改伤四乘区（普攻与技能共享） ──

        [Test]
        public void FlatPenHook_ExactLedger()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));

            var player = Place(bus, board, 1, true, new Pos(0, 0), Stats(atk: 100));
            var enemy = Place(bus, board, 2, false, new Pos(1, 1), Stats(hp: 10000, atk: 0, armor: 60));

            var combat = new CombatSystem(board, bus, 11u);
            combat.AddHook(new CombatHookLibrary.FlatPenHook(30, ModSourceId.TestBase));
            int guard = 0;
            while (guard++ < 35) combat.Tick();   // 30~31 tick 首击（±1 步浮点抖动）

            var hitsOnEnemy = damages.Where(d => d.UnitId == enemy.Id).ToList();
            Assert.IsNotEmpty(hitsOnEnemy, "首击已发生");
            // 固定穿透 30：有效抗性 = 60-30 = 30 → 承伤系数 100/130 → 100×(100/130) = 76.92 → 77
            Assert.AreEqual(77, hitsOnEnemy[0].FinalDamage, "固定穿透精确账目：100 × 100/(100+60-30) = 77");
        }

        [Test]
        public void PercentPenHook_ExactLedger()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));

            var player = Place(bus, board, 1, true, new Pos(0, 0), Stats(atk: 100));
            var enemy = Place(bus, board, 2, false, new Pos(1, 1), Stats(hp: 10000, atk: 0, armor: 60));

            var combat = new CombatSystem(board, bus, 11u);
            combat.AddHook(new CombatHookLibrary.PercentPenHook(50, ModSourceId.TestBase));   // 无视 50% 抗性
            int guard = 0;
            while (guard++ < 35) combat.Tick();

            var hitsOnEnemy = damages.Where(d => d.UnitId == enemy.Id).ToList();
            Assert.IsNotEmpty(hitsOnEnemy);
            // 百分比穿透 50%：有效抗性 = 60×(1-0.5) = 30 → 同上 77
            Assert.AreEqual(77, hitsOnEnemy[0].FinalDamage, "百分比穿透精确账目：100 × 100/(100+60×0.5) = 77");
        }

        [Test]
        public void DamageAmpHook_ExactLedger()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));

            var player = Place(bus, board, 1, true, new Pos(0, 0), Stats(atk: 100));
            var enemy = Place(bus, board, 2, false, new Pos(1, 1), Stats(hp: 10000, atk: 0));

            var combat = new CombatSystem(board, bus, 11u);
            combat.AddHook(new CombatHookLibrary.DamageAmpHook(20f, ModSourceId.TestBase));
            int guard = 0;
            while (guard++ < 35) combat.Tick();

            var hitsOnEnemy = damages.Where(d => d.UnitId == enemy.Id).ToList();
            Assert.IsNotEmpty(hitsOnEnemy);
            Assert.AreEqual(120, hitsOnEnemy[0].FinalDamage, "增伤 20%：100 × 1.2 = 120（独立乘区，无抗性）");
        }

        [Test]
        public void DamageReductionHook_MemberScope_ExactLedger()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));

            var player = Place(bus, board, 1, true, new Pos(0, 0), Stats(atk: 100));
            var enemyA = Place(bus, board, 2, false, new Pos(0, 1), Stats(hp: 10000, atk: 0));   // 成员（减伤）——最近目标（距离 1）
            var enemyB = Place(bus, board, 3, false, new Pos(3, 1), Stats(hp: 10000, atk: 0));   // 非成员——远（距离 3），不被锁定

            var combat = new CombatSystem(board, bus, 11u);
            combat.AddHook(new CombatHookLibrary.DamageReductionHook(new[] { enemyA }, 20f, ModSourceId.TestBase));
            int guard = 0;
            while (guard++ < 95) combat.Tick();

            // 玩家锁定最近的 enemyA（距离 1 < 3）——所有攻击打 A：成员减伤生效
            var hitsOnA = damages.Where(d => d.UnitId == enemyA.Id).ToList();
            Assert.IsNotEmpty(hitsOnA, "对成员的攻击发生");
            Assert.IsTrue(hitsOnA.All(d => d.FinalDamage == 80), "成员被打全是 80：100 × (1-20%)");
            Assert.IsFalse(damages.Any(d => d.UnitId == enemyA.Id && d.FinalDamage == 100), "成员不该吃到全额");
        }

        [Test]
        public void DamageReductionHook_Clamp90()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));

            var player = Place(bus, board, 1, true, new Pos(0, 0), Stats(atk: 100));
            var enemy = Place(bus, board, 2, false, new Pos(1, 1), Stats(hp: 10000, atk: 0));

            var combat = new CombatSystem(board, bus, 11u);
            combat.AddHook(new CombatHookLibrary.DamageReductionHook(150f, ModSourceId.TestBase));   // 超 90% 上限
            int guard = 0;
            while (guard++ < 35) combat.Tick();

            var hitsOnEnemy = damages.Where(d => d.UnitId == enemy.Id).ToList();
            Assert.IsNotEmpty(hitsOnEnemy);
            Assert.AreEqual(10, hitsOnEnemy[0].FinalDamage, "减伤钳 90% 上限：100 × (1-0.9) = 10（永不全免）");
        }

        // ── 击碎/削抗（挂 OnDamaged：普攻与技能命中都触发） ──

        [Test]
        public void ShredHook_StacksAndCaps_ArmorGoesNegative()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));

            var player = Place(bus, board, 1, true, new Pos(0, 0), Stats(atk: 100));
            var enemy = Place(bus, board, 2, false, new Pos(1, 1), Stats(hp: 100000, atk: 0, armor: 20));

            var combat = new CombatSystem(board, bus, 11u);
            combat.AddHook(new CombatHookLibrary.ShredHook(StatKind.Armor, 15, 3, ModSourceId.TestBase));   // 每击 -15 甲，最多 3 层
            int guard = 0;
            while (guard++ < 200) combat.Tick();   // 攻速 1 秒 → 6~7 击

            var hitsOnEnemy = damages.Where(d => d.UnitId == enemy.Id).ToList();
            Assert.IsTrue(hitsOnEnemy.Count >= 4, $"至少 4 击（实际 {hitsOnEnemy.Count}）");
            // 第 1 击：甲 20 → 100×100/120 = 83.3 → 83；击碎后甲 5
            // 第 2 击：甲 5 → 100×100/105 = 95.2 → 95；击碎后甲 -10
            // 第 3 击：甲 -10 → 负值公式 2-100/110 = 1.0909 → 100×1.0909 = 109.09 → 109；击碎后甲 -25（封顶）
            // 第 4 击起：甲 -25 → 2-100/125 = 1.2 → 120
            Assert.AreEqual(83, hitsOnEnemy[0].FinalDamage, "第 1 击：甲 20，伤害 83");
            Assert.AreEqual(95, hitsOnEnemy[1].FinalDamage, "第 2 击：甲 5（碎 1 层），伤害 95");
            Assert.AreEqual(109, hitsOnEnemy[2].FinalDamage, "第 3 击：甲 -10（碎 2 层，负值公式生效），伤害 109");
            Assert.AreEqual(120, hitsOnEnemy[3].FinalDamage, "第 4 击：甲 -25（碎 3 层封顶），伤害 120");
        }

        [Test]
        public void ShredHook_RejectsNonResistKind()
        {
            Assert.Throws<ArgumentException>(() =>
                new CombatHookLibrary.ShredHook(StatKind.MaxHp, 10, 3, ModSourceId.TestBase), "击碎只作用于抗性维");
        }

        [Test]
        public void ShredHook_TriggersOnSkillDamage_Too()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var casts = new List<SkillCastEvent>();
            bus.Subscribe<SkillCastEvent>(e => casts.Add(e));

            var casterStats = new UnitStats
            {
                MaxHp = 1000, AttackPower = 10, Armor = 0, MagicResist = 0, AbilityPower = 100,
                AttackRange = 5, AttackInterval = 1f, MoveInterval = 1f,
                CritChance = 0, CritDamage = 50, MaxMana = 30, ManaPerHit = 30, ManaRegenPerSecond = 0,
            };
            var caster = new Unit(1, 13, true, casterStats, bus, 1, 13);   // charId 13 = 战地法师；skillId 13（奥术飞弹）
            Assert.IsTrue(board.TryPlace(caster, new Pos(2, 1)));
            caster.AddMana(30);   // 直充拉满 → 首行动位施法
            var enemy = Place(bus, board, 2, false, new Pos(2, 0), Stats(hp: 100000, atk: 0));   // 距离 1，施法距离 3 内

            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default);
            combat.AddHook(new CombatHookLibrary.ShredHook(StatKind.Armor, 15, 3, ModSourceId.TestBase));
            int guard = 0;
            while (guard++ < 5) combat.Tick();

            Assert.IsNotEmpty(casts, "施法发生");
            // 施法命中 → OnDamaged 触发 → 敌人身上有一条 -15 甲修饰器（SourceId = 测试章）
            Assert.AreEqual(1, enemy.Modifiers.Count(m => m.Kind == StatKind.Armor
                && m.SourceId == ModSourceId.TestBase), "击碎对技能命中同样触发（OnDamaged 挂点）");
        }

        // ── 吸血（挂 OnAfterAttack：仅普攻触发） ──

        [Test]
        public void LifestealHook_MemberScope_ExactLedger()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var heals = new List<UnitHealedEvent>();
            bus.Subscribe<UnitHealedEvent>(e => heals.Add(e));

            var player = Place(bus, board, 1, true, new Pos(0, 0), Stats(hp: 1000, atk: 100));
            var enemy = Place(bus, board, 2, false, new Pos(1, 1), Stats(hp: 100000, atk: 50));   // 敌方 50 攻 → 玩家会掉血 → 吸血可回

            var combat = new CombatSystem(board, bus, 11u);
            combat.AddHook(new CombatHookLibrary.LifestealHook(new[] { player }, 12f, ModSourceId.TestBase));
            int guard = 0;
            while (guard++ < 100) combat.Tick();

            Assert.IsNotEmpty(heals, "玩家被敌方打掉血后，普攻吸血触发回血");
            Assert.IsTrue(heals.All(h => h.UnitId == player.Id), "成员制：只有玩家吸血（敌方不吸）");
            Assert.IsTrue(heals.Any(h => h.Amount == 12), "吸血精确账目：100 × 12% = 12");
        }

        [Test]
        public void LifestealHook_DoesNotTrigger_OnSkillCast()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var heals = new List<UnitHealedEvent>();
            var casts = new List<SkillCastEvent>();
            bus.Subscribe<UnitHealedEvent>(e => heals.Add(e));
            bus.Subscribe<SkillCastEvent>(e => casts.Add(e));

            var casterStats = new UnitStats
            {
                MaxHp = 1000, AttackPower = 10, Armor = 0, MagicResist = 0, AbilityPower = 100,
                AttackRange = 5, AttackInterval = 1f, MoveInterval = 1f,
                CritChance = 0, CritDamage = 50, MaxMana = 30, ManaPerHit = 30, ManaRegenPerSecond = 0,
            };
            var caster = new Unit(1, 13, true, casterStats, bus, 1, 13);   // charId 13 = 战地法师；skillId 13（奥术飞弹）
            Assert.IsTrue(board.TryPlace(caster, new Pos(2, 1)));
            caster.AddMana(30);
            var enemy = Place(bus, board, 2, false, new Pos(3, 0), Stats(hp: 100000, atk: 0));

            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default);
            combat.AddHook(new CombatHookLibrary.LifestealHook(new[] { caster }, 12f, ModSourceId.TestBase));
            int guard = 0;
            while (guard++ < 5) combat.Tick();

            Assert.IsNotEmpty(casts, "施法发生");
            Assert.IsEmpty(heals, "吸血不触发（OnAfterAttack 不对施法触发——批次2 口径回归）");
        }

        // ── 击杀回复（挂 OnDeath：普攻与技能击杀都触发） ──

        [Test]
        public void KillHealHook_ExactLedger()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var heals = new List<UnitHealedEvent>();
            bus.Subscribe<UnitHealedEvent>(e => heals.Add(e));

            var player = Place(bus, board, 1, true, new Pos(0, 0), Stats(hp: 500, atk: 200));   // 2 击杀 300 血敌
            var enemy = Place(bus, board, 2, false, new Pos(1, 1), Stats(hp: 300, atk: 100));   // 敌方 100 攻 → 玩家先掉血再回

            var combat = new CombatSystem(board, bus, 11u);
            combat.AddHook(new CombatHookLibrary.KillHealHook(10f, ModSourceId.TestBase));
            int guard = 0;
            while (!combat.IsOver && guard++ < 200) combat.Tick();

            Assert.IsTrue(combat.IsOver && combat.PlayerWon, "玩家 200 攻 2 击杀 300 血敌人");
            Assert.IsTrue(heals.Any(h => h.UnitId == player.Id && h.Amount == 50),
                "击杀回复精确账目：500（有效上限）× 10% = 50");
        }

        [Test]
        public void KillHealHook_MemberScope_EmptyList_NoHeal()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var heals = new List<UnitHealedEvent>();
            bus.Subscribe<UnitHealedEvent>(e => heals.Add(e));

            var player = Place(bus, board, 1, true, new Pos(0, 0), Stats(hp: 500, atk: 200));
            var enemy = Place(bus, board, 2, false, new Pos(1, 1), Stats(hp: 300, atk: 0));

            var combat = new CombatSystem(board, bus, 11u);
            combat.AddHook(new CombatHookLibrary.KillHealHook(new Unit[0], 10f, ModSourceId.TestBase));   // 空成员名单
            int guard = 0;
            while (!combat.IsOver && guard++ < 200) combat.Tick();

            Assert.IsTrue(combat.IsOver && combat.PlayerWon);
            Assert.IsFalse(heals.Any(h => h.UnitId == player.Id && h.Amount == 50), "非成员击杀不回血");
        }

        // ── 施法与改伤钩子的交互（普攻/技能共享改伤挂点） ──

        [Test]
        public void DamageAmpHook_AppliesToSkillDamage_Too()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var damages = new List<UnitDamagedEvent>();
            var casts = new List<SkillCastEvent>();
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));
            bus.Subscribe<SkillCastEvent>(e => casts.Add(e));

            var casterStats = new UnitStats
            {
                MaxHp = 1000, AttackPower = 10, Armor = 0, MagicResist = 0, AbilityPower = 100,
                AttackRange = 5, AttackInterval = 1f, MoveInterval = 1f,
                CritChance = 0, CritDamage = 50, MaxMana = 30, ManaPerHit = 30, ManaRegenPerSecond = 0,
            };
            var caster = new Unit(1, 13, true, casterStats, bus, 1, 13);   // charId 13 = 战地法师；skillId 13（奥术飞弹）
            Assert.IsTrue(board.TryPlace(caster, new Pos(2, 1)));
            caster.AddMana(30);
            var enemy = Place(bus, board, 2, false, new Pos(3, 0), Stats(hp: 100000, atk: 0));

            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default);
            combat.AddHook(new CombatHookLibrary.DamageAmpHook(20f, ModSourceId.TestBase));
            int guard = 0;
            while (guard++ < 5) combat.Tick();

            Assert.IsNotEmpty(casts, "施法发生");
            var hitsOnEnemy = damages.Where(d => d.UnitId == enemy.Id).ToList();
            Assert.IsNotEmpty(hitsOnEnemy);
            // 技能基础 115（60 + 55×100/100）× 增伤 1.2 = 138
            Assert.AreEqual(138, hitsOnEnemy[0].FinalDamage, "增伤钩子对技能伤害同样生效：115 × 1.2 = 138");
        }

        // ── 7c-1 扩充：装备大件 v2 / 策略 v2 的效果钩子（穿戴者制 + 成员制类型增伤 + 起手蓝） ──

        [Test]
        public void KillPenStackHook_StacksOnKill_CapsAtMax()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));

            var player = Place(bus, board, 1, true, new Pos(0, 0), Stats(atk: 100));
            var weak = Place(bus, board, 2, false, new Pos(1, 1), Stats(hp: 50, atk: 0, armor: 60));   // 首击 63 即死
            var enemy = Place(bus, board, 3, false, new Pos(2, 2), Stats(hp: 100000, atk: 0, armor: 60));

            var combat = new CombatSystem(board, bus, 11u);
            combat.AddHook(new CombatHookLibrary.KillPenStackHook(player, 10, 50, ModSourceId.TestBase));
            int guard = 0;
            while (guard++ < 95) combat.Tick();   // 首击 + 杀 weak + 对 enemy 三击（0 层→1 层对比）

            var onEnemy = damages.Where(d => d.UnitId == enemy.Id).ToList();
            Assert.GreaterOrEqual(onEnemy.Count, 2, "对大血敌至少两击");
            // 杀 weak 前（0 层）：100 × 100/160 = 62.5 → 63；杀后（1 层 10%）：有效抗性 60×0.9=54 → 100×100/154 = 64.9 → 65
            Assert.AreEqual(65, onEnemy[0].FinalDamage, "击杀后下一击吃 10% 穿透：100 × 100/(100+54) = 65");
        }

        [Test]
        public void AttackAmpStackHook_StacksPerHit_CapsAtMax()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));

            var player = Place(bus, board, 1, true, new Pos(0, 0), Stats(atk: 100));
            Place(bus, board, 2, false, new Pos(1, 1), Stats(hp: 1000000, atk: 0));

            var combat = new CombatSystem(board, bus, 11u);
            combat.AddHook(new CombatHookLibrary.AttackAmpStackHook(player, 5, 50, ModSourceId.TestBase));
            int guard = 0;
            while (guard++ < 12 * 31) combat.Tick();   // 12 次出手（攻击间隔 1s = 30~31 tick）

            var hits = damages.Where(d => d.SourceUnitId == player.Id).ToList();
            Assert.GreaterOrEqual(hits.Count, 11, "至少 11 击（10 层叠满 + 封顶验证）");
            Assert.AreEqual(100, hits[0].FinalDamage, "首击 0 层 = 100");
            Assert.AreEqual(105, hits[1].FinalDamage, "第二击 1 层 = 100 × 1.05");
            Assert.AreEqual(110, hits[2].FinalDamage, "第三击 2 层 = 100 × 1.10");
            for (int i = 10; i < hits.Count; i++)
                Assert.AreEqual(150, hits[i].FinalDamage, $"第 {i + 1} 击封顶 50%：100 × 1.50");
        }

        [Test]
        public void LostHpRegenHook_HealsPercentOfMissingHp_EverySecond()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var heals = new List<UnitHealedEvent>();
            bus.Subscribe<UnitHealedEvent>(e => heals.Add(e));

            var wearer = Place(bus, board, 1, true, new Pos(0, 0), Stats(hp: 10000, atk: 0));
            Place(bus, board, 2, false, new Pos(1, 1), Stats(hp: 100000, atk: 500));   // t39 首击 -500

            var combat = new CombatSystem(board, bus, 11u);
            combat.AddHook(new CombatHookLibrary.LostHpRegenHook(wearer, 5, ModSourceId.TestBase));
            int guard = 0;
            while (guard++ < 65) combat.Tick();   // 2 秒出头：t39 重创 → t60 首跳回复

            Assert.IsNotEmpty(heals, "回复已发生");
            Assert.AreEqual(25, heals[0].Amount, "每秒回已损 5%：500 × 0.05 = 25（MaxHp 回复不掺和）");
        }

        [Test]
        public void MaxHpRegenHook_HealsPercentOfMaxHp_EveryPeriod()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var heals = new List<UnitHealedEvent>();
            bus.Subscribe<UnitHealedEvent>(e => heals.Add(e));

            var wearer = Place(bus, board, 1, true, new Pos(0, 0), Stats(hp: 10000, atk: 0));
            Place(bus, board, 2, false, new Pos(1, 1), Stats(hp: 100000, atk: 500));   // t39 重创（制造不满血）

            var combat = new CombatSystem(board, bus, 11u);
            combat.AddHook(new CombatHookLibrary.MaxHpRegenHook(wearer, 2.5f, 2, ModSourceId.TestBase));
            int guard = 0;
            while (guard++ < 65) combat.Tick();

            Assert.IsNotEmpty(heals, "回复已发生");
            Assert.AreEqual(250, heals[0].Amount, "每 2 秒回最大生命 2.5%：10000 × 0.025 = 250");
        }

        [Test]
        public void BurnAuraHook_BurnsAdjacentEnemies_Periodically()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));

            var wearer = Place(bus, board, 1, true, new Pos(2, 2), Stats(hp: 10000, atk: 0));    // 玩家排最前排（row 2）
            var near = Place(bus, board, 2, false, new Pos(2, 0), Stats(hp: 10000, atk: 0));   // (2,3)：切比雪夫 1 → 圈内
            var far = Place(bus, board, 3, false, new Pos(6, 1), Stats(hp: 8000, atk: 0));     // (6,4)：切比雪夫 4 → 圈外

            var combat = new CombatSystem(board, bus, 11u);
            combat.AddHook(new CombatHookLibrary.BurnAuraHook(wearer, 2.5f, 2, ModSourceId.TestBase));
            int guard = 0;
            while (guard++ < 65) combat.Tick();   // t60 首跳（2 秒周期）

            var onNear = damages.Where(d => d.UnitId == near.Id).ToList();
            var onFar = damages.Where(d => d.UnitId == far.Id).ToList();
            // 注意：atk 0 的佩戴者仍会普攻（保底 1 伤害）——按伤害值断言光环那一跳，不数总条数
            Assert.IsTrue(onNear.Any(d => d.FinalDamage == 250), "圈内敌吃到光环一跳 = 其最大生命 2.5%（10000 × 0.025）");
            var auraHit = onNear.First(d => d.FinalDamage == 250);
            Assert.AreEqual(wearer.Id, auraHit.SourceUnitId, "伤害来源 = 穿戴者（事件流可归因）");
            Assert.IsTrue(onFar.All(d => d.FinalDamage != 250), "圈外敌不被灼伤（只有保底 1 的普攻噪声）");
        }

        [Test]
        public void TypeDamageReductionHook_OnlyMatchingType()
        {
            // 段一：挂 Physical 30% —— 普攻（物理）被减：100 × 0.7 = 70
            {
                var bus = new EventBus();
                var board = new BoardData(bus);
                var damages = new List<UnitDamagedEvent>();
                bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));
                var wearer = Place(bus, board, 1, true, new Pos(0, 0), Stats(hp: 100000, atk: 0));
                Place(bus, board, 2, false, new Pos(1, 1), Stats(atk: 100));

                var combat = new CombatSystem(board, bus, 11u);
                combat.AddHook(new CombatHookLibrary.TypeDamageReductionHook(wearer, DamageType.Physical, 30f, ModSourceId.TestBase));
                int guard = 0;
                while (guard++ < 45) combat.Tick();   // 敌攻间隔 1s → t30 首击

                var onWearer = damages.Where(d => d.UnitId == wearer.Id).ToList();
                Assert.IsNotEmpty(onWearer);
                Assert.AreEqual(70, onWearer[0].FinalDamage, "类型匹配：物理普攻 100 × (1-0.3) = 70");
            }

            // 段二：挂 Magic 30% —— 普攻（物理）不受影响
            {
                var bus = new EventBus();
                var board = new BoardData(bus);
                var damages = new List<UnitDamagedEvent>();
                bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));
                var wearer = Place(bus, board, 1, true, new Pos(0, 0), Stats(hp: 100000, atk: 0));
                Place(bus, board, 2, false, new Pos(1, 1), Stats(atk: 100));

                var combat = new CombatSystem(board, bus, 11u);
                combat.AddHook(new CombatHookLibrary.TypeDamageReductionHook(wearer, DamageType.Magic, 30f, ModSourceId.TestBase));
                int guard = 0;
                while (guard++ < 45) combat.Tick();

                var onWearer = damages.Where(d => d.UnitId == wearer.Id).ToList();
                Assert.IsNotEmpty(onWearer);
                Assert.AreEqual(100, onWearer[0].FinalDamage, "类型不匹配：法术减伤对物理普攻无效");
            }
        }

        [Test]
        public void TypeDamageAmpHook_MemberOnly_MatchingType()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));

            var member = Place(bus, board, 1, true, new Pos(0, 0), Stats(atk: 100));
            var outsider = Place(bus, board, 3, true, new Pos(7, 0), Stats(atk: 100));
            var enemyA = Place(bus, board, 2, false, new Pos(1, 1), Stats(hp: 100000, atk: 0));   // member 的最近敌
            var enemyB = Place(bus, board, 4, false, new Pos(6, 1), Stats(hp: 100000, atk: 0));   // outsider 的最近敌

            var combat = new CombatSystem(board, bus, 11u);
            combat.AddHook(new CombatHookLibrary.TypeDamageAmpHook(new[] { member }, DamageType.Physical, 30f, ModSourceId.TestBase));
            int guard = 0;
            while (guard++ < 35) combat.Tick();

            var onA = damages.Where(d => d.UnitId == enemyA.Id).ToList();
            var onB = damages.Where(d => d.UnitId == enemyB.Id).ToList();
            Assert.AreEqual(130, onA[0].FinalDamage, "成员物理伤害 +30%：100 × 1.3");
            Assert.AreEqual(100, onB[0].FinalDamage, "非成员不吃增伤");
        }

        [Test]
        public void InitialMana_AppliedAtBattleStart_ClampedByMaxMana()
        {
            // 有蓝条：起手蓝 15 → 开战 CurrentMana = 15；超额 70 → 钳到上限 60
            {
                var bus = new EventBus();
                var board = new BoardData(bus);
                var casterStats = new UnitStats
                {
                    MaxHp = 1000, AttackPower = 10, Armor = 0, MagicResist = 0, AbilityPower = 100,
                    AttackRange = 5, AttackInterval = 1f, MoveInterval = 1f,
                    CritChance = 0, CritDamage = 50, MaxMana = 60, ManaPerHit = 10, ManaRegenPerSecond = 0,
                };
                var caster = new Unit(1, 0, true, casterStats, bus);
                Assert.IsTrue(board.TryPlace(caster, new Pos(0, 0)));
                caster.AddModifier(new StatModifier(StatKind.InitialMana, StatOp.Flat, 15f, ModSourceId.TestBase));
                caster.AddModifier(new StatModifier(StatKind.InitialMana, StatOp.Flat, 55f, ModSourceId.TestBase + 1));   // 合计 70 > 60

                Place(bus, board, 2, false, new Pos(1, 1), Stats(hp: 100000, atk: 0));
                var combat = new CombatSystem(board, bus, 11u);
                combat.Tick();   // 首 tick：起手蓝入账（先于任何回蓝/施法）

                Assert.AreEqual(60, caster.CurrentMana, "起手蓝 70 钳到 MaxMana 60（AddMana 上限语义）");
            }

            // 无蓝条：起手蓝静默（AddMana 对 MaxMana=0 不建账本）
            {
                var bus = new EventBus();
                var board = new BoardData(bus);
                var plain = Place(bus, board, 1, true, new Pos(0, 0), Stats());
                plain.AddModifier(new StatModifier(StatKind.InitialMana, StatOp.Flat, 15f, ModSourceId.TestBase));
                Place(bus, board, 2, false, new Pos(1, 1), Stats(hp: 100000, atk: 0));
                var combat = new CombatSystem(board, bus, 11u);
                combat.Tick();
                Assert.AreEqual(0, plain.CurrentMana, "无蓝条单位：起手蓝静默不生效");
            }
        }
    }
}
