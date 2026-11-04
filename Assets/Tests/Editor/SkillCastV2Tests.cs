using System;
using System.Collections.Generic;
using System.Linq;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    /// <summary>
    /// 平衡期 7c-2：技能系统 v2 的契约锁（多目标/形状/纯 buff/附带 Buff/Buff 生产接线）。
    ///
    /// 锁死的契约：
    ///  · Default 表 v2 结构：18 条连号、类型分布 4 物/2 法/1 真/11 纯 buff、BuffId 全部可解析
    ///  · 目标规则 8 值：最低血（距离内筛选+并列先扫到）/锚点形状（多人受击+贴边截断）/
    ///    自身形状（形状即门禁：空=不施法不扣蓝）/整行整列十字/Box 偶数维向右下/半场（施法者视角敌方半场）
    ///  · 纯 buff 技能：无伤害事件、无暴击掷骰、清蓝、面板修饰器入账、到期回落
    ///  · 友方目标规则：攻最高（含自身+并列先扫到）/生命比例最低（交叉相乘精确——绝对值最低不等于比例最低）
    ///  · 全局前置：敌方场上无人 → 任何规则都不施法（含纯 buff）
    ///  · 多目标：行主序逐个受击、SkillCastEvent 恰一条（主目标=名单首位）、
    ///    一次掷骰全目标共享暴击、出手前/受伤后钩子每目标独立触发、击杀收尾每目标走 HandleKill
    ///  · 附带 Buff：击杀目标不挂（毒不上尸体）、存活者挂层、来源章 600+技能 id、
    ///    无内建 BuffSystem 时静默跳过（伤害照常）
    ///  · Buff 生产接线：CombatSystem 构造器收 BuffTable → 内建账本 → 每 tick 第 6.5 步结算；
    ///    GameSession 生产链恒有 BuffSystem（Buffs 非空）
    /// </summary>
    [TestFixture]
    public class SkillCastV2Tests
    {
        // ── 基建（与 SkillCastTests 同款口径，互不共享实例） ──

        /// <summary>施法者面板：默认法强 100、无暴击、攻速 1 秒。</summary>
        private static UnitStats Caster(int maxMana, int manaPerHit = 0, int abilityPower = 100,
            int attackPower = 20, int critChance = 0, float atkIv = 1f, int range = 1, float moveIv = 1f)
            => new UnitStats
            {
                MaxHp = 500, AttackPower = attackPower, Armor = 0, MagicResist = 0,
                AbilityPower = abilityPower, AttackRange = range, AttackInterval = atkIv, MoveInterval = moveIv,
                CritChance = critChance, CritDamage = 50,
                MaxMana = maxMana, ManaPerHit = manaPerHit, ManaRegenPerSecond = 0,
            };

        /// <summary>木桩：打不死、不还手、走得慢——隔离出"只有施法者动"的时间线。</summary>
        private static UnitStats Dummy(int hp = 100000, int armor = 0, int mr = 0)
            => new UnitStats
            {
                MaxHp = hp, AttackPower = 0, Armor = armor, MagicResist = mr,
                AbilityPower = 0, AttackRange = 1, AttackInterval = 10f, MoveInterval = 10f,
                CritChance = 0, CritDamage = 0, MaxMana = 0, ManaPerHit = 0, ManaRegenPerSecond = 0,
            };

        /// <summary>友方队友面板（纯 buff 技能的目标们）。</summary>
        private static UnitStats Ally(int atk, int hp = 1000)
            => new UnitStats
            {
                MaxHp = hp, AttackPower = atk, Armor = 0, MagicResist = 0,
                AbilityPower = 0, AttackRange = 1, AttackInterval = 10f, MoveInterval = 10f,
                CritChance = 0, CritDamage = 0, MaxMana = 0, ManaPerHit = 0, ManaRegenPerSecond = 0,
            };

        private static Unit Place(EventBus bus, BoardData board, int id, bool playerSide, Pos pos, UnitStats stats, int skillId = 0)
        {
            var u = new Unit(id, 0, playerSide, stats, bus, 1, skillId);
            Assert.IsTrue(board.TryPlace(u, pos), $"摆位失败：{pos}");
            return u;
        }

        /// <summary>自建技能表（形状类规则的定向测试用）。</summary>
        private static SkillTable CustomTable(params SkillDef[] defs) => new SkillTable(defs);

        /// <summary>出手前钩子记录器：验证"每目标独立 draft"。</summary>
        private sealed class TargetRecorderHook : IBattleHook
        {
            public int SourceId => ModSourceId.TestBase;
            public readonly List<int> Targets = new List<int>();
            public void OnBattleStart(BattleContext ctx) { }
            public void OnBeforeAttack(BattleContext ctx, ref AttackDraft draft)
            {
                if (draft.Target != null) Targets.Add(draft.Target.Id);
            }
            public void OnAfterAttack(BattleContext ctx, in AttackResult result) { }
            public void OnDamaged(BattleContext ctx, in DamageResult result) { }
            public void OnDeath(BattleContext ctx, in DeathInfo death) { }
            public void OnTickEnd(BattleContext ctx) { }
        }

        // ─────────────────────── Default 表 v2 结构锁 ───────────────────────

        [Test]
        public void DefaultTable_V2Structure_DistributionAndBuffResolution()
        {
            var table = SkillTable.Default;
            Assert.AreEqual(18, table.All.Count, "18 棋子每人一技");

            int physical = 0, magic = 0, trueDmg = 0, pureBuff = 0;
            foreach (var s in table.All)
            {
                if (!s.HasDamageSegment) { pureBuff++; continue; }
                switch (s.DamageType)
                {
                    case DamageType.Physical: physical++; break;
                    case DamageType.Magic: magic++; break;
                    case DamageType.True: trueDmg++; break;
                }
            }
            Assert.AreEqual(4, physical, "伤害技能分布（拍板口径）：4 物理");
            Assert.AreEqual(2, magic, "2 魔法");
            Assert.AreEqual(1, trueDmg, "1 真实");
            Assert.AreEqual(11, pureBuff, "11 纯 buff（1~10 + 17）");

            // 关键行形状/规则/标记锁（改表必红——逼着同步改测试与文档）
            Assert.AreEqual(SkillShape.FullRow, table.Get(11).Shape, "督军：整行剑刃");
            Assert.AreEqual(SkillTargetRule.SelfShape, table.Get(11).TargetRule);
            Assert.AreEqual(7, table.Get(12).CastRange, "神射穿云箭：超远单体（7 = 全图可达）");
            Assert.AreEqual(SkillDelivery.Projectile, table.Get(12).Delivery);
            Assert.AreEqual(SkillShape.Box, table.Get(13).Shape, "战地法师：锚点 3x3");
            Assert.AreEqual(3, table.Get(13).ShapeWidth);
            Assert.AreEqual(3, table.Get(13).ShapeHeight);
            Assert.AreEqual(SkillTargetRule.LowestHpEnemy, table.Get(14).TargetRule, "影刃：生命最低单体");
            Assert.AreEqual(5, table.Get(14).BuffId, "影刃：淬毒");
            Assert.AreEqual(SkillBuffTarget.Targets, table.Get(14).BuffTarget);
            Assert.AreEqual(DamageType.Physical, table.Get(15).DamageType, "巨兽地裂践踏：拍板翻转成物理（v1 为魔法）");
            Assert.AreEqual(SkillShape.Box, table.Get(15).Shape);
            Assert.AreEqual(6, table.Get(16).ShapeWidth, "大法师：锚点 6x6");
            Assert.AreEqual(6, table.Get(16).ShapeHeight);
            Assert.AreEqual(SkillTargetRule.AllAllies, table.Get(17).TargetRule, "圣殿之王：全队防御 buff");
            Assert.AreEqual(13, table.Get(17).BuffId);
            Assert.AreEqual(SkillShape.FullCross, table.Get(18).Shape, "兽王：行+列十字");
            Assert.AreEqual(DamageType.True, table.Get(18).DamageType);

            // 表间交叉校验：技能附带 BuffId 全部能在 BuffTable 解析（表不互引，测试兜底——同 RoleTests 对职业表）
            foreach (var s in table.All)
                if (s.BuffId != 0)
                    Assert.AreNotEqual(0, BuffTable.Default.Get(s.BuffId).Id,
                        $"技能 {s.Id}「{s.DisplayName}」的 BuffId {s.BuffId} 不在 BuffTable.Default 里");
        }

        // ─────────────────────── 目标规则：单体 ───────────────────────

        [Test]
        public void LowestHpEnemy_PicksLowestWithinCastRange_TieBreakFirstScanned()
        {
            // (a) 距离内筛生命最低：B 更近但血厚，A 更远（仍 ≤ 施法距离 4）但血薄 → 打 A
            {
                var bus = new EventBus();
                var board = new BoardData(bus);
                var caster = Place(bus, board, 1, true, new Pos(3, 1), Caster(30), 14);
                Unit foeA = Place(bus, board, 2, false, new Pos(4, 4), Dummy(hp: 100000), 0);
                foeA.TakeDamage(99500, null);   // 500 血（更薄）
                Unit foeB = Place(bus, board, 3, false, new Pos(4, 3), Dummy(hp: 100000), 0);
                foeB.TakeDamage(92000, null);   // 800 血（更近：距离 2）
                caster.AddMana(30);

                var casts = new List<SkillCastEvent>();
                bus.Subscribe<SkillCastEvent>(e => casts.Add(e));
                var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default, buffs: BuffTable.Default);
                combat.Tick();

                Assert.AreEqual(1, casts.Count);
                Assert.AreEqual(foeA.Id, casts[0].TargetId, "施法距离内筛生命最低（不是最近的那个）");
            }
            // (b) 并列：先扫到的赢（行主序——B 在 y=3、A 在 y=4，B 先被扫到）
            {
                var bus = new EventBus();
                var board = new BoardData(bus);
                var caster = Place(bus, board, 1, true, new Pos(3, 1), Caster(30), 14);
                Unit foeA = Place(bus, board, 2, false, new Pos(4, 4), Dummy(hp: 100000), 0);
                foeA.TakeDamage(99500, null);   // 500 血
                Unit foeB = Place(bus, board, 3, false, new Pos(4, 3), Dummy(hp: 100000), 0);
                foeB.TakeDamage(99500, null);   // 500 血（并列）
                caster.AddMana(30);

                var casts = new List<SkillCastEvent>();
                bus.Subscribe<SkillCastEvent>(e => casts.Add(e));
                var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default, buffs: BuffTable.Default);
                combat.Tick();

                Assert.AreEqual(foeB.Id, casts[0].TargetId, "血量并列 → 行主序先扫到的（B 在 y=3）");
            }
        }

        [Test]
        public void LowestHpEnemy_NoTargetInRange_HoldsMana()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            // 唯一的敌人（也是血量最低）在施法距离 4 之外 → 无合法目标：不施法不扣蓝
            var caster = Place(bus, board, 1, true, new Pos(0, 1), Caster(30, range: 1, moveIv: 10f), 14);
            Place(bus, board, 2, false, new Pos(6, 4), Dummy(hp: 100000), 0);
            caster.AddMana(30);

            var casts = new List<SkillCastEvent>();
            bus.Subscribe<SkillCastEvent>(e => casts.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default, buffs: BuffTable.Default);
            for (int i = 0; i < 90; i++) combat.Tick();

            Assert.IsEmpty(casts, "生命最低的敌人超距 → 不施法");
            Assert.AreEqual(30, caster.CurrentMana, "门禁不过不扣蓝");
            Assert.AreEqual(0, combat.RollCount);
        }

        // ─────────────────────── 目标规则：锚点形状 ───────────────────────

        [Test]
        public void NearestEnemyShape_HitsAllEnemiesInBox_SingleCastEvent()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var caster = Place(bus, board, 1, true, new Pos(2, 1), Caster(30, range: 1, moveIv: 10f), 13);
            Unit a = Place(bus, board, 2, false, new Pos(4, 3), Dummy(), 0);   // 最近（距离 2）→ 锚点
            Unit b = Place(bus, board, 3, false, new Pos(4, 4), Dummy(), 0);   // 在锚点 3x3 内（x∈[3,5], y∈[2,4]）
            Unit c = Place(bus, board, 4, false, new Pos(6, 4), Dummy(), 0);   // 形状外
            caster.AddMana(30);

            var casts = new List<SkillCastEvent>();
            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<SkillCastEvent>(e => casts.Add(e));
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default, buffs: BuffTable.Default);
            combat.Tick();

            Assert.AreEqual(1, casts.Count, "SkillCastEvent 恰一条（多目标也只发一次）");
            Assert.AreEqual(a.Id, casts[0].TargetId, "主目标 = 名单首位 = 锚点（行主序先扫到）");
            Assert.AreEqual(2, damages.Count, "3x3 内两名敌人都受击");
            Assert.AreEqual(115, damages[0].FinalDamage, "60 + 55×100/100 = 115（法抗 0）");
            Assert.AreEqual(115, damages[1].FinalDamage);
            Assert.IsTrue(damages.All(d => d.UnitId == a.Id || d.UnitId == b.Id), "形状外的 C 不受击");
        }

        [Test]
        public void NearestEnemyShape_AnchorOutOfRange_HoldsMana()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var caster = Place(bus, board, 1, true, new Pos(2, 1), Caster(30, range: 1, moveIv: 10f), 13);
            Place(bus, board, 2, false, new Pos(7, 4), Dummy(), 0);   // 距离 5 > 施法距离 3
            caster.AddMana(30);

            var casts = new List<SkillCastEvent>();
            bus.Subscribe<SkillCastEvent>(e => casts.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default, buffs: BuffTable.Default);
            for (int i = 0; i < 90; i++) combat.Tick();

            Assert.IsEmpty(casts, "锚点超距 → 不施法");
            Assert.AreEqual(30, caster.CurrentMana, "不扣蓝");
        }

        [Test]
        public void NearestEnemyShape_BoxClampsAtBoardEdge()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            // 锚点贴底缘 (4,0)：3x3 的 y∈[-1,1] 只有 y∈[0,1] 真实存在——贴边截断
            var caster = Place(bus, board, 1, true, new Pos(1, 1), Caster(30, range: 1, moveIv: 10f), 13);
            Place(bus, board, 2, false, new Pos(4, 0), Dummy(), 0);   // 锚点（距离 3 = 施法距离边界）
            Place(bus, board, 3, false, new Pos(5, 2), Dummy(), 0);   // y=2 > 1：虽在锚点旁却出形状
            caster.AddMana(30);

            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default, buffs: BuffTable.Default);
            combat.Tick();

            Assert.AreEqual(1, damages.Count, "贴边锚点：形状区间越界部分自然截断（只有锚点自己中）");
        }

        // ─────────────────────── 目标规则：自身形状 ───────────────────────

        [Test]
        public void SelfShape_EmptyShape_HoldsManaFallsBackToNormalLogic()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            // 督军整行：敌人在别的行 → 形状内无人 = 不施法不扣蓝
            var caster = Place(bus, board, 1, true, new Pos(2, 1), Caster(30, range: 1, moveIv: 10f), 11);
            Place(bus, board, 2, false, new Pos(4, 3), Dummy(), 0);
            Place(bus, board, 3, false, new Pos(5, 4), Dummy(), 0);
            caster.AddMana(30);

            var casts = new List<SkillCastEvent>();
            bus.Subscribe<SkillCastEvent>(e => casts.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default, buffs: BuffTable.Default);
            for (int i = 0; i < 90; i++) combat.Tick();

            Assert.IsEmpty(casts, "自身形状内无敌人：形状即门禁 → 不施法");
            Assert.AreEqual(30, caster.CurrentMana, "不扣蓝（蓝满干等，与 v1 超距纪律同族）");
        }

        [Test]
        public void SelfShape_FullRow_HitsWholeRow_FriendlyNeverHit()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var caster = Place(bus, board, 1, true, new Pos(2, 1), Caster(30, range: 1, moveIv: 10f), 11);
            Unit ally = Place(bus, board, 5, true, new Pos(3, 1), Ally(atk: 0), 0);   // 同行的队友
            Unit foe = Place(bus, board, 2, false, new Pos(7, 3), Dummy(), 0);
            Assert.IsTrue(board.TryMove(foe, new Pos(6, 1), mustStayInOwnHalf: false),
                "战斗走位可越境——把敌人挪进施法者所在行（模拟近战交战后回身横斩的场景）");
            caster.AddMana(30);

            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default, buffs: BuffTable.Default);
            combat.Tick();

            Assert.AreEqual(1, damages.Count, "整行全宽：远至 4 格外的同排敌人也中");
            Assert.AreEqual(foe.Id, damages[0].UnitId);
            Assert.AreEqual(96, damages[0].FinalDamage, "70 + 130×20/100 = 96");
            Assert.AreEqual(ally.EffectiveStats.MaxHp, ally.CurrentHp, "同行的队友毫发无损（形状只收敌方）");
        }

        [Test]
        public void SelfShape_FullColumn_HitsOwnColumnOnly()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var skill = new SkillDef(99, "整列测试", DamageType.Physical, 10, 10, 10, 0, 100, 1,
                SkillTargetRule.SelfShape, false, SkillShape.FullColumn);
            var caster = Place(bus, board, 1, true, new Pos(3, 2), Caster(30, range: 1, moveIv: 10f), 99);
            Unit inCol = Place(bus, board, 2, false, new Pos(4, 3), Dummy(), 0);
            Assert.IsTrue(board.TryMove(inCol, new Pos(3, 3), mustStayInOwnHalf: false),
                "战斗走位越境——把敌人挪进施法者所在列（敌方半场放不进玩家列，先摆再挪）");
            Place(bus, board, 3, false, new Pos(5, 4), Dummy(), 0);   // 同排斜向、不同列
            caster.AddMana(30);

            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: CustomTable(skill), buffs: BuffTable.Default);
            combat.Tick();

            Assert.AreEqual(1, damages.Count, "整列只中同列的（10 + 100×20/100 = 30）");
            Assert.AreEqual(30, damages[0].FinalDamage);
        }

        [Test]
        public void SelfShape_FullCross_HitsRowAndColumn_NotDiagonal()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var caster = Place(bus, board, 1, true, new Pos(3, 2), Caster(30, range: 1, moveIv: 10f), 18);
            Unit inRow = Place(bus, board, 2, false, new Pos(6, 3), Dummy(), 0);
            Assert.IsTrue(board.TryMove(inRow, new Pos(5, 2), mustStayInOwnHalf: false), "挪进行内");
            Unit inCol = Place(bus, board, 3, false, new Pos(5, 4), Dummy(), 0);
            Assert.IsTrue(board.TryMove(inCol, new Pos(3, 4), mustStayInOwnHalf: false), "挪进列内");
            Place(bus, board, 4, false, new Pos(5, 5), Dummy(), 0);                 // 斜向：不在十字上
            caster.AddMana(30);

            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default, buffs: BuffTable.Default);
            combat.Tick();

            Assert.AreEqual(2, damages.Count, "十字 = 行 ∪ 列：行内一个 + 列内一个，对角不中");
            Assert.IsTrue(damages.All(d => d.FinalDamage == 118), "真实伤害：100 + 90×20/100 = 118");
        }

        [Test]
        public void SelfShape_Box_EvenSizeExtendsRightAndDown()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            // Box(2,2) 以自身为锚：x∈[X, X+1]、y∈[Y, Y+1]——偶数维向右/向下多占一格（确定性约定）
            var skill = new SkillDef(99, "偶数Box", DamageType.Physical, 10, 10, 10, 0, 100, 1,
                SkillTargetRule.SelfShape, false, SkillShape.Box, 2, 2);
            var caster = Place(bus, board, 1, true, new Pos(3, 2), Caster(30, range: 1, moveIv: 10f), 99);
            Unit rightDown = Place(bus, board, 2, false, new Pos(4, 3), Dummy(), 0);   // 右+下 ✓ 在形状内
            Place(bus, board, 3, false, new Pos(5, 2), Dummy(), 0);                    // 右+平 ✗ 出形状（x=5 > 4）
            Place(bus, board, 4, false, new Pos(4, 4), Dummy(), 0);                    // 下+二 ✗ 出形状（y=4 > 3）
            caster.AddMana(30);

            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: CustomTable(skill), buffs: BuffTable.Default);
            combat.Tick();

            Assert.AreEqual(1, damages.Count, "偶数维 Box 只覆盖右下象限一格");
            Assert.AreEqual(rightDown.Id, damages[0].UnitId);
        }

        [Test]
        public void SelfShape_HalfBoard_HitsCastersEnemyHalf()
        {
            var skill = new SkillDef(99, "半场测试", DamageType.Physical, 10, 10, 10, 0, 100, 1,
                SkillTargetRule.SelfShape, false, SkillShape.HalfBoard);

            // (a) 玩家施法 → 打敌方半场（列 X 4~7）：敌方单位本就只能摆在那里 → 全中
            {
                var bus = new EventBus();
                var board = new BoardData(bus);
                var caster = Place(bus, board, 1, true, new Pos(2, 1), Caster(30, range: 1, moveIv: 10f), 99);
                Place(bus, board, 2, false, new Pos(4, 3), Dummy(), 0);
                Place(bus, board, 3, false, new Pos(7, 5), Dummy(), 0);
                caster.AddMana(30);

                var damages = new List<UnitDamagedEvent>();
                bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));
                var combat = new CombatSystem(board, bus, 11u, skills: CustomTable(skill), buffs: BuffTable.Default);
                combat.Tick();
                Assert.AreEqual(2, damages.Count, "玩家视角：敌方半场全部（两个敌人都中）");
            }
            // (b) 敌方施法 → 打玩家半场（列 X 0~3）：规则对敌方镜像成立
            {
                var bus = new EventBus();
                var board = new BoardData(bus);
                Place(bus, board, 1, true, new Pos(2, 1), Dummy(), 0);
                Place(bus, board, 2, true, new Pos(1, 2), Dummy(), 0);
                var foeCaster = Place(bus, board, 3, false, new Pos(4, 4), Caster(30, range: 1, moveIv: 10f), 99);
                foeCaster.AddMana(30);

                var damages = new List<UnitDamagedEvent>();
                bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));
                var combat = new CombatSystem(board, bus, 11u, skills: CustomTable(skill), buffs: BuffTable.Default);
                combat.Tick();
                Assert.AreEqual(2, damages.Count, "敌方视角：玩家半场全部（规则镜像）");
            }
        }

        // ─────────────────────── 纯 buff 技能 ───────────────────────

        [Test]
        public void PureBuff_SelfRule_NoDamageNoRoll_ClearsMana_PanelsInAndOut()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            // 野猪武士技能（id 1 坚鬃）：Self + 硬皮（6 秒双抗 +25，180 tick）
            var caster = Place(bus, board, 1, true, new Pos(2, 1), Caster(30, range: 1, moveIv: 10f), 1);
            caster.AddModifier(new StatModifier(StatKind.Armor, StatOp.Flat, 40f, ModSourceId.TestBase));
            caster.AddModifier(new StatModifier(StatKind.MagicResist, StatOp.Flat, 20f, ModSourceId.TestBase));
            int baseArmor = caster.EffectiveStats.Armor;   // 40
            int baseMr = caster.EffectiveStats.MagicResist; // 20
            Place(bus, board, 2, false, new Pos(5, 4), Dummy(), 0);
            caster.AddMana(30);

            var casts = new List<SkillCastEvent>();
            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<SkillCastEvent>(e => casts.Add(e));
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default, buffs: BuffTable.Default);
            combat.Tick();

            Assert.AreEqual(1, casts.Count, "蓝满即放（Self 规则无距离门）");
            Assert.AreEqual(caster.Id, casts[0].TargetId, "主目标 = 施法者自己");
            Assert.IsFalse(casts[0].IsCrit, "纯 buff 不掷骰");
            Assert.AreEqual(0, combat.RollCount, "无伤害段 = 无暴击掷骰（D9：不出手不消耗随机）");
            Assert.AreEqual(0, damages.Count, "纯 buff 不产生任何伤害事件");
            Assert.AreEqual(0, caster.CurrentMana, "施法后清零");
            Assert.AreEqual(1, combat.Buffs.GetStacks(caster, 6), "硬皮挂上一层");
            Assert.AreEqual(baseArmor + 25, caster.EffectiveStats.Armor, "护甲 +25（buff 修饰器入账）");
            Assert.AreEqual(baseMr + 25, caster.EffectiveStats.MagicResist, "法抗 +25");

            // 到期回落：180 tick 后层掉、面板回基线
            for (int i = 0; i < 180; i++) combat.Tick();
            Assert.AreEqual(0, combat.Buffs.GetStacks(caster, 6), "180 tick 到期摘层");
            Assert.AreEqual(baseArmor, caster.EffectiveStats.Armor, "护甲回落");
            Assert.AreEqual(baseMr, caster.EffectiveStats.MagicResist, "法抗回落");
        }

        [Test]
        public void PureBuff_AllAllies_AppliesToEveryAlly_FirstScannedIsMainTarget()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            // 圣殿之王技能（id 17 王权裁决）：AllAllies + 王权庇护（全队双抗 +30）
            var caster = Place(bus, board, 1, true, new Pos(3, 1), Caster(30, range: 1, moveIv: 10f), 17);
            Unit allyA = Place(bus, board, 2, true, new Pos(0, 0), Ally(atk: 10), 0);
            Unit allyB = Place(bus, board, 3, true, new Pos(0, 2), Ally(atk: 10), 0);
            Place(bus, board, 4, false, new Pos(5, 4), Dummy(), 0);
            caster.AddMana(30);

            var casts = new List<SkillCastEvent>();
            bus.Subscribe<SkillCastEvent>(e => casts.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default, buffs: BuffTable.Default);
            combat.Tick();

            Assert.AreEqual(1, casts.Count);
            Assert.AreEqual(allyA.Id, casts[0].TargetId, "主目标 = 名单首位（行主序：(0,0) 最先被扫到）");
            Assert.AreEqual(1, combat.Buffs.GetStacks(caster, 13), "施法者自己也在全队名单里");
            Assert.AreEqual(1, combat.Buffs.GetStacks(allyA, 13));
            Assert.AreEqual(1, combat.Buffs.GetStacks(allyB, 13));
        }

        [Test]
        public void PureBuff_NoEnemyOnBoard_NeverCasts()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            // 全局前置：敌方场上没人（清场后的余势行动位）→ 任何规则都不施法——含无距离门的纯 buff
            var caster = Place(bus, board, 1, true, new Pos(2, 1), Caster(30, range: 1, moveIv: 10f), 1);
            Place(bus, board, 2, true, new Pos(3, 1), Ally(atk: 10), 0);   // 只有队友没有敌人
            caster.AddMana(30);

            var casts = new List<SkillCastEvent>();
            bus.Subscribe<SkillCastEvent>(e => casts.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default, buffs: BuffTable.Default);
            for (int i = 0; i < 5; i++) combat.Tick();

            Assert.IsEmpty(casts, "敌方清空 → 不施法（战斗事实上已结束）");
            Assert.AreEqual(30, caster.CurrentMana);
        }

        [Test]
        public void PureBuff_AllyHighestAttack_PicksHighestIncludingSelf_TieFirstScanned()
        {
            // (a) 攻最高的队友拿 buff（自己攻低就不给自己）
            {
                var bus = new EventBus();
                var board = new BoardData(bus);
                var caster = Place(bus, board, 1, true, new Pos(3, 1), Caster(30, range: 1, moveIv: 10f, attackPower: 20), 9);
                Unit weak = Place(bus, board, 2, true, new Pos(0, 0), Ally(atk: 50), 0);
                Unit strong = Place(bus, board, 3, true, new Pos(0, 2), Ally(atk: 80), 0);
                Place(bus, board, 4, false, new Pos(5, 4), Dummy(), 0);
                caster.AddMana(30);

                var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default, buffs: BuffTable.Default);
                combat.Tick();

                Assert.AreEqual(1, combat.Buffs.GetStacks(strong, 9), "攻最高（80）的队友拿到强化");
                Assert.AreEqual(0, combat.Buffs.GetStacks(weak, 9));
                Assert.AreEqual(0, combat.Buffs.GetStacks(caster, 9), "施法者自己攻 20 最低：不给自己");
                Assert.AreEqual(100, strong.EffectiveStats.AttackPower, "80 × 1.25 = 100（强化 +25%）");
            }
            // (b) 攻并列 → 行主序先扫到；(c) 自己攻最高 → 给自己
            {
                var bus = new EventBus();
                var board = new BoardData(bus);
                var caster = Place(bus, board, 1, true, new Pos(3, 1), Caster(30, range: 1, moveIv: 10f, attackPower: 90), 9);
                Unit allyA = Place(bus, board, 2, true, new Pos(0, 0), Ally(atk: 90), 0);   // 与施法者并列 90，先扫到
                Unit allyB = Place(bus, board, 3, true, new Pos(0, 2), Ally(atk: 70), 0);
                Place(bus, board, 4, false, new Pos(5, 4), Dummy(), 0);
                caster.AddMana(30);

                var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default, buffs: BuffTable.Default);
                combat.Tick();

                Assert.AreEqual(1, combat.Buffs.GetStacks(allyA, 9), "并列 90：先扫到的 allyA 拿（含自身语义下并列取先扫到）");
                Assert.AreEqual(0, combat.Buffs.GetStacks(caster, 9), "后扫到的自己不拿");
                Assert.AreEqual(0, combat.Buffs.GetStacks(allyB, 9));
            }
        }

        [Test]
        public void PureBuff_AllyLowestHpRatio_CrossMultiplicationExact()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var caster = Place(bus, board, 1, true, new Pos(3, 1), Caster(30, range: 1, moveIv: 10f), 10);
            Unit a = Place(bus, board, 2, true, new Pos(0, 0), Ally(atk: 10, hp: 40), 0);
            Unit b = Place(bus, board, 3, true, new Pos(0, 2), Ally(atk: 10, hp: 100), 0);
            a.TakeDamage(10, null);   // A：30/40 = 75%（绝对值最低）
            b.TakeDamage(40, null);   // B：60/100 = 60%（比例最低）
            Place(bus, board, 4, false, new Pos(5, 4), Dummy(), 0);
            caster.AddMana(30);

            var heals = new List<UnitHealedEvent>();
            bus.Subscribe<UnitHealedEvent>(e => heals.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default, buffs: BuffTable.Default);
            combat.Tick();
            for (int i = 0; i < 10; i++) combat.Tick();

            Assert.AreEqual(1, combat.Buffs.GetStacks(b, 12), "圣愈挂在生命【比例】最低的 B（60% < 75%）——不是绝对值最低的 A");
            Assert.AreEqual(0, combat.Buffs.GetStacks(a, 12));
            Assert.IsNotEmpty(heals, "Hot 开始跳（每 tick 3 点）");
            Assert.IsTrue(heals.All(h => h.UnitId == b.Id), "只有 B 被治疗");
        }

        // ─────────────────────── 伤害 + 附带 Buff（淬毒链） ───────────────────────

        [Test]
        public void DamagePlusBuff_PoisonOnSurvivor_NotOnDead()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            // 影刃技能（id 14 影袭）：生命最低单体 + 淬毒（DoT 2/跳 × 120 tick）
            // 蓝条 30、命中回蓝 30：一击普攻回满 → 交替"普攻→施法"
            var caster = Place(bus, board, 1, true, new Pos(2, 1), Caster(30, manaPerHit: 30, range: 5, moveIv: 10f), 14);
            Unit weak = Place(bus, board, 2, false, new Pos(4, 3), Dummy(hp: 50), 0);
            Unit tanky = Place(bus, board, 3, false, new Pos(5, 4), Dummy(hp: 100000), 0);
            caster.AddMana(30);

            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default, buffs: BuffTable.Default);
            combat.Tick();   // 第一次施法：打生命最低的 weak（50 血）→ 111 伤害致死

            Assert.IsFalse(weak.IsAlive, "50 血的最低血目标被 111 伤害击杀");
            Assert.AreEqual(0, combat.Buffs.GetStacks(weak, 5), "毒不上尸体：被击杀者不挂淬毒");

            // 普攻一次回满蓝（30/30）→ 下个行动位再施法：这次最低血 = tanky
            for (int i = 0; i < 40 && combat.Buffs.GetStacks(tanky, 5) == 0; i++) combat.Tick();
            Assert.AreEqual(1, combat.Buffs.GetStacks(tanky, 5), "存活的最低血目标挂上淬毒");
        }

        [Test]
        public void NoBuffTable_SkillWithBuff_DamageStillDealt_SilentSkip()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var caster = Place(bus, board, 1, true, new Pos(2, 1), Caster(30, range: 5, moveIv: 10f), 14);
            Place(bus, board, 2, false, new Pos(4, 3), Dummy(), 0);
            caster.AddMana(30);

            var casts = new List<SkillCastEvent>();
            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<SkillCastEvent>(e => casts.Add(e));
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default);   // 不传 buffs（直连测试的回归红线）

            Assert.IsNull(combat.Buffs, "无 Buff 表 = 无内建账本");
            Assert.DoesNotThrow(() => { for (int i = 0; i < 5; i++) combat.Tick(); }, "附带 Buff 的技能在无 Buff 管线下不炸");
            Assert.AreEqual(1, casts.Count, "施法照常");
            Assert.AreEqual(111, damages[0].FinalDamage, "伤害照常（85 + 130×20/100 = 111）");
        }

        // ─────────────────────── 多目标：顺序 / 暴击共享 / 钩子 ───────────────────────

        [Test]
        public void MultiTarget_RowMajorDamageOrder_SingleCastEvent_SharedCrit()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var caster = Place(bus, board, 1, true, new Pos(2, 1), Caster(30, critChance: 100, range: 1, moveIv: 10f), 11);
            Unit a = Place(bus, board, 2, false, new Pos(4, 3), Dummy(), 0);
            Unit b = Place(bus, board, 3, false, new Pos(5, 3), Dummy(), 0);
            Unit c = Place(bus, board, 4, false, new Pos(7, 3), Dummy(), 0);
            Assert.IsTrue(board.TryMove(a, new Pos(3, 1), mustStayInOwnHalf: false));
            Assert.IsTrue(board.TryMove(b, new Pos(5, 1), mustStayInOwnHalf: false));
            Assert.IsTrue(board.TryMove(c, new Pos(7, 1), mustStayInOwnHalf: false));
            caster.AddMana(30);

            var casts = new List<SkillCastEvent>();
            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<SkillCastEvent>(e => casts.Add(e));
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default, buffs: BuffTable.Default);
            combat.Tick();

            Assert.AreEqual(1, casts.Count, "整行三名敌人：SkillCastEvent 仍恰一条");
            Assert.AreEqual(a.Id, casts[0].TargetId, "主目标 = 行主序首位（x 最小）");
            Assert.AreEqual(3, damages.Count, "三名敌人都受击");
            CollectionAssert.AreEqual(
                new[] { a.Id, b.Id, c.Id }, damages.Select(d => d.UnitId).ToArray(),
                "受击顺序 = 行主序（x 升序）——确定性");
            Assert.IsTrue(damages.All(d => d.FinalDamage == 144), "共享暴击：96 × 1.5 = 144，三名目标同一结果");
            Assert.AreEqual(1, combat.RollCount, "整次施法只掷一次（多目标不膨胀随机序列）");
        }

        [Test]
        public void MultiTarget_BeforeAttackHook_PerTargetIndependentDraft()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var caster = Place(bus, board, 1, true, new Pos(2, 1), Caster(30, range: 1, moveIv: 10f), 11);
            Unit a = Place(bus, board, 2, false, new Pos(4, 3), Dummy(), 0);
            Unit b = Place(bus, board, 3, false, new Pos(5, 3), Dummy(), 0);
            Unit c = Place(bus, board, 4, false, new Pos(7, 3), Dummy(), 0);
            Assert.IsTrue(board.TryMove(a, new Pos(3, 1), mustStayInOwnHalf: false));
            Assert.IsTrue(board.TryMove(b, new Pos(5, 1), mustStayInOwnHalf: false));
            Assert.IsTrue(board.TryMove(c, new Pos(7, 1), mustStayInOwnHalf: false));
            caster.AddMana(30);

            var recorder = new TargetRecorderHook();
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default, buffs: BuffTable.Default);
            combat.AddHook(recorder);
            combat.Tick();

            Assert.AreEqual(3, recorder.Targets.Count, "出手前钩子对每个目标独立触发一次");
            CollectionAssert.AreEqual(new[] { a.Id, b.Id, c.Id }, recorder.Targets, "钩子顺序也走行主序");
        }

        [Test]
        public void MultiTarget_Kill_AllTargetsDie_HandleKillPerTarget()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            var caster = Place(bus, board, 1, true, new Pos(2, 1), Caster(30, range: 1, moveIv: 10f), 11);
            for (int i = 0; i < 3; i++)
            {
                Unit foe = Place(bus, board, 2 + i, false, new Pos(4 + i, 3), Dummy(hp: 50), 0);
                Assert.IsTrue(board.TryMove(foe, new Pos(3 + i * 2, 1), mustStayInOwnHalf: false));
            }
            caster.AddMana(30);

            var died = new List<UnitDiedEvent>();
            bus.Subscribe<UnitDiedEvent>(e => died.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default, buffs: BuffTable.Default);
            combat.Tick();

            Assert.AreEqual(3, died.Count, "一次施法清空整行：三个目标各走一次击杀收尾");
            Assert.IsTrue(combat.IsOver && combat.PlayerWon, "敌方清空 → 终局判胜");
        }

        // ─────────────────────── Buff 生产接线（CombatSystem 内建账本） ───────────────────────

        [Test]
        public void InternalBuffSystem_PoisonTicks_EveryTickStep65_ExactLedger()
        {
            var bus = new EventBus();
            var board = new BoardData(bus);
            // 影刃淬毒（DoT 2/跳 × 120 tick）：施法发生在 tick 1 的行动段，
            // 同 tick 的第 6.5 步（Buff 结算）就跳第一跳——第 120 跳发生在 tick 120，层在当步末摘除
            var caster = Place(bus, board, 1, true, new Pos(2, 1), Caster(30, range: 1, moveIv: 10f), 14);
            Unit tanky = Place(bus, board, 2, false, new Pos(4, 3), Dummy(hp: 100000), 0);
            caster.AddMana(30);

            var damages = new List<UnitDamagedEvent>();
            bus.Subscribe<UnitDamagedEvent>(e => damages.Add(e));
            var combat = new CombatSystem(board, bus, 11u, skills: SkillTable.Default, buffs: BuffTable.Default);
            for (int i = 0; i < 121; i++) combat.Tick();   // 驱 121 tick：覆盖 120 跳 + 到期后 1 tick

            int dotTicks = damages.Count(d => d.SourceUnitId == 0 && d.FinalDamage == 2);
            Assert.AreEqual(120, dotTicks, "淬毒恰好 120 跳 × 2 点（SourceUnitId=0 = 无攻击者的持续伤害）");
            Assert.AreEqual(0, combat.Buffs.GetStacks(tanky, 5), "120 tick 后层掉账清");
            int skillHits = damages.Count(d => d.FinalDamage == 111);
            Assert.AreEqual(1, skillHits, "施法本体一击 111（85 + 130×20/100）——之后再无施法（蓝不回）");
        }

        [Test]
        public void GameSession_ProductionWiring_BuffTableFlowsIntoCombat()
        {
            var bus = new EventBus();
            var casts = new List<SkillCastEvent>();
            bus.Subscribe<SkillCastEvent>(e => casts.Add(e));

            var s = new GameSession(bus, 0u, GameTables.Default);   // Default 捆十表：含 Buffs
            Assert.IsTrue(s.StartRun());
            Assert.IsTrue(s.Buy(0), "开局 3 金买 1 费");
            Assert.IsTrue(s.PlaceOnBoard(s.Bench[0].Id, new Pos(0, 0)));
            Assert.IsTrue(s.StartBattle());

            Assert.IsNotNull(s.Combat.Buffs, "生产链：GameTables.Buffs → CombatSystem 内建账本（技能附带 Buff 的管线在场）");

            int guard = 0;
            while (!s.Combat.IsOver && guard++ < 4500) s.Combat.Tick();
            Assert.IsTrue(s.Combat.IsOver, "战斗有始有终");
            Assert.DoesNotThrow(() => s.SettleBattle(), "结算链路含内建 Buff 账本零异常");
        }

        // ─────────────────────── 确定性 ───────────────────────

        [Test]
        public void Determinism_V2CastingAndBuffs_SameSeedSameEventStream()
        {
            string RunOnce(uint seed)
            {
                var bus = new EventBus();
                var log = new List<string>();
                bus.Subscribe<SkillCastEvent>(e => log.Add($"C|{e.CasterId}|{e.SkillId}|{e.TargetId}|{e.IsCrit}"));
                bus.Subscribe<UnitDamagedEvent>(e => log.Add($"D|{e.UnitId}|{e.SourceUnitId}|{e.FinalDamage}"));
                bus.Subscribe<UnitHealedEvent>(e => log.Add($"H|{e.UnitId}|{e.Amount}"));
                bus.Subscribe<ManaChangedEvent>(e => log.Add($"M|{e.UnitId}|{e.CurrentMana}"));

                var board = new BoardData(bus);
                // 督军（整行剑刃，暴击 50%——普攻与施法都掷骰）vs 整行三名敌人；命中回蓝 30 = 一击回满
                var caster = new Unit(1, 11, true, Caster(30, manaPerHit: 30, critChance: 50, range: 5, moveIv: 10f), bus, 1, 11);
                board.TryPlace(caster, new Pos(2, 1));
                for (int i = 0; i < 3; i++)
                {
                    var foe = new Unit(2 + i, 0, false, Dummy(), bus);
                    board.TryPlace(foe, new Pos(4 + i, 3));
                    board.TryMove(foe, new Pos(3 + i * 2, 1), mustStayInOwnHalf: false);
                }
                caster.AddMana(30);

                var combat = new CombatSystem(board, bus, seed, skills: SkillTable.Default, buffs: BuffTable.Default);
                for (int i = 0; i < 300 && !combat.IsOver; i++) combat.Tick();
                return string.Join(";", log);
            }

            Assert.AreEqual(RunOnce(77u), RunOnce(77u), "多目标施法 + 共享暴击掷骰：同种子双战斗事件流逐条一致");
        }
    }
}
