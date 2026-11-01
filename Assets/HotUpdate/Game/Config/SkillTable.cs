using System;
using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// 技能查表：int id → SkillDef。
    /// 同一个"实例 + Default 双源模式"（同 CharacterTable / RoleTable / PolicyTable）：
    /// Default 是代码内置基线，正式内容由阶段4 从 Res/Config 读入后注入新实例。
    ///
    /// 构造即校验（fail fast）：id 唯一且 ≥1、三档基础值非负、两个系数非负、施法距离 ≥1 格；
    /// 7c-2 追加五案：形状规则必须带形状 / 单体与友方规则禁止带形状 / Box 宽高 ≥1 /
    /// 纯 buff 技能必须带 BuffId / BuffId 与 BuffTarget=Targets 成对出现。
    /// 基础值不强制随星级递增——那是编表偏好不是结构约束，校验器不越权。
    /// </summary>
    public sealed class SkillTable
    {
        /// <summary>代码内置基线：v1 占位模板（D2-11 纪律——方向由用户拍板，数值阶段3 平衡期重定）。</summary>
        public static SkillTable Default { get; } = BuildDefault();

        private readonly Dictionary<int, SkillDef> _byId = new Dictionary<int, SkillDef>();
        private readonly List<SkillDef> _all = new List<SkillDef>();

        public SkillTable(IReadOnlyList<SkillDef> defs)
        {
            if (defs == null) throw new ArgumentNullException(nameof(defs));
            foreach (var def in defs) Register(def);
        }

        /// <summary>全部技能定义（保持注册顺序）。</summary>
        public IReadOnlyList<SkillDef> All => _all;

        /// <summary>按 id 查技能；查无返回 default（Id=0）。调用方先查 Id 再用。</summary>
        public SkillDef Get(int id) => _byId.TryGetValue(id, out var def) ? def : default;

        public bool TryGet(int id, out SkillDef def)
        {
            if (_byId.TryGetValue(id, out def)) return true;
            def = default;
            return false;
        }

        private void Register(SkillDef def)
        {
            if (def.Id < 1)
                throw new ArgumentException($"技能 id 必须 ≥ 1（当前 {def.Id}）");
            if (_byId.ContainsKey(def.Id))
                throw new ArgumentException($"技能 id 重复：{def.Id}");
            if (def.BaseDamage1 < 0 || def.BaseDamage2 < 0 || def.BaseDamage3 < 0)
                throw new ArgumentException($"技能 {def.Id}「{def.DisplayName}」三档基础值不能为负");
            if (def.AbilityPowerCoefficient < 0)
                throw new ArgumentException($"技能 {def.Id}「{def.DisplayName}」法强系数不能为负");
            if (def.AttackPowerCoefficient < 0)
                throw new ArgumentException($"技能 {def.Id}「{def.DisplayName}」攻击力系数不能为负");
            if (def.CastRange < 1)
                throw new ArgumentException($"技能 {def.Id}「{def.DisplayName}」施法距离必须 ≥ 1 格（当前 {def.CastRange}）");
            bool isShapeRule = def.TargetRule == SkillTargetRule.NearestEnemyShape
                || def.TargetRule == SkillTargetRule.SelfShape;
            if (isShapeRule && def.Shape == SkillShape.Single)
                throw new ArgumentException($"技能 {def.Id}「{def.DisplayName}」形状规则必须带形状（Box/整行/整列/十字/半场）");
            if (!isShapeRule && def.Shape != SkillShape.Single)
                throw new ArgumentException($"技能 {def.Id}「{def.DisplayName}」单体/友方规则不能带形状（形状只属于群体规则）");
            if (def.Shape == SkillShape.Box && (def.ShapeWidth < 1 || def.ShapeHeight < 1))
                throw new ArgumentException($"技能 {def.Id}「{def.DisplayName}」Box 形状宽高必须 ≥ 1（当前 {def.ShapeWidth}x{def.ShapeHeight}）");
            if (!def.HasDamageSegment && def.BuffId == 0)
                throw new ArgumentException($"技能 {def.Id}「{def.DisplayName}」纯 buff 技能必须带 BuffId（无伤害段也无 Buff = 建表事故）");
            if ((def.BuffId == 0) != (def.BuffTarget == SkillBuffTarget.None))
                throw new ArgumentException($"技能 {def.Id}「{def.DisplayName}」BuffId 与 BuffTarget=Targets 必须成对出现");
            _byId[def.Id] = def;
            _all.Add(def);
        }

        /// <summary>
        /// 技能 v2（7c-2，平衡期）：18 棋子每人一技，技能 id 与棋子 id 对齐（1~18，自解释且测试引用稳定）。
        /// 分配与数值 = 第一版锚点（2026-11-01 用户拍板方向 + #21⑩ 压测重定流程）：
        ///  · 1~10（1~2 费）+ 17：纯 buff 技能（无伤害段）——拍板原文"1~2 费没有攻击技能、
        ///    单纯给自己或我方上 buff"：坦自身双抗 / 射自身或攻最高队友加攻 / 刺自身攻速 /
        ///    术自身回蓝与最缺血队友治疗；17 圣殿之王全队防御 buff（防御向终极）；
        ///  · 11~16、18：3 费+ 伤害技能按拍板落位——11 督军整行剑刃物理 / 12 神射超远单体弹道 /
        ///    13 战地法师锚点 3x3 魔法弹道 / 14 影刃生命最低单体+淬毒 / 15 巨兽自身中心 3x3 物理
        ///    （拍板翻转：v1 为魔法）/ 16 大法师锚点 6x6 魔法弹道 / 18 兽王自身行+列十字真实伤害；
        ///  · 伤害类型分布（拍板口径）：4 物（11/12/14/15）/ 2 法（13/16）/ 1 真（18）/ 11 纯 buff。
        /// 数值口径：基础值按 ×1.5 复利（与血/攻的星级复利同款）；3 费单体约 2~3 倍普攻裸伤、
        /// 群体技能单价比单体低、总伤看目标数；1~2 费 buff 压在"身份技能非胜负手"量级。
        /// ⚠ 名字全部占位可改；数值全部 = 第一版锚点随压测重定。
        /// </summary>
        private static SkillTable BuildDefault()
        {
            return new SkillTable(new List<SkillDef>
            {
                // ── 1~2 费纯 buff（无伤害段；DamageType 不消费填 Physical；蓝满即放、无距离门）──
                new SkillDef( 1, "坚鬃",     DamageType.Physical, 0, 0, 0, 0, 0, 1, SkillTargetRule.Self, false, buffId: 6,  buffTarget: SkillBuffTarget.Targets),   // 野猪武士：硬皮（双抗）
                new SkillDef( 2, "磨矛",     DamageType.Physical, 0, 0, 0, 0, 0, 1, SkillTargetRule.Self, false, buffId: 8,  buffTarget: SkillBuffTarget.Targets),   // 兽人投矛手：战吼（攻+20%）
                new SkillDef( 3, "列阵",     DamageType.Physical, 0, 0, 0, 0, 0, 1, SkillTargetRule.Self, false, buffId: 6,  buffTarget: SkillBuffTarget.Targets),   // 民兵：硬皮（双抗）
                new SkillDef( 4, "法力汲取", DamageType.Physical, 0, 0, 0, 0, 0, 1, SkillTargetRule.Self, false, buffId: 11, buffTarget: SkillBuffTarget.Targets),   // 巫术学徒：汲魔（每秒回蓝+5）
                new SkillDef( 5, "魔弹蓄能", DamageType.Physical, 0, 0, 0, 0, 0, 1, SkillTargetRule.Self, false, buffId: 8,  buffTarget: SkillBuffTarget.Targets),   // 魔弹射手：战吼（攻+20%）
                new SkillDef( 6, "狼影疾袭", DamageType.Physical, 0, 0, 0, 0, 0, 1, SkillTargetRule.Self, false, buffId: 10, buffTarget: SkillBuffTarget.Targets),   // 暗狼刺客：疾影（攻速）
                new SkillDef( 7, "符文壁垒", DamageType.Physical, 0, 0, 0, 0, 0, 1, SkillTargetRule.Self, false, buffId: 7,  buffTarget: SkillBuffTarget.Targets),   // 符文守卫：符文壁垒（双抗强档）
                new SkillDef( 8, "锁定弱点", DamageType.Physical, 0, 0, 0, 0, 0, 1, SkillTargetRule.Self, false, buffId: 9,  buffTarget: SkillBuffTarget.Targets),   // 夜行狙击手：强化（攻+25%）
                new SkillDef( 9, "侍从号令", DamageType.Physical, 0, 0, 0, 0, 0, 1, SkillTargetRule.AllyHighestAttack, false, buffId: 9, buffTarget: SkillBuffTarget.Targets),   // 弓卫侍从：给攻最高队友（含自身）
                new SkillDef(10, "圣愈术",   DamageType.Physical, 0, 0, 0, 0, 0, 1, SkillTargetRule.AllyLowestHpRatio, false, buffId: 12, buffTarget: SkillBuffTarget.Targets),  // 随军牧师：奶生命比例最低队友
                new SkillDef(17, "王权裁决", DamageType.Physical, 0, 0, 0, 0, 0, 1, SkillTargetRule.AllAllies, false, buffId: 13, buffTarget: SkillBuffTarget.Targets),   // 圣殿之王：全队防御 buff（防御向终极）

                // ── 3 费+ 伤害技能（形状/弹道标记/附带 Buff 按拍板；施法距离见 CastRange）──
                new SkillDef(11, "冲阵号令", DamageType.Physical, 70, 105, 158, 0, 130, 1, SkillTargetRule.SelfShape, false, SkillShape.FullRow, 0, 0, SkillDelivery.Instant),       // 战团督军：自身整行剑刃（物理可暴击；形状即门禁）
                new SkillDef(12, "穿云箭",   DamageType.Physical, 90, 135, 203, 0, 130, 7, SkillTargetRule.NearestEnemy, false, SkillShape.Single, 0, 0, SkillDelivery.Projectile),  // 王家神射：超远单体弹道（7 = 全图可达）
                new SkillDef(13, "奥术飞弹", DamageType.Magic,    60,  90, 135, 55, 0, 3, SkillTargetRule.NearestEnemyShape, false, SkillShape.Box, 3, 3, SkillDelivery.Projectile),// 战地法师：锚点中心 3x3
                new SkillDef(14, "影袭",     DamageType.Physical, 85, 128, 192, 0, 130, 4, SkillTargetRule.LowestHpEnemy, false, SkillShape.Single, 0, 0, SkillDelivery.Instant, 5, SkillBuffTarget.Targets), // 影刃：生命最低单体 + 淬毒
                new SkillDef(15, "地裂践踏", DamageType.Physical, 65,  98, 147, 0, 100, 1, SkillTargetRule.SelfShape, false, SkillShape.Box, 3, 3, SkillDelivery.Area),                  // 铁甲巨兽：自身中心 3x3（拍板改物理）
                new SkillDef(16, "奥术共鸣", DamageType.Magic,    50,  75, 113, 70, 0, 3, SkillTargetRule.NearestEnemyShape, false, SkillShape.Box, 6, 6, SkillDelivery.Projectile),  // 宫廷大法师：锚点中心 6x6
                new SkillDef(18, "深渊裂击", DamageType.True,    100, 150, 225, 0,  90, 1, SkillTargetRule.SelfShape, false, SkillShape.FullCross, 0, 0, SkillDelivery.Area),       // 魔渊兽王：自身行+列十字真实伤害
            });
        }
    }
}
