using System;
using System.Collections.Generic;
using AutoChess.Game;
using NUnit.Framework;

namespace AutoChess.Tests
{
    /// <summary>
    /// 阶段3 批次1：职业轴（决策 #22⑥"职业=独立 RoleDef/RoleTable，阶段3 落地"）。
    ///
    /// 锁死的契约：
    ///  · RoleId 是 enum：封闭集合（坦/战/刺/射/术由决策定死）→ 编译期类型安全（P3-5 划线原则）
    ///  · 职业表构造即校验：id 不能为 None、id 唯一、回蓝两维非负（fail fast，别让数据事故静默）
    ///  · 五职业 v1 数值：坦 5 / 战 10 / 刺 10 / 射 10 / 术 7+每秒 2
    ///  · 18 棋子每人一职，且职业全部在职业表里
    ///  · 面板回蓝两维 = 职业表取值（职业是回蓝速率轴的唯一真源）；无蓝条单位恒 0
    ///  · 蓝条纪律：3 费+ 有蓝条 + 有技能；1~2 费两者皆无
    /// </summary>
    [TestFixture]
    public class RoleTests
    {
        // ─────────────────────── 职业表本身 ───────────────────────

        [Test]
        public void RoleTable_Default_HasFiveRoles_WithV1Values()
        {
            var table = RoleTable.Default;

            Assert.AreEqual(5, table.All.Count, "五职业 v1：坦/战/刺/射/术");

            Assert.AreEqual(5,  table.Get(RoleId.Tank).ManaPerHit,         "坦：命中回蓝 5");
            Assert.AreEqual(0,  table.Get(RoleId.Tank).ManaRegenPerSecond, "坦：无被动回蓝");

            Assert.AreEqual(10, table.Get(RoleId.Warrior).ManaPerHit,         "战：命中回蓝 10");
            Assert.AreEqual(0,  table.Get(RoleId.Warrior).ManaRegenPerSecond, "战：无被动回蓝");

            Assert.AreEqual(10, table.Get(RoleId.Assassin).ManaPerHit,         "刺：命中回蓝 10");
            Assert.AreEqual(0,  table.Get(RoleId.Assassin).ManaRegenPerSecond, "刺：无被动回蓝");

            Assert.AreEqual(10, table.Get(RoleId.Marksman).ManaPerHit,         "射：命中回蓝 10");
            Assert.AreEqual(0,  table.Get(RoleId.Marksman).ManaRegenPerSecond, "射：无被动回蓝");

            Assert.AreEqual(7,  table.Get(RoleId.Mage).ManaPerHit,         "术：命中回蓝 7");
            Assert.AreEqual(2,  table.Get(RoleId.Mage).ManaRegenPerSecond, "术：唯一有每秒被动回蓝（2）");
        }

        [Test]
        public void RoleTable_Get_UnknownId_ReturnsNoneSentinel()
        {
            var table = RoleTable.Default;
            Assert.AreEqual(RoleId.None, table.Get((RoleId)999).Id, "未定义的枚举值 → 查无 → Id=None 哨兵");
            Assert.AreEqual(RoleId.None, table.Get(RoleId.None).Id, "None 不是一条职业");
        }

        [Test]
        public void RoleTable_Constructor_RejectsBadData()
        {
            Assert.Throws<ArgumentException>(() => new RoleTable(new[]
            {
                new RoleDef(RoleId.None, "哨兵当职业", 1, 0),
            }), "id 不能为 None");

            Assert.Throws<ArgumentException>(() => new RoleTable(new[]
            {
                new RoleDef(RoleId.Tank, "甲", 1, 0),
                new RoleDef(RoleId.Tank, "乙", 1, 0),
            }), "id 重复");

            Assert.Throws<ArgumentException>(() => new RoleTable(new[]
            {
                new RoleDef(RoleId.Tank, "负回蓝", -1, 0),
            }), "命中回蓝不能为负");

            Assert.Throws<ArgumentException>(() => new RoleTable(new[]
            {
                new RoleDef(RoleId.Tank, "负被动", 0, -2),
            }), "每秒回蓝不能为负");
        }

        // ─────────────────────── 18 棋子的职业挂载 ───────────────────────

        [Test]
        public void CharacterTable_AllCharacters_HaveValidRole()
        {
            var chars = CharacterTable.Default;
            Assert.AreEqual(18, chars.Count, "18 棋子矩阵（决策 #22②）");

            foreach (var def in chars.All)
            {
                Assert.AreNotEqual(RoleId.None, def.RoleId, $"{def.DisplayName} 必须挂职业");
                Assert.AreNotEqual(RoleId.None, RoleTable.Default.Get(def.RoleId).Id,
                    $"{def.DisplayName} 的职业 {def.RoleId} 不在职业表里");
            }
        }

        [Test]
        public void CharacterTable_AllFiveRoles_AreCovered()
        {
            var used = new HashSet<RoleId>();
            foreach (var def in CharacterTable.Default.All) used.Add(def.RoleId);

            Assert.IsTrue(used.Contains(RoleId.Tank),     "坦必须有棋子");
            Assert.IsTrue(used.Contains(RoleId.Warrior),  "战必须有棋子");
            Assert.IsTrue(used.Contains(RoleId.Assassin), "刺必须有棋子");
            Assert.IsTrue(used.Contains(RoleId.Marksman), "射必须有棋子");
            Assert.IsTrue(used.Contains(RoleId.Mage),     "术必须有棋子");
        }

        [Test]
        public void CharacterTable_ManaStats_ComeFromRoleTable()
        {
            foreach (var def in CharacterTable.Default.All)
            {
                RoleDef role = RoleTable.Default.Get(def.RoleId);
                if (def.BaseStats.MaxMana <= 0)
                {
                    Assert.AreEqual(0, def.BaseStats.ManaPerHit,
                        $"{def.DisplayName} 无蓝条 → 不建回蓝账本 → 命中回蓝恒 0");
                    Assert.AreEqual(0, def.BaseStats.ManaRegenPerSecond,
                        $"{def.DisplayName} 无蓝条 → 每秒回蓝恒 0");
                    continue;
                }

                Assert.AreEqual(role.ManaPerHit, def.BaseStats.ManaPerHit,
                    $"{def.DisplayName}（{role.DisplayName}）命中回蓝取职业表值");
                Assert.AreEqual(role.ManaRegenPerSecond, def.BaseStats.ManaRegenPerSecond,
                    $"{def.DisplayName}（{role.DisplayName}）每秒回蓝取职业表值");
            }
        }

        [Test]
        public void CharacterTable_SkillAndMana_OnlyForThreeCostPlus()
        {
            foreach (var def in CharacterTable.Default.All)
            {
                bool threePlus = def.Cost >= 3;
                Assert.AreEqual(threePlus, def.BaseStats.MaxMana > 0,
                    $"{def.DisplayName}（{def.Cost} 费）蓝条纪律：3 费+ 有蓝条、1~2 费恒无");
                Assert.AreEqual(threePlus, def.SkillId > 0,
                    $"{def.DisplayName}（{def.Cost} 费）技能纪律：3 费+ 有技能、1~2 费恒无");
            }
        }

        [Test]
        public void CharacterTable_SkillId_ResolvesInSkillTable()
        {
            foreach (var def in CharacterTable.Default.All)
            {
                if (def.SkillId == 0) continue;
                Assert.AreNotEqual(0, SkillTable.Default.Get(def.SkillId).Id,
                    $"{def.DisplayName} 的技能 id {def.SkillId} 不在技能表里");
            }
        }

        [Test]
        public void CharacterDef_LegacyConstructor_KeepsRoleAndSkillEmpty()
        {
            // 五参构造器（阶段2 原签名）保留：职业与技能恒无，既有 11 处调用点不受影响
            var def = new CharacterDef(1, "旧式棋子", 1, new UnitStats { MaxHp = 100, AttackInterval = 1f, MoveInterval = 1f }, null);
            Assert.AreEqual(RoleId.None, def.RoleId);
            Assert.AreEqual(0, def.SkillId);
        }
    }
}
