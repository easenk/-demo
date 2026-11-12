using System.Collections.Generic;
using System.Linq;
using AutoChess.Game;
using AutoChess.Presentation;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace AutoChess.Tests
{
    /// <summary>
    /// 批次4D 契约索引（弹道与技能视觉）：
    ///  · ProjectileEffectPlan.Build —— 出弹时刻/命中配对/锚点与施法者特效/buff 短爆展开/病理钳制；
    ///  · ProjectileMath —— 格间插值的边界与中点（「箭落地才见血」的时刻半边）；
    ///  · EffectVisualConfig —— 远程集与职业表互锁、技能表全覆盖、buff/伤害路由形态、
    ///    全部引用路径在磁盘上真实存在（素材树重组会被此测试当场揪出）。
    /// 时间轴用 internal 构造器直构合成节点（InternalsVisibleTo——批次B1 同款入口）。
    /// </summary>
    [TestFixture]
    public class ProjectileEffectTests
    {
        private const float Eps = 1e-6f;

        // ── 测试基建 ──

        private static PlannedTimeline Timeline(params PlannedNode[] nodes)
        {
            return new PlannedTimeline(new List<PlannedNode>(nodes));
        }

        private static PlannedNode Attack(int seq, float start, float duration, int unit, int target)
            => new PlannedNode { Kind = PlannedNodeKind.Attack, UnitId = unit, TargetId = target, Seq = seq, Start = start, Duration = duration };

        private static PlannedNode Cast(int seq, float start, float duration, int unit, int target, int skill)
            => new PlannedNode { Kind = PlannedNodeKind.Cast, UnitId = unit, TargetId = target, SkillId = skill, Seq = seq, Start = start, Duration = duration };

        private static PlannedNode Damage(int seq, float start, int unit, int source)
            => new PlannedNode { Kind = PlannedNodeKind.Damage, UnitId = unit, TargetId = source, Seq = seq, Start = start };

        private static PlannedNode Mana(int seq, float start, int unit)
            => new PlannedNode { Kind = PlannedNodeKind.Mana, UnitId = unit, Seq = seq, Start = start };

        private static PlannedNode Foreign(int seq, float start, int unit)
            => new PlannedNode { Kind = PlannedNodeKind.Heal, UnitId = unit, Seq = seq, Start = start };

        private static Dictionary<int, ResolvedUnitVisual> Visuals(params (int unit, float hitPct)[] entries)
        {
            var map = new Dictionary<int, ResolvedUnitVisual>();
            foreach (var (unit, hitPct) in entries)
                map[unit] = new ResolvedUnitVisual { AttackHitPercent = hitPct };
            return map;
        }

        private static Dictionary<int, int> Chars(params (int unit, int charId)[] entries)
        {
            var map = new Dictionary<int, int>();
            foreach (var (unit, charId) in entries) map[unit] = charId;
            return map;
        }

        // ── ProjectileEffectPlan.Build：普攻弹体 ──

        [Test]
        public void Build_RangedAttack_MakesProjectileAtHitMoment()
        {
            // 弓卫侍从（char9）出弹：出弹=Start+时长x命中帧%；命中=紧邻下一条 Damage 的交付
            var timeline = Timeline(
                Attack(1, 1f, 1.2f, 5, 7),
                Damage(2, 1.9f, 7, 5));
            var plan = ProjectileEffectPlan.Build(timeline, Visuals((5, 0.5f)), Chars((5, 9)), SkillTable.Default);

            Assert.AreEqual(1, plan.Count, "恰一条弹体演出");
            var p = plan.Items[0];
            Assert.AreEqual(PlannedVisualKind.Projectile, p.Kind);
            Assert.AreEqual(1.6f, p.FireAt, Eps, "出弹=1+1.2x0.5");
            Assert.AreEqual(1.9f, p.ArriveAt, Eps, "命中=配对交付");
            Assert.AreEqual(5, p.UnitId);
            Assert.AreEqual(7, p.TargetId);
            Assert.AreEqual(1f, p.Scale, Eps);
            StringAssert.EndsWith("紫色三叉戟弹体.prefab", p.Path, "箭形代用（2026-11-12 拍板映射）");
        }

        [Test]
        public void Build_MeleeAttack_NoProjectile()
        {
            var timeline = Timeline(Attack(1, 1f, 1.1f, 5, 7), Damage(2, 1.9f, 7, 5));
            var plan = ProjectileEffectPlan.Build(timeline, Visuals((5, 0.5f)), Chars((5, 1)), SkillTable.Default);
            Assert.AreEqual(0, plan.Count, "近战棋子（char1）无弹体——查表即路由");
        }

        [Test]
        public void Build_UnknownUnit_NoProjectile()
        {
            var timeline = Timeline(Attack(1, 1f, 1.2f, 5, 7), Damage(2, 1.9f, 7, 5));
            var plan = ProjectileEffectPlan.Build(timeline, Visuals((5, 0.5f)), new Dictionary<int, int>(), SkillTable.Default);
            Assert.AreEqual(0, plan.Count, "无棋子 id 的单位（怪种）不出弹");
        }

        [Test]
        public void Build_AttackWithoutPairedDamage_ClampsArrive()
        {
            // 紧邻下一条不是自己的配对伤害（防御探针场景）→ 保底最短飞行
            var timeline = Timeline(
                Attack(1, 1f, 1.2f, 5, 7),
                Foreign(2, 1.1f, 99),
                Damage(3, 1.9f, 7, 5));
            var plan = ProjectileEffectPlan.Build(timeline, Visuals((5, 0.5f)), Chars((5, 9)), SkillTable.Default);

            Assert.AreEqual(1, plan.Count);
            Assert.AreEqual(plan.Items[0].FireAt + ProjectileEffectPlan.MinFlight, plan.Items[0].ArriveAt, Eps);
        }

        // ── ProjectileEffectPlan.Build：伤害技能 ──

        [Test]
        public void Build_CastProjectile_PairsFirstVictimDelivery()
        {
            // 穿云箭（skill12）：Cast 后块内 Src=施法者 Damage 连串 = 受击名单，首笔=命中；清蓝=块尾
            var timeline = Timeline(
                Cast(1, 0.5f, 1f, 2, 7, 12),
                Damage(2, 2.1f, 7, 2),
                Damage(3, 2.1f, 8, 2),
                Mana(4, 0.55f, 2));
            var plan = ProjectileEffectPlan.Build(timeline, Visuals((2, 0.5f)), Chars((2, 12)), SkillTable.Default);

            Assert.AreEqual(1, plan.Count, "穿云箭无锚点特效（受击走通用物理命中反馈）");
            var p = plan.Items[0];
            Assert.AreEqual(PlannedVisualKind.Projectile, p.Kind);
            Assert.AreEqual(1f, p.FireAt, Eps, "出弹=0.5+1x0.5");
            Assert.AreEqual(2.1f, p.ArriveAt, Eps, "命中=首笔受击交付");
            Assert.AreEqual(1.6f, p.Scale, Eps, "穿云箭放大（拍板口径）");
            StringAssert.EndsWith("紫色三叉戟弹体.prefab", p.Path);
        }

        [Test]
        public void Build_CastBox13_ProjectileAndAnchorAtMainTarget()
        {
            // 奥术飞弹（skill13）：弹体 + 锚点爆炸（锚=主目标——NearestEnemyShape 的锚点=名单首位）
            var timeline = Timeline(
                Cast(1, 1f, 1f, 3, 9, 13),
                Damage(2, 2.5f, 9, 3),
                Damage(3, 2.5f, 10, 3),
                Mana(4, 1.05f, 3));
            var plan = ProjectileEffectPlan.Build(timeline, Visuals((3, 0.5f)), Chars((3, 13)), SkillTable.Default);

            Assert.AreEqual(2, plan.Count, "弹体+锚点特效");
            var proj = plan.Items.First(i => i.Kind == PlannedVisualKind.Projectile);
            Assert.AreEqual(2.5f, proj.ArriveAt, Eps);
            StringAssert.EndsWith("闪电黑洞弹体.prefab", proj.Path);
            var anchor = plan.Items.First(i => i.Kind == PlannedVisualKind.Effect);
            Assert.AreEqual(2.5f, anchor.FireAt, Eps, "锚点特效随命中时刻爆");
            Assert.AreEqual(9, anchor.UnitId, "锚=主目标格");
            StringAssert.EndsWith("圆形闪电范围落地.prefab", anchor.Path);
        }

        [Test]
        public void Build_CastInstant11_CasterEffectAtLaunch_NoProjectile()
        {
            var timeline = Timeline(
                Cast(1, 1f, 1f, 4, 7, 11),
                Damage(2, 2.2f, 7, 4),
                Mana(3, 1.05f, 4));
            var plan = ProjectileEffectPlan.Build(timeline, Visuals((4, 0.5f)), Chars((4, 11)), SkillTable.Default);

            Assert.AreEqual(1, plan.Count, "瞬发=只有施法者出手特效（受击震荡波走节点派发路由）");
            var e = plan.Items[0];
            Assert.AreEqual(PlannedVisualKind.Effect, e.Kind);
            Assert.AreEqual(1.5f, e.FireAt, Eps, "挥砍随出手时刻");
            Assert.AreEqual(4, e.UnitId, "挥砍播在施法者格");
            StringAssert.EndsWith("红黄色挥砍.prefab", e.Path);
        }

        [Test]
        public void Build_CastArea15_AnchorAtCasterOnArrive()
        {
            // 地裂践踏（skill15）：SelfShape → 锚=施法者；中心震荡波随命中时刻
            var timeline = Timeline(
                Cast(1, 1f, 1f, 6, 9, 15),
                Damage(2, 2.4f, 9, 6),
                Mana(3, 1.05f, 6));
            var plan = ProjectileEffectPlan.Build(timeline, Visuals((6, 0.5f)), Chars((6, 15)), SkillTable.Default);

            Assert.AreEqual(1, plan.Count);
            var e = plan.Items[0];
            Assert.AreEqual(6, e.UnitId, "SelfShape 锚=施法者");
            Assert.AreEqual(2.4f, e.FireAt, Eps, "随命中时刻");
            StringAssert.EndsWith("木元素震荡波.prefab", e.Path, "地裂代用（2026-11-12 拍板）");
        }

        [Test]
        public void Build_CastCross18_TwoBladesAndCenterWave()
        {
            var timeline = Timeline(
                Cast(1, 1f, 1f, 9, 7, 18),
                Damage(2, 2.2f, 7, 9),
                Mana(3, 1.05f, 9));
            var plan = ProjectileEffectPlan.Build(timeline, Visuals((9, 0.5f)), Chars((9, 18)), SkillTable.Default);

            Assert.AreEqual(3, plan.Count, "横刀+纵刀+中心震荡波");
            var blades = plan.Items.Where(i => i.Kind == PlannedVisualKind.Effect && i.Path.EndsWith("暗红色刀刃.prefab")).ToList();
            Assert.AreEqual(2, blades.Count, "十字=两道刀刃");
            Assert.AreEqual(0f, blades[0].Yaw, Eps);
            Assert.AreEqual(90f, blades[1].Yaw, Eps, "纵刀横刀差 90 度");
            Assert.AreEqual(9, blades[0].UnitId);
            StringAssert.EndsWith("暗红色震荡波.prefab",
                plan.Items.First(i => i.Path.EndsWith("暗红色震荡波.prefab")).Path);
        }

        [Test]
        public void Build_CastArriveBeforeLaunch_ClampsToMinFlight()
        {
            // 病理级联（配对交付早于出弹帧）：保底最短飞行——序不破，只迟到让步
            var timeline = Timeline(
                Cast(1, 1f, 1f, 2, 7, 12),
                Damage(2, 0.8f, 7, 2),
                Mana(3, 1.05f, 2));
            var plan = ProjectileEffectPlan.Build(timeline, Visuals((2, 0.5f)), Chars((2, 12)), SkillTable.Default);

            Assert.AreEqual(1, plan.Count);
            Assert.LessOrEqual(plan.Items[0].FireAt + ProjectileEffectPlan.MinFlight, plan.Items[0].ArriveAt + Eps);
        }

        // ── ProjectileEffectPlan.Build：纯 buff ──

        [Test]
        public void Build_PureBuffSelf_BurstAtAnimEnd()
        {
            // 坚鬃（skill1）：短爆排在施法窗走完、目标=施法者
            var timeline = Timeline(Cast(1, 2f, 1f, 1, 1, 1), Mana(2, 2.05f, 1));
            var plan = ProjectileEffectPlan.Build(timeline, Visuals((1, 0.5f)), Chars((1, 1)), SkillTable.Default);

            Assert.AreEqual(1, plan.Count);
            var e = plan.Items[0];
            Assert.AreEqual(PlannedVisualKind.Effect, e.Kind);
            Assert.AreEqual(3f, e.FireAt, Eps, "施法窗走完=Start+Duration");
            Assert.AreEqual(1, e.UnitId, "Self 规则目标=施法者");
            StringAssert.EndsWith("蓝色脚底光环.prefab", e.Path, "防御系=蓝光环");
        }

        [Test]
        public void Build_PureBuffAllAllies_AllAlliesEntry()
        {
            // 王权裁决（skill17）：全队条目——视图发火时按施法者阵营全员各播
            var timeline = Timeline(Cast(1, 1f, 1f, 2, 5, 17), Mana(2, 1.05f, 2));
            var plan = ProjectileEffectPlan.Build(timeline, Visuals((2, 0.5f)), Chars((2, 17)), SkillTable.Default);

            Assert.AreEqual(1, plan.Count);
            var e = plan.Items[0];
            Assert.AreEqual(PlannedVisualKind.AllAlliesEffect, e.Kind);
            Assert.AreEqual(2, e.UnitId, "阵营按施法者取");
            Assert.AreEqual(2f, e.FireAt, Eps);
            StringAssert.EndsWith("蓝色脚底光环.prefab", e.Path);
        }

        [Test]
        public void Build_PureBuffAllySingle_BurstAtMainTarget()
        {
            // 圣愈术（skill10）：友方单体——主目标=生命比例最低队友（流条目的 TargetId）
            var timeline = Timeline(Cast(1, 1f, 0.8f, 3, 6, 10), Mana(2, 1.05f, 3));
            var plan = ProjectileEffectPlan.Build(timeline, Visuals((3, 0.5f)), Chars((3, 10)), SkillTable.Default);

            Assert.AreEqual(1, plan.Count);
            var e = plan.Items[0];
            Assert.AreEqual(6, e.UnitId, "友方单体目标=主目标");
            Assert.AreEqual(1.8f, e.FireAt, Eps);
            StringAssert.EndsWith("单体治疗特效.prefab", e.Path);
        }

        [Test]
        public void Build_UnknownSkill_ProducesNothing()
        {
            var timeline = Timeline(Cast(1, 1f, 1f, 2, 7, 99), Mana(2, 1.05f, 2));
            var plan = ProjectileEffectPlan.Build(timeline, Visuals((2, 0.5f)), Chars((2, 12)), SkillTable.Default);
            Assert.AreEqual(0, plan.Count, "表外技能 id 防御性跳过");
        }

        // ── ProjectileMath ──

        [Test]
        public void ProjectileMath_PositionAt_ClampsAtBothEnds()
        {
            var from = new Vector3(0f, 0.7f, 0f);
            var to = new Vector3(10f, 0.7f, 0f);

            Assert.AreEqual(from, ProjectileMath.PositionAt(from, to, 1f, 3f, 0f), "出弹前钉在起点");
            Assert.AreEqual(new Vector3(5f, 0.7f, 0f), ProjectileMath.PositionAt(from, to, 1f, 3f, 2f), "中点线性插值");
            Assert.AreEqual(to, ProjectileMath.PositionAt(from, to, 1f, 3f, 3f), "命中时刻到终点");
            Assert.AreEqual(to, ProjectileMath.PositionAt(from, to, 1f, 3f, 99f), "命中后钉在终点");
        }

        [Test]
        public void ProjectileMath_ZeroSpan_LandsInstantly()
        {
            var from = new Vector3(0f, 0f, 0f);
            Assert.AreEqual(new Vector3(1f, 0f, 0f), ProjectileMath.PositionAt(from, new Vector3(1f, 0f, 0f), 2f, 2f, 2f), "零时长退化=直接落点");
        }

        // ── EffectVisualConfig：映射表三重锁 ──

        [Test]
        public void Config_RangedCharSet_MatchesCharacterTable()
        {
            for (int charId = 1; charId <= 18; charId++)
            {
                bool ranged = CharacterTable.Default.Get(charId).BaseStats.AttackRange > 1;
                bool hasProjectile = EffectVisualConfig.ProjectilePathForChar(charId) != null;
                Assert.AreEqual(ranged, hasProjectile,
                    $"棋子 {charId}：射程 {(ranged ? ">1" : "=1")} 与弹体映射 {(hasProjectile ? "有" : "无")} 必须一致");
            }
        }

        [Test]
        public void Config_MagicCharSet_MatchesMageRoles()
        {
            for (int charId = 1; charId <= 18; charId++)
            {
                bool mage = CharacterTable.Default.Get(charId).RoleId == RoleId.Mage;
                Assert.AreEqual(mage, EffectVisualConfig.IsMagicChar(charId),
                    $"棋子 {charId}：魔法命中反馈的集合必须恰为术士职业集");
            }
        }

        [Test]
        public void Config_AllSkillsHaveVisualEntries()
        {
            Assert.AreEqual(18, SkillTable.Default.All.Count, "技能表 18 条（id 与棋子对齐）");
            foreach (SkillDef skill in SkillTable.Default.All)
                Assert.IsTrue(EffectVisualConfig.Skills.ContainsKey(skill.Id),
                    $"技能 {skill.Id}「{skill.DisplayName}」缺视觉配置——映射表必须全覆盖");
        }

        [Test]
        public void Config_BuffSkillsHaveBurst_DamageSkillsDont()
        {
            foreach (SkillDef skill in SkillTable.Default.All)
            {
                var v = EffectVisualConfig.Skills[skill.Id];
                if (skill.HasDamageSegment)
                    Assert.IsNull(v.BuffBurstPath, $"伤害技能 {skill.Id} 不应有 buff 短爆配置");
                else
                {
                    Assert.IsFalse(string.IsNullOrEmpty(v.BuffBurstPath), $"纯 buff 技能 {skill.Id} 必须有短爆素材");
                    Assert.IsNull(v.ProjectilePath, $"纯 buff 技能 {skill.Id} 不应有弹体");
                    Assert.IsNull(v.CasterEffectPath, $"纯 buff 技能 {skill.Id} 不应有施法者特效");
                }
            }
        }

        [Test]
        public void Config_VictimRouting_LocksKeySkills()
        {
            Assert.IsFalse(string.IsNullOrEmpty(EffectVisualConfig.Skills[11].VictimEffectPath), "冲阵号令：行内受击敌格震荡波");
            StringAssert.EndsWith("血红色震荡波.prefab", EffectVisualConfig.Skills[11].VictimEffectPath);

            StringAssert.EndsWith("暗元素震荡波.prefab", EffectVisualConfig.Skills[14].VictimEffectPath, "影袭受击者");
            Assert.IsFalse(string.IsNullOrEmpty(EffectVisualConfig.Skills[14].VictimExtraPath), "影袭淬毒标识");
            StringAssert.EndsWith("毒雾特效.prefab", EffectVisualConfig.Skills[14].VictimExtraPath);

            Assert.IsNull(EffectVisualConfig.Skills[15].VictimEffectPath, "地裂践踏受击者走通用命中反馈（拍板口径）");
            Assert.IsNull(EffectVisualConfig.Skills[18].VictimEffectPath, "深渊裂击受击者走通用命中反馈");
            Assert.IsNull(EffectVisualConfig.Skills[12].VictimEffectPath, "穿云箭受击者走通用物理命中（枪械子弹受击）");
        }

        [Test]
        public void Config_AllReferencedPaths_ExistOnDisk()
        {
            var paths = EffectVisualConfig.AllPaths().ToList();
            Assert.GreaterOrEqual(paths.Count, 15, "路径枚举应覆盖弹体/命中/技能全量（去重后 15+）");
            Assert.AreEqual(paths.Distinct().Count(), paths.Count, "路径不应重复");
            foreach (string path in paths)
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(path),
                    $"引用的特效预制体在磁盘上不存在（素材树重组后须同步改映射表）：{path}");
        }
    }
}
