using System;
using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// 技能查表：int id → SkillDef。
    /// 同一个"实例 + Default 双源模式"（同 CharacterTable / RoleTable / PolicyTable）：
    /// Default 是代码内置基线，正式内容由阶段4 从 Res/Config 读入后注入新实例。
    ///
    /// 构造即校验（fail fast）：id 唯一且 ≥1、三档基础值非负、两个系数非负、施法距离 ≥1 格。
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
            _byId[def.Id] = def;
            _all.Add(def);
        }

        /// <summary>
        /// 占位模板（D2-11）：v1 只给 3 费+ 的 8 个棋子各配一条技能
        /// （"3 费+才有技能"是数据录入纪律，不写 if(cost) 进战斗循环）。
        /// 数值口径：基础值按 ×1.5 复利（与血/攻的星级复利同款）；法强系数按职业分档
        /// （术 40 / 战 35 / 射 30 / 刺 30 / 坦 25——越依赖法强的越低，坦不该靠技能吃饭）。
        /// ⚠ 占位数值：本工程法强基准 100，系数 40 即 +40 点伤害，相对 3 费棋子 14~26 的攻击力偏高，
        /// 平衡期需整体下调系数或同步上调面板攻击力（待用户拍板）。
        /// </summary>
        private static SkillTable BuildDefault()
        {
            return new SkillTable(new List<SkillDef>
            {
                // 1 战团督军（战 / 物理 / 施法距离 1）
                new SkillDef(1, "冲阵号令", DamageType.Physical, 40, 60, 90, 35, 0, 1, SkillTargetRule.NearestEnemy, false),
                // 2 王家神射（射 / 物理 / 施法距离 4）
                new SkillDef(2, "穿云箭", DamageType.Physical, 45, 68, 101, 30, 0, 4, SkillTargetRule.NearestEnemy, false),
                // 3 战地法师（术 / 魔法 / 施法距离 3）
                new SkillDef(3, "奥术飞弹", DamageType.Magic, 30, 45, 68, 40, 0, 3, SkillTargetRule.NearestEnemy, false),
                // 4 影刃（刺 / 物理 / 施法距离 1）
                new SkillDef(4, "影袭", DamageType.Physical, 55, 83, 124, 30, 0, 1, SkillTargetRule.NearestEnemy, false),
                // 5 铁甲巨兽（坦 / 魔法 / 施法距离 2）
                new SkillDef(5, "地裂践踏", DamageType.Magic, 40, 60, 90, 25, 0, 2, SkillTargetRule.NearestEnemy, false),
                // 6 宫廷大法师（术 / 魔法 / 施法距离 3）
                new SkillDef(6, "奥术共鸣", DamageType.Magic, 35, 53, 79, 40, 0, 3, SkillTargetRule.NearestEnemy, false),
                // 7 圣殿之王（坦 / 物理 / 施法距离 1）
                new SkillDef(7, "王权裁决", DamageType.Physical, 60, 90, 135, 25, 0, 1, SkillTargetRule.NearestEnemy, false),
                // 8 魔渊兽王（战 / 真实 / 施法距离 2）
                new SkillDef(8, "深渊裂击", DamageType.True, 70, 105, 158, 35, 0, 2, SkillTargetRule.NearestEnemy, false),
            });
        }
    }
}
