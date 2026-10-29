using System;
using System.Collections.Generic;
using System.Linq;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    /// <summary>
    /// 阶段3 批次2：施法循环（阶段3-接口设计 4.3；2026-10-16 用户拍板口径）。
    ///
    /// 锁死的契约：
    ///  · 行动位最前判定"蓝满 + 有技能 + 目标在施法距离内" → 施法替代本次普攻/移动（本行动位不充条不出手）
    ///  · 打断语义：命中回满的当次普攻照常完成（伤害落地），下个行动位施法
    ///  · 施法后清零（SkillDef.KeepManaAfterCast = true 保留——v1 表全 false）
    ///  · 施法失败（无目标/超距/无技能表/无技能）不扣蓝、落回原普攻/移动逻辑
    ///  · 不引入新随机源：物理技能才掷骰、暴击率 0 不掷（RollCount 焊死）；魔法/真实技能永不掷
    ///  · 技能伤害走 DamageFormula 同一条乘区管线：魔法走法抗（护甲无效）、真实不打折、物理可暴击
    ///  · 出手前钩子（改伤四乘区）/受伤后/死亡钩子对施法照常触发；OnAfterAttack（普攻收尾）不对施法触发
    ///  · 敌方怪可配技能（编成表 SkillId → Session 生成 Unit 时带技能 id）
    /// </summary>
    [TestFixture]
    public class SkillCastTests
    {
        // ── 基建 ──

        /// <summary>施法者面板：默认法强 100（= 技能基准威力）、无暴击、攻速 1 秒。</summary>
        private static UnitStats CasterStats(int maxMana, int manaPerHit = 0, int abilityPower = 100,
            int attackPower = 20, int critChance = 0, float atkIv = 1f, int range = 1, float moveIv = 1f)
            => new UnitStats
            {
                MaxHp = 500, AttackPower = attackPower, Armor = 0, MagicResist = 0,
                AbilityPower = abilityPower, AttackRange = range, AttackInterval = atkIv, MoveInterval = moveIv,
                CritChance = critChance, CritDamage = 50,
                MaxMana = maxMana, ManaPerHit = manaPerHit, ManaRegenPerSecond = 0,
            };

        /// <summary>木桩：打不死（血厚）、不还手（攻 0）、走得慢（10 秒一格）——隔离出"只有施法者动"的时间线。</summary>
        private static UnitStats DummyStats(int hp = 100000, int armor = 0, int mr = 0)
            => new UnitStats
            {
                MaxHp = hp, AttackPower = 0, Armor = armor, MagicResist = mr,
                AbilityPower = 0, AttackRange = 1, AttackInterval = 10f, MoveInterval = 10f,
                CritChance = 0, CritDamage = 0, MaxMana = 0, ManaPerHit = 0, ManaRegenPerSecond = 0,
            };

        /// <summary>减伤 20% 的改伤钩子（铁壁 6 档同款机制）：验证技能伤害吃出手前钩子。</summary>
        private sealed class ReductionHook : IBattleHook
        {
            public int SourceId => ModSourceId.TestBase;
            public void OnBattleStart(BattleContext ctx) { }
            public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft)
            {
                if (draft.Target != null) draft.DamageReductionPercent += 20f;
            }
            public void OnAfterAttack(BattleContext ctx, in AttackResult result) { }
            public void OnDamaged(BattleContext ctx, in DamageResult result) { }
            public void OnDeath(BattleContext ctx, in DeathInfo death) { }
            public void OnTickEnd(BattleContext ctx) { }
        }

        /// <summary>钩子触发计数器：验证出手前/出手后/死亡钩子对施法的触发口径。</summary>
        private sealed class RecorderHook : IBattleHook
        {
            public int SourceId => ModSourceId.TestBase;
            public int BeforeAttack, AfterAttack, Damaged, Death;
            public DeathInfo LastDeath;
            public void OnBattleStart(BattleContext ctx) { }
            public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft) => BeforeAttack++;
            public void OnAfterAttack(BattleContext ctx, in AttackResult result) => AfterAttack++;
            public void OnDamaged(BattleContext ctx, in DamageResult result) => Damaged++;
            public void OnDeath(BattleContext ctx, in DeathInfo death) { Death++; LastDeath = death; }
            public void OnTickEnd(BattleContext ctx) { }
        }

        // ── 基本链路：行动位施法 + 精确账目 ──

        [Test]
        public void Cast_ManaFullTargetInRange_CastsAtFirstActionSlot_WithExactDamage()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            // 战地法师技能（id 3）：魔法、1 星基础 30、法强系数 40、施法距离 3
            var caster = new Unit(1, 13, true, CasterStats(30, range: 5), bus, 1, 3);
            var dummy = new Unit(2, 0, false, DummyStats(), bus);
            Assert.IsTrue(board.TryPlace(caster, new Pos(2, 1)));
            Assert.IsTrue(board.TryPlace(dummy, new Pos(3, 3)));   // 切比雪夫距离 2 ≤ 施法距离 3；普攻射程 5 也够得着
            caster.AddMana(30);                                       // 直充拉满（充蓝的 Mana 事件发生在订阅前，不进断言）

            var casts = new List<SkillCastEvent>();
            var damages = new List<UnitDamagedEvent>();
            var attacks = new List<UnitAttackedEvent>();
            var manas = new List<ManaChangedEvent>();
            bus.Subscribe<SkillCastEvent>(e => casts.Add(e));
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));
            bus.Subscribe<UnitAttackedEvent>(e => attacks.Add(e));
            bus.Subscribe<ManaChangedEvent>(e => manas.Add(e));

            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default);
            combat.Tick();   // 第一个行动位即施法

            Assert.AreEqual(1, casts.Count, "蓝满 + 有技能 + 在施法距离内 → 首个行动位施法");
            Assert.AreEqual(caster.Id, casts[0].CasterId);
            Assert.AreEqual(3, casts[0].SkillId);
            Assert.AreEqual(dummy.Id, casts[0].TargetId);
            Assert.IsFalse(casts[0].IsCrit, "魔法技能不可暴击");
            Assert.AreEqual(0, attacks.Count, "施法占据行动位：该 tick 没有普攻（普攻射程够得着也没出手）");
            Assert.AreEqual(1, damages.Count);
            Assert.AreEqual(dummy.Id, damages[0].UnitId);
            Assert.AreEqual(70, damages[0].FinalDamage, "1 星战地法师：30 + 40×100/100 = 70（法抗 0）");
            Assert.AreEqual(0, caster.CurrentMana, "施法后清零");
            Assert.AreEqual(1, manas.Count, "战斗内只有清蓝一条蓝事件");
            Assert.AreEqual(0, manas[0].CurrentMana);
        }

        [Test]
        public void Cast_MagicSkill_AppliesMagicResist_IgnoresArmor()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var caster = new Unit(1, 13, true, CasterStats(30), bus, 1, 3);
            var dummy = new Unit(2, 0, false, DummyStats(armor: 500, mr: 40), bus);
            board.TryPlace(caster, new Pos(2, 2));
            board.TryPlace(dummy, new Pos(3, 3));
            caster.AddMana(30);

            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default);
            combat.Tick();

            Assert.AreEqual(50, damages[0].FinalDamage, "魔法技能：70 × 100/(100+40) = 50（护甲 500 无关）");
        }

        [Test]
        public void Cast_TrueSkill_BypassesAllResistances()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            // 魔渊兽王技能（id 8）：真实、1 星基础 70、法强系数 35、施法距离 2
            var caster = new Unit(1, 18, true, CasterStats(30), bus, 1, 8);
            var dummy = new Unit(2, 0, false, DummyStats(armor: 500, mr: 500), bus);
            board.TryPlace(caster, new Pos(2, 2));
            board.TryPlace(dummy, new Pos(4, 3));   // 切比雪夫距离 2 ≤ 施法距离 2
            caster.AddMana(30);

            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default);
            combat.Tick();

            Assert.AreEqual(105, damages[0].FinalDamage, "真实技能：70 + 35×100/100 = 105（双抗 500 全部无视）");
        }

        [Test]
        public void Cast_PhysicalSkill_RollsExactlyOnceAndCanCrit()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            // 战团督军技能（id 1）：物理、1 星基础 40、法强系数 35、施法距离 1
            var caster = new Unit(1, 11, true, CasterStats(30, critChance: 100), bus, 1, 1);
            var dummy = new Unit(2, 0, false, DummyStats(), bus);
            board.TryPlace(caster, new Pos(2, 2));
            board.TryPlace(dummy, new Pos(3, 3));   // 距离 1
            caster.AddMana(30);

            var casts = new List<SkillCastEvent>();
            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<SkillCastEvent>(e => casts.Add(e));
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default);
            combat.Tick();

            Assert.AreEqual(1, combat.RollCount, "物理技能暴击率 > 0：施法恰好掷一次");
            Assert.IsTrue(casts[0].IsCrit, "暴击率 100% 必中");
            Assert.AreEqual(113, damages[0].FinalDamage, "75 × 1.5 = 112.5 → 113（暴伤 +50%）");
        }

        [Test]
        public void Cast_NeverRolls_WhenCritChanceZeroOrSkillNonPhysical()
        {
            // (a) 物理技能 + 暴击率 0：不掷、不暴击
            {
                var bus = new EventBus();
                var board = new BoardData(bus);
                var caster = new Unit(1, 11, true, CasterStats(30, critChance: 0), bus, 1, 1);
                var dummy = new Unit(2, 0, false, DummyStats(), bus);
                board.TryPlace(caster, new Pos(2, 2));
                board.TryPlace(dummy, new Pos(3, 3));
                caster.AddMana(30);
                var casts = new List<SkillCastEvent>();
                var damages = new List<UnitDamagedEvent>();
                bus.Subscribe<SkillCastEvent>(e => casts.Add(e));
                bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));
                var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default);
                combat.Tick();
                Assert.AreEqual(0, combat.RollCount, "暴击率 0 不掷（D9：随机序列消耗点与行为一一对应）");
                Assert.IsFalse(casts[0].IsCrit);
                Assert.AreEqual(75, damages[0].FinalDamage, "40 + 35×100/100 = 75");
            }
            // (b) 魔法技能 + 暴击率 100：也不掷（只有物理可暴击）
            {
                var bus = new EventBus();
                var board = new BoardData(bus);
                var caster = new Unit(1, 13, true, CasterStats(30, critChance: 100), bus, 1, 3);
                var dummy = new Unit(2, 0, false, DummyStats(), bus);
                board.TryPlace(caster, new Pos(2, 1));
                board.TryPlace(dummy, new Pos(3, 3));
                caster.AddMana(30);
                var casts = new List<SkillCastEvent>();
                bus.Subscribe<SkillCastEvent>(e => casts.Add(e));
                var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default);
                combat.Tick();
                Assert.AreEqual(0, combat.RollCount, "魔法技能永不掷骰");
                Assert.IsFalse(casts[0].IsCrit);
            }
        }

        // ── 失败路径：不扣蓝、落回原逻辑 ──

        [Test]
        public void Cast_OutOfCastRange_HoldsMana_FallsBackToNormalLogic()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            // 施法距离 3、普攻射程 1、移动 10 秒一格（90 tick 内走不动）：目标距离 4 → 只能干等
            var caster = new Unit(1, 13, true, CasterStats(30, range: 1, moveIv: 10f), bus, 1, 3);
            var dummy = new Unit(2, 0, false, DummyStats(), bus);
            board.TryPlace(caster, new Pos(2, 1));
            board.TryPlace(dummy, new Pos(6, 4));   // 切比雪夫距离 4 > 施法距离 3
            caster.AddMana(30);

            var casts = new List<SkillCastEvent>();
            bus.Subscribe<SkillCastEvent>(e => casts.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default);
            for (int i = 0; i < 90; i++) combat.Tick();

            Assert.IsEmpty(casts, "超距不施法");
            Assert.AreEqual(30, caster.CurrentMana, "施法失败不扣蓝");
            Assert.AreEqual(0, combat.RollCount, "没出手就没掷骰");
        }

        [Test]
        public void Cast_NoSkillTable_NeverCasts()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var caster = new Unit(1, 13, true, CasterStats(30), bus, 1, 3);
            var dummy = new Unit(2, 0, false, DummyStats(), bus);
            board.TryPlace(caster, new Pos(2, 1));
            board.TryPlace(dummy, new Pos(3, 3));
            caster.AddMana(30);

            var casts = new List<SkillCastEvent>();
            bus.Subscribe<SkillCastEvent>(e => casts.Add(e));
            var combat = new CombatSystem(board, bus, 11u);   // 不传技能表（测试/未注入配置的兜底）
            for (int i = 0; i < 60; i++) combat.Tick();

            Assert.IsEmpty(casts, "无技能表：蓝满也永不施法");
            Assert.AreEqual(30, caster.CurrentMana, "蓝白攒着（回归红线：与批次1 行为一致）");
        }

        [Test]
        public void Cast_SkillIdZero_NeverCasts()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var caster = new Unit(1, 13, true, CasterStats(30), bus, 1, 0);   // 有蓝条但无技能（数据事故形态）
            var dummy = new Unit(2, 0, false, DummyStats(), bus);
            board.TryPlace(caster, new Pos(2, 1));
            board.TryPlace(dummy, new Pos(3, 3));
            caster.AddMana(30);

            var casts = new List<SkillCastEvent>();
            bus.Subscribe<SkillCastEvent>(e => casts.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default);
            for (int i = 0; i < 60; i++) combat.Tick();

            Assert.IsEmpty(casts, "SkillId=0：永不施法（编表纪律：有蓝条就该有技能）");
        }

        // ── 打断语义（2026-10-16 拍板：命中回满的当次普攻照常完成 + 下个行动位施法）──

        [Test]
        public void Cast_ManaFilledByHit_AttackCompletesThenCastsAtNextActionSlot()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            // 蓝条 20、命中回蓝 10：两次普攻打满；攻速 1 秒、射程 5（从开战就能打）
            var caster = new Unit(1, 13, true, CasterStats(20, manaPerHit: 10, range: 5), bus, 1, 3);
            var dummy = new Unit(2, 0, false, DummyStats(), bus);
            board.TryPlace(caster, new Pos(2, 1));
            board.TryPlace(dummy, new Pos(3, 3));

            var log = new List<string>();
            bus.Subscribe<UnitAttackedEvent>(e => log.Add($"A|{e.AttackerId}"));
            bus.Subscribe<UnitDamagedEvent>(e => log.Add($"D|{e.UnitId}|{e.FinalDamage}"));
            bus.Subscribe<ManaChangedEvent>(e => log.Add($"M|{e.CurrentMana}"));
            bus.Subscribe<SkillCastEvent>(e => log.Add($"C|{e.CasterId}"));

            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default);
            int guard = 0;
            while (log.Count(l => l.StartsWith("C|", StringComparison.Ordinal)) == 0 && guard++ < 300)
                combat.Tick();

            Assert.AreEqual(1, log.Count(l => l.StartsWith("C|", StringComparison.Ordinal)), "施法必然发生（300 tick 内）");
            // 两次普攻把蓝打满：每次"出手 → 见血 → 回蓝"，之后才是施法
            Assert.AreEqual(2, log.Count(l => l == $"A|{caster.Id}"), "恰好两次普攻（第二次命中把蓝打满）");
            Assert.AreEqual(3, log.Count(l => l.StartsWith("M|", StringComparison.Ordinal)), "蓝事件：10 → 20 → 0（施法清零）");
            Assert.IsTrue(log.SequenceEqual(new[]
            {
                $"A|{caster.Id}", $"D|{dummy.Id}|20", "M|10",       // 第 1 击（普攻伤害 = 攻 20）
                $"A|{caster.Id}", $"D|{dummy.Id}|20", "M|20",       // 第 2 击：命中回蓝到满，本次普攻照常完成
                $"C|{caster.Id}", $"D|{dummy.Id}|70", "M|0",        // 下个行动位施法：70 点魔法伤害 + 清蓝
            }), "打断语义：命中回满的当次普攻照常完成、下个行动位施法、施法后清零");
            Assert.AreEqual(0, caster.CurrentMana);
        }

        [Test]
        public void Cast_KeepManaAfterCast_True_PreservesManaAndCastsEverySlot()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var keepSkill = new SkillDef(99, "连发型", DamageType.Magic, 10, 10, 10, 0, 0, 3, SkillTargetRule.NearestEnemy, true);
            var caster = new Unit(1, 13, true, CasterStats(10), bus, 1, 99);
            var dummy = new Unit(2, 0, false, DummyStats(), bus);
            board.TryPlace(caster, new Pos(2, 1));
            board.TryPlace(dummy, new Pos(3, 3));
            caster.AddMana(10);

            var casts = new List<SkillCastEvent>();
            bus.Subscribe<SkillCastEvent>(e => casts.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: new SkillTable(new[] { keepSkill }));
            for (int i = 0; i < 5; i++) combat.Tick();

            Assert.AreEqual(5, casts.Count, "保留型：蓝不清 → 每个行动位都施法（字面语义，v1 表不使用）");
            Assert.AreEqual(10, caster.CurrentMana, "保留型：施法后蓝原样保留");
        }

        // ── 钩子口径：改伤共享、普攻收尾不触发 ──

        [Test]
        public void Cast_BeforeAttackHook_ModifiesSkillDamage()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var caster = new Unit(1, 13, true, CasterStats(30), bus, 1, 3);
            var dummy = new Unit(2, 0, false, DummyStats(), bus);
            board.TryPlace(caster, new Pos(2, 1));
            board.TryPlace(dummy, new Pos(3, 3));
            caster.AddMana(30);

            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default);
            combat.AddHook(new ReductionHook());   // 守方减伤 +20%（铁壁 6 档同款机制）
            combat.Tick();

            Assert.AreEqual(56, damages[0].FinalDamage, "技能伤害吃出手前钩子的减伤乘区：70 × (1-20%) = 56");
        }

        [Test]
        public void Cast_DoesNotFireAfterAttackHook()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var caster = new Unit(1, 13, true, CasterStats(30, range: 5), bus, 1, 3);
            var dummy = new Unit(2, 0, false, DummyStats(), bus);
            board.TryPlace(caster, new Pos(2, 1));
            board.TryPlace(dummy, new Pos(3, 3));
            caster.AddMana(30);

            var recorder = new RecorderHook();
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default);
            combat.AddHook(recorder);
            combat.Tick();   // 施法恰好一次

            Assert.GreaterOrEqual(recorder.BeforeAttack, 1, "出手前钩子（改伤挂点）对施法照常触发");
            Assert.AreEqual(0, recorder.AfterAttack, "出手后钩子（普攻收尾：吸血/叠层）不对施法触发——词条口径是普攻专属");
            Assert.AreEqual(1, recorder.Damaged, "受伤后钩子对施法照常触发");
        }

        // ── 击杀链 ──

        [Test]
        public void Cast_Kill_FiresDeathChain_AndClearsBoard()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var caster = new Unit(1, 13, true, CasterStats(30), bus, 1, 3);
            var dummy = new Unit(2, 0, false, DummyStats(hp: 50), bus);
            board.TryPlace(caster, new Pos(2, 1));
            board.TryPlace(dummy, new Pos(3, 3));
            caster.AddMana(30);

            var log = new List<string>();
            bus.Subscribe<SkillCastEvent>(e => log.Add($"C|{e.CasterId}"));
            bus.Subscribe<UnitDiedEvent>(e => log.Add($"X|{e.UnitId}"));

            var recorder = new RecorderHook();
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default);
            combat.AddHook(recorder);
            combat.Tick();

            Assert.AreEqual(1, recorder.Death, "死亡钩子对施法击杀照常触发");
            Assert.AreSame(caster, recorder.LastDeath.Killer, "击杀者 = 施法者（暗影击杀类效果的数据源）");
            Assert.AreEqual(new[] { $"C|{caster.Id}", $"X|{dummy.Id}" }, log, "先施法后死亡（时序契约）");
            Assert.IsTrue(combat.IsOver && combat.PlayerWon, "目标清场 → 战斗终局判胜");
        }

        // ── 敌方施法 ──

        [Test]
        public void Cast_EnemyUnit_CastsAtPlayerUnit()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var player = new Unit(1, 0, true, DummyStats(), bus);       // 玩家侧木桩
            var enemyCaster = new Unit(2, 0, false, CasterStats(30), bus, 1, 3);
            board.TryPlace(player, new Pos(2, 1));
            board.TryPlace(enemyCaster, new Pos(3, 3));
            enemyCaster.AddMana(30);

            var casts = new List<SkillCastEvent>();
            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<SkillCastEvent>(e => casts.Add(e));
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default);
            combat.Tick();

            Assert.AreEqual(1, casts.Count);
            Assert.AreEqual(enemyCaster.Id, casts[0].CasterId, "敌方单位同样按行动位施法");
            Assert.AreEqual(player.Id, casts[0].TargetId);
            Assert.AreEqual(70, damages[0].FinalDamage);
        }

        [Test]
        public void Session_EnemySpawn_SkillId_FlowsIntoCombat()
        {
            var bus = new EventBus();
            var casts = new List<SkillCastEvent>();
            bus.Subscribe<SkillCastEvent>(e => casts.Add(e));

            Assert.AreSame(SkillTable.Default, GameTables.Default.Skills, "Default 捆八表：技能表已接线");

            // 会施法的怪：蓝条 10、命中回蓝 10（一击回满）、每秒回蓝 2——三条路都会攒满，命中最快
            var casterFoe = new UnitStats
            {
                MaxHp = 400, AttackPower = 10, Armor = 0, MagicResist = 0, AbilityPower = 100,
                AttackRange = 1, AttackInterval = 1.5f, MoveInterval = 1f,
                CritChance = 0, CritDamage = 0, MaxMana = 10, ManaPerHit = 10, ManaRegenPerSecond = 2,
            };
            var tables = new GameTables
            {
                Characters = CharacterTable.Default,
                Odds = ShopOdds.Default,
                Rounds = RoundTable.Default,
                Enemies = new EnemyTable(new[]
                {
                    new EnemyRoundEntry(1, new EnemySpawn[]
                    {
                        new EnemySpawn("会施法的怪", casterFoe, new Pos(4, 4), 0, 1, skillId: 3),
                    }),
                }),
                Synergies = SynergyTable.Default,
                Ores = OreTable.Default,
                Policies = new PolicyTable(new PolicyDef[0]),
                Skills = SkillTable.Default,
            };
            var s = new GameSession(bus, 0u, tables);
            Assert.IsTrue(s.StartRun());

            // 买一个 1 费棋子上场当靶子（起始 3 金、L2 全 1 费 100%）
            int slot = Array.FindIndex(s.Shop.Offers, o => o.Id != 0);
            Assert.GreaterOrEqual(slot, 0);
            Assert.IsTrue(s.Buy(slot));
            Assert.IsTrue(s.PlaceOnBoard(s.Bench[0].Id, new Pos(4, 0)));

            Assert.IsTrue(s.StartBattle());
            int guard = 0;
            while (!s.Combat.IsOver && guard++ < 4500) s.Combat.Tick();

            Assert.IsNotEmpty(casts, "编成表 SkillId → Session 生成敌方 Unit 时带上 → 战斗中施法（整条链路）");
            Assert.AreEqual(s.EnemyRoster[0].Id, casts[0].CasterId);
        }

        // ── 确定性 ──

        [Test]
        public void Determinism_WithCasting_SameSeedSameEventStream()
        {
            string RunOnce(uint seed)
            {
                var bus = new EventBus();
                var log = new List<string>();
                bus.Subscribe<SkillCastEvent>(e => log.Add($"C|{e.CasterId}|{e.SkillId}|{e.TargetId}|{e.IsCrit}"));
                bus.Subscribe<UnitDamagedEvent>(e => log.Add($"D|{e.UnitId}|{e.SourceUnitId}|{e.FinalDamage}"));
                bus.Subscribe<ManaChangedEvent>(e => log.Add($"M|{e.UnitId}|{e.CurrentMana}"));

                var board = new BoardData(bus);
                var player = new Unit(1, 13, true, CasterStats(30, abilityPower: 100, range: 5), bus, 1, 3);
                var enemy = new Unit(2, 18, false, CasterStats(30, attackPower: 0, range: 5, moveIv: 1f), bus, 1, 8);
                board.TryPlace(player, new Pos(2, 1));
                board.TryPlace(enemy, new Pos(5, 4));   // 距离 3：玩家技能 3 距离够、敌方技能 8（距离 2）需先走近
                player.AddMana(30);
                enemy.AddMana(30);

                var combat = new CombatSystem(board, bus, seed, skills: SkillTable.Default);
                for (int i = 0; i < 150 && !combat.IsOver; i++) combat.Tick();
                return string.Join(";", log);
            }

            Assert.AreEqual(RunOnce(77u), RunOnce(77u), "施法不引入新随机源：同种子双战斗事件流逐条一致");
        }
    }
}
