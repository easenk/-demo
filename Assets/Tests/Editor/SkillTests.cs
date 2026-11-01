using System;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    /// <summary>
    /// 阶段3 批次1：技能表 + 技能伤害纯函数（决策 #15）。
    ///
    /// 锁死的契约：
    ///  · 技能表构造即校验：id 唯一且 ≥1、三档基础值非负、两系数非负、施法距离 ≥1 格；
    ///    7c-2 追加五案（形状规则必带形状/单体友方规则禁形状/Box 宽高 ≥1/纯 buff 必带 BuffId/BuffId 与 BuffTarget 成对）
    ///  · 技能伤害 = 基础值(按星级) + 法强系数×法强/100 + 攻击力系数×攻击力/100，末尾取整一次、保底 1
    ///  · 法强基准 100（系数 150 = 150% 法强）
    ///  · 只有物理伤害可暴击（决策 #10）——魔法/真实技能不吃暴击乘区
    ///  · 技能伤害与普攻共享同一条乘区管线（DamageFormula），技能不是第二套伤害体系
    /// </summary>
    [TestFixture]
    public class SkillTests
    {
        private static UnitStats Attacker(int abilityPower, int attackPower, int critDamage = 50)
            => new UnitStats
            {
                MaxHp = 100,
                AttackPower = attackPower,
                Armor = 0,
                MagicResist = 0,
                AbilityPower = abilityPower,
                AttackRange = 1,
                AttackInterval = 1f,
                MoveInterval = 1f,
                CritChance = 5,
                CritDamage = critDamage,
            };

        // ─────────────────────── 技能表本身 ───────────────────────

        [Test]
        public void SkillTable_Default_HasEighteenSkills()
        {
            var table = SkillTable.Default;
            Assert.AreEqual(18, table.All.Count, "7c-2：18 棋子每人一技（技能 id 与棋子 id 对齐 1~18）");
            for (int id = 1; id <= 18; id++)
                Assert.AreNotEqual(0, table.Get(id).Id, $"技能 id {id} 必须存在");
        }

        [Test]
        public void SkillTable_Get_UnknownId_ReturnsNoneSentinel()
        {
            Assert.AreEqual(0, SkillTable.Default.Get(9999).Id, "查无 → Id=0 哨兵");
        }

        [Test]
        public void SkillTable_Constructor_RejectsBadData()
        {
            Assert.Throws<ArgumentException>(() => new SkillTable(new[]
            {
                new SkillDef(0, "零号技能", DamageType.Physical, 1, 1, 1, 0, 0, 1, SkillTargetRule.NearestEnemy, false),
            }), "id 必须 ≥ 1");

            Assert.Throws<ArgumentException>(() => new SkillTable(new[]
            {
                new SkillDef(1, "甲", DamageType.Physical, 1, 1, 1, 0, 0, 1, SkillTargetRule.NearestEnemy, false),
                new SkillDef(1, "乙", DamageType.Physical, 1, 1, 1, 0, 0, 1, SkillTargetRule.NearestEnemy, false),
            }), "id 重复");

            Assert.Throws<ArgumentException>(() => new SkillTable(new[]
            {
                new SkillDef(1, "负基础值", DamageType.Physical, -1, 1, 1, 0, 0, 1, SkillTargetRule.NearestEnemy, false),
            }), "基础值不能为负");

            Assert.Throws<ArgumentException>(() => new SkillTable(new[]
            {
                new SkillDef(1, "负法强系数", DamageType.Physical, 1, 1, 1, -10, 0, 1, SkillTargetRule.NearestEnemy, false),
            }), "法强系数不能为负");

            Assert.Throws<ArgumentException>(() => new SkillTable(new[]
            {
                new SkillDef(1, "负攻系数", DamageType.Physical, 1, 1, 1, 0, -10, 1, SkillTargetRule.NearestEnemy, false),
            }), "攻击力系数不能为负");

            Assert.Throws<ArgumentException>(() => new SkillTable(new[]
            {
                new SkillDef(1, "零距离", DamageType.Physical, 1, 1, 1, 0, 0, 0, SkillTargetRule.NearestEnemy, false),
            }), "施法距离必须 ≥ 1 格");
        }

        [Test]
        public void SkillTable_Constructor_RejectsBadV2Data()
        {
            // 7c-2 校验五案（构造即校验，别让建表事故静默）

            // ① 形状规则必须带形状
            Assert.Throws<ArgumentException>(() => new SkillTable(new[]
            {
                new SkillDef(1, "锚点无形状", DamageType.Magic, 1, 1, 1, 0, 0, 3, SkillTargetRule.NearestEnemyShape, false),
            }), "锚点形状规则必须带形状");

            Assert.Throws<ArgumentException>(() => new SkillTable(new[]
            {
                new SkillDef(1, "自身无形状", DamageType.Physical, 1, 1, 1, 0, 0, 1, SkillTargetRule.SelfShape, false),
            }), "自身形状规则必须带形状");

            // ② 单体/友方规则禁止带形状
            Assert.Throws<ArgumentException>(() => new SkillTable(new[]
            {
                new SkillDef(1, "单体带形状", DamageType.Physical, 1, 1, 1, 0, 0, 1, SkillTargetRule.NearestEnemy, false, SkillShape.FullRow),
            }), "单体规则不能带形状");

            Assert.Throws<ArgumentException>(() => new SkillTable(new[]
            {
                new SkillDef(1, "友方带形状", DamageType.Physical, 0, 0, 0, 0, 0, 1, SkillTargetRule.AllAllies, false, SkillShape.Box, 3, 3, SkillDelivery.Instant, 13, SkillBuffTarget.Targets),
            }), "友方规则不能带形状");

            // ③ Box 宽高必须 ≥ 1
            Assert.Throws<ArgumentException>(() => new SkillTable(new[]
            {
                new SkillDef(1, "零宽Box", DamageType.Magic, 1, 1, 1, 0, 0, 3, SkillTargetRule.NearestEnemyShape, false, SkillShape.Box, 0, 3),
            }), "Box 宽高必须 ≥ 1");

            // ④ 纯 buff 技能必须带 BuffId
            Assert.Throws<ArgumentException>(() => new SkillTable(new[]
            {
                new SkillDef(1, "纯buff无挂载", DamageType.Physical, 0, 0, 0, 0, 0, 1, SkillTargetRule.Self, false),
            }), "纯 buff 技能必须带 BuffId");

            // ⑤ BuffId 与 BuffTarget=Targets 必须成对
            Assert.Throws<ArgumentException>(() => new SkillTable(new[]
            {
                new SkillDef(1, "有id无目标", DamageType.Physical, 0, 0, 0, 0, 0, 1, SkillTargetRule.Self, false, buffId: 6),
            }), "有 BuffId 必须同时 BuffTarget=Targets");

            Assert.Throws<ArgumentException>(() => new SkillTable(new[]
            {
                new SkillDef(1, "有目标无id", DamageType.Physical, 1, 1, 1, 0, 0, 1, SkillTargetRule.NearestEnemy, false, buffTarget: SkillBuffTarget.Targets),
            }), "BuffTarget=Targets 必须同时带 BuffId");
        }

        [Test]
        public void SkillDef_BaseDamageForStar_RejectsOutOfRangeStar()
        {
            var skill = SkillTable.Default.Get(13);   // 奥术飞弹：60/90/135
            Assert.AreEqual(skill.BaseDamage1, skill.BaseDamageForStar(1));
            Assert.AreEqual(skill.BaseDamage2, skill.BaseDamageForStar(2));
            Assert.AreEqual(skill.BaseDamage3, skill.BaseDamageForStar(3));
            Assert.Throws<ArgumentOutOfRangeException>(() => skill.BaseDamageForStar(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => skill.BaseDamageForStar(4));
        }

        // ─────────────────────── 伤害纯函数：精确账目 ───────────────────────

        [Test]
        public void SkillDamage_Formula_ExactLedger()
        {
            // 战地法师（术 / 魔法）：7c-2 表 基础 60/90/135，法强系数 55，攻击力系数 0
            var skill = SkillTable.Default.Get(13);
            var attacker = Attacker(abilityPower: 100, attackPower: 14);

            // 法强基准 100 → 系数 55 = +55 点：60 + 55 = 115
            Assert.AreEqual(115, SkillDamage.Compute(skill, 1, attacker), "1 星：60 + 55×100/100 = 115");
            Assert.AreEqual(145, SkillDamage.Compute(skill, 2, attacker), "2 星：90 + 55 = 145");
            Assert.AreEqual(190, SkillDamage.Compute(skill, 3, attacker), "3 星：135 + 55 = 190");
        }

        [Test]
        public void SkillDamage_AbilityPowerScalesLinearly()
        {
            var skill = SkillTable.Default.Get(13);
            Assert.AreEqual(60, SkillDamage.Compute(skill, 1, Attacker(0, 14)), "法强 0 → 只剩基础值 60");
            Assert.AreEqual(115, SkillDamage.Compute(skill, 1, Attacker(100, 14)), "法强 100 → +55");
            Assert.AreEqual(170, SkillDamage.Compute(skill, 1, Attacker(200, 14)), "法强 200 → +110");
        }

        [Test]
        public void SkillDamage_AttackPowerCoefficientCounts()
        {
            var skill = new SkillDef(99, "攻击力段", DamageType.Physical, 10, 10, 10, 0, 150, 1, SkillTargetRule.NearestEnemy, false);
            Assert.AreEqual(40, SkillDamage.Compute(skill, 1, Attacker(0, 20)), "10 + 150×20/100 = 40");
        }

        [Test]
        public void SkillDamage_ZeroBaseNeverDealsZero()
        {
            var skill = new SkillDef(98, "空技能", DamageType.Magic, 0, 0, 0, 0, 0, 1, SkillTargetRule.NearestEnemy, false);
            Assert.AreEqual(1, SkillDamage.Compute(skill, 1, Attacker(0, 0)), "保底 1（与 DamageFormula 同纪律）");
        }

        [Test]
        public void SkillDamage_IsPureFunction_SameInputSameOutput()
        {
            var skill = SkillTable.Default.Get(18);   // 深渊裂击：真实 / 3 星 225 / 攻击系数 90
            var attacker = Attacker(100, 45);
            int a = SkillDamage.Compute(skill, 3, attacker);
            int b = SkillDamage.Compute(skill, 3, attacker);
            Assert.AreEqual(a, b, "纯函数：无随机、无状态");
            Assert.AreEqual(266, a, "魔渊兽王 3 星：225 + 90×45/100 = 265.5 → 远离零取整 266");
        }

        // ─────────────────────── 与伤害公式管线的对接 ───────────────────────

        [Test]
        public void CanCrit_OnlyPhysicalSkills()
        {
            var table = SkillTable.Default;
            foreach (var skill in table.All)
            {
                bool expected = skill.DamageType == DamageType.Physical;
                Assert.AreEqual(expected, SkillDamage.CanCrit(skill),
                    $"{skill.DisplayName}（{skill.DamageType}）可暴击 = {expected}");
            }
        }

        [Test]
        public void SkillDamage_SharesSamePipelineAsBasicAttack()
        {
            var attacker = Attacker(100, 14);
            var defender = new UnitStats { Armor = 0, MagicResist = 0 };

            // 魔法技能：不吃暴击乘区（决策 #10）
            var magic = SkillTable.Default.Get(13);
            int baseMagic = SkillDamage.Compute(magic, 1, attacker);
            var magicInput = new DamageInput
            {
                Type = DamageType.Magic,
                BaseDamage = baseMagic,
                CanCrit = SkillDamage.CanCrit(magic),
                Attacker = attacker,
                Defender = defender,
                IsCrit = true,   // 强行置真：魔法技能也不该吃暴击
            };
            Assert.AreEqual(115, DamageFormula.Compute(magicInput), "魔法技能 IsCrit=true 也不加伤（0 抗性 → 系数 1）");

            // 物理技能：吃暴击乘区（基础 5% 暴击率出厂、暴伤 +50%）
            var physical = SkillTable.Default.Get(11);
            int basePhysical = SkillDamage.Compute(physical, 1, attacker);
            var physInput = new DamageInput
            {
                Type = DamageType.Physical,
                BaseDamage = basePhysical,
                CanCrit = SkillDamage.CanCrit(physical),
                Attacker = attacker,
                Defender = defender,
                IsCrit = true,
            };
            Assert.AreEqual(132, DamageFormula.Compute(physInput), "物理技能暴击：70 + 130×14/100 = 88.2 → 88，88 × 1.5 = 132");
        }
    }
}
