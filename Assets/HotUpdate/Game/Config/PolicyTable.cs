using System;
using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// 策略效果类型（决策 #18③ 正负同构，六类——2026-10-27 新增"战后扣血"、2026-10-29 P3-8 新增"拆卸器"）。
    /// 负面语义载体：金币负数=代价（钱包层，与棋子无关）；
    /// 修饰器负值=debuff（棋子层，只挂场上玩家单位——"只有上战场的棋子才算"）；
    /// 战后扣血=玩家血量层的例行代价（每场都扣，与棋子无关）。
    /// </summary>
    public enum PolicyEffectType
    {
        /// <summary>全队面板修饰器：正=buff、负=debuff，同一条策略内正负共存。
        /// 只挂场上玩家单位、下场摘干净上场补挂、场上人员变动/升星重算（2026-10-27 拍板，与羁绊同构）。</summary>
        TeamModifier,

        /// <summary>金币：正值入账、负值=代价（D2-5：扣到 0 为止不产生负债）。</summary>
        Gold,

        /// <summary>经验：恒正（编表纪律，构造期校验）。</summary>
        Xp,

        /// <summary>装备散件（#12⑧ 只给散件）。批次6 起 PickPolicy 直接入背包（AddEquipment，条目预检）。</summary>
        Equipment,

        /// <summary>战后额外扣血（2026-10-27 拍板 A 案：每场战斗结束都扣，胜也扣；Amount=扣血值）。
        /// 作用对象是玩家血量不是棋子——全场覆灭照样生效。</summary>
        PostBattleHpLoss,

        /// <summary>拆卸器（P3-8，2026-10-29 新增效果类型：策略是晶矿之外的第二个拆卸器来源；Amount=赠送数量）。
        /// 批次6 起 PickPolicy 直接入背包（AddUnsmithTools）。</summary>
        UnsmithTool,
    }

    /// <summary>
    /// 单条效果。正负同构（决策 #18③）：一条策略持多个条目，正负共存、玩家看总量权衡。
    /// HookFactory 与 Type 正交（附加字段）：任何条目都可附带钩子——"既发效果又带钩子"是合法复合。
    /// </summary>
    public readonly struct PolicyEffect
    {
        public readonly PolicyEffectType Type;

        /// <summary>金币（可负、≠0）/ 经验（恒 &gt;0）/ 战后扣血（恒 &gt;0）/ 拆卸器（恒 &gt;0）；修饰器与装备类恒 0。</summary>
        public readonly int Amount;

        /// <summary>TeamModifier 时非空（构造期校验）；模板 SourceId 恒 0（选中时盖实例章）。</summary>
        public readonly StatModifier[] Modifiers;

        /// <summary>Equipment 时 &gt;0（构造期校验）；"在装备表内"是编表纪律，
        /// 运行期由 PickPolicy 预检把关：表外 id = 纯拒绝、候选不消费）；其余恒 0。</summary>
        public readonly int EquipmentId;

        /// <summary>钩子工厂（预留，内容阶段3 填）：选中当场留存，每场 StartBattle 新造一只注册
        /// （与羁绊 RegisterBattleHooks 同款纪律，传开战瞬间冻结的场上玩家名单）。</summary>
        public readonly Func<IReadOnlyList<Unit>, IBattleHook> HookFactory;

        public PolicyEffect(PolicyEffectType type, int amount,
            StatModifier[] modifiers, int equipmentId,
            Func<IReadOnlyList<Unit>, IBattleHook> hookFactory)
        {
            Type = type;
            Amount = amount;
            Modifiers = modifiers;
            EquipmentId = equipmentId;
            HookFactory = hookFactory;
        }

        // ── 速记工厂（表内与测试共用；钩子条目用全参构造器）──

        public static PolicyEffect Team(params StatModifier[] modifiers)
            => new PolicyEffect(PolicyEffectType.TeamModifier, 0, modifiers, 0, null);

        public static PolicyEffect Gold(int amount)
            => new PolicyEffect(PolicyEffectType.Gold, amount, null, 0, null);

        public static PolicyEffect Xp(int amount)
            => new PolicyEffect(PolicyEffectType.Xp, amount, null, 0, null);

        public static PolicyEffect Gear(int equipmentId)
            => new PolicyEffect(PolicyEffectType.Equipment, 0, null, equipmentId, null);

        /// <summary>战后额外扣血（每场战斗结束都扣，2026-10-27 拍板 A 案）。</summary>
        public static PolicyEffect HpCost(int hpLoss)
            => new PolicyEffect(PolicyEffectType.PostBattleHpLoss, hpLoss, null, 0, null);

        /// <summary>拆卸器赠送（P3-8：策略是晶矿之外的第二个拆卸器来源；数量恒正，构造期校验）。</summary>
        public static PolicyEffect UnsmithTools(int count)
            => new PolicyEffect(PolicyEffectType.UnsmithTool, count, null, 0, null);
    }

    /// <summary>一条策略 = 正/负效果条目的组合（#18③）；正式词条由用户阶段3 构思填表（挂起 #9）。</summary>
    public readonly struct PolicyDef
    {
        public readonly int Id;
        public readonly string DisplayName;
        public readonly string Desc;
        public readonly PolicyEffect[] Effects;

        public PolicyDef(int id, string displayName, string desc, PolicyEffect[] effects)
        {
            Id = id;
            DisplayName = displayName ?? "";
            Desc = desc ?? "";
            Effects = effects;
        }
    }

    /// <summary>
    /// 策略表（实例 + Default 双源模式）。构造即校验（fail fast，同 OreTable/EnemyTable 纪律）：
    /// Id 唯一且 ≥1、条目数组非空、类型字段自洽（修饰器类带数组、金币 ≠0、经验 &gt;0、装备带 id、
    /// 战后扣血 &gt;0、拆卸器 &gt;0）。
    /// 空表合法（弹选退化 0 候选、不挡门禁——测试与"未配策略"场景用）。
    /// </summary>
    public sealed class PolicyTable
    {
        public static PolicyTable Default { get; } = BuildDefault();

        private readonly Dictionary<int, PolicyDef> _byId = new Dictionary<int, PolicyDef>();
        private readonly List<PolicyDef> _all = new List<PolicyDef>();

        public PolicyTable(IReadOnlyList<PolicyDef> defs)
        {
            if (defs == null) throw new ArgumentNullException(nameof(defs));
            foreach (var def in defs) Register(def);
        }

        /// <summary>全部策略（弹选池的数据源；顺序稳定——注册序）。</summary>
        public IReadOnlyList<PolicyDef> All => _all;

        /// <summary>按 id 查策略；查无返回 default（Id=0）。</summary>
        public PolicyDef Get(int id) => _byId.TryGetValue(id, out var def) ? def : default;

        private void Register(PolicyDef def)
        {
            if (def.Id < 1)
                throw new ArgumentException($"策略 id 必须 ≥ 1（当前 {def.Id}）");
            if (_byId.ContainsKey(def.Id))
                throw new ArgumentException($"策略 id 重复：{def.Id}");
            if (def.Effects == null || def.Effects.Length == 0)
                throw new ArgumentException($"策略 {def.Id}「{def.DisplayName}」效果条目不能为空");
            foreach (var e in def.Effects)
            {
                switch (e.Type)
                {
                    case PolicyEffectType.TeamModifier:
                        if (e.Modifiers == null || e.Modifiers.Length == 0)
                            throw new ArgumentException($"策略 {def.Id}「{def.DisplayName}」修饰器类条目必须带修饰器数组");
                        break;
                    case PolicyEffectType.Gold:
                        if (e.Amount == 0)
                            throw new ArgumentException($"策略 {def.Id}「{def.DisplayName}」金币条目 Amount 不能为 0");
                        break;
                    case PolicyEffectType.Xp:
                        if (e.Amount <= 0)
                            throw new ArgumentException($"策略 {def.Id}「{def.DisplayName}」经验条目必须恒正");
                        break;
                    case PolicyEffectType.Equipment:
                        if (e.EquipmentId <= 0)
                            throw new ArgumentException($"策略 {def.Id}「{def.DisplayName}」装备条目必须带装备 id");
                        break;
                    case PolicyEffectType.PostBattleHpLoss:
                        if (e.Amount <= 0)
                            throw new ArgumentException($"策略 {def.Id}「{def.DisplayName}」战后扣血条目必须恒正");
                        break;
                    case PolicyEffectType.UnsmithTool:
                        if (e.Amount <= 0)
                            throw new ArgumentException($"策略 {def.Id}「{def.DisplayName}」拆卸器条目必须恒正");
                        break;
                }
            }
            _byId[def.Id] = def;
            _all.Add(def);
        }

        /// <summary>
        /// 占位词条（D2-11；两方向=2026-10-27 拍板；第七条=2026-10-29 P3-8 拆卸器来源）：
        /// 正面向=小 buff/散件/拆卸器/少量金币或经验；负面向=战后每场额外扣血 4 点（A 案拍板值），
        /// 换取大量金币或经验。正式词条阶段3 用户填表。
        /// </summary>
        private static PolicyTable BuildDefault()
        {
            return new PolicyTable(new List<PolicyDef>
            {
                new PolicyDef(1, "磨刀石", "全队攻击 +5%",
                    new[] { PolicyEffect.Team(new StatModifier(StatKind.AttackPower, StatOp.Percent, 5)) }),
                new PolicyDef(2, "商队小费", "金币 +6",
                    new[] { PolicyEffect.Gold(6) }),
                new PolicyDef(3, "学徒笔记", "经验 +4",
                    new[] { PolicyEffect.Xp(4) }),
                new PolicyDef(4, "神秘散件箱", "获得一件装备散件",
                    new[] { PolicyEffect.Gear(1) }),
                new PolicyDef(5, "亡命赌约", "每场战斗结束额外扣血 4 点，换取金币 +20",
                    new[] { PolicyEffect.HpCost(4), PolicyEffect.Gold(20) }),
                new PolicyDef(6, "赏金猎人", "每场战斗结束额外扣血 4 点，换取经验 +12",
                    new[] { PolicyEffect.HpCost(4), PolicyEffect.Xp(12) }),
                new PolicyDef(7, "工坊补给", "获得 2 个拆卸器",
                    new[] { PolicyEffect.UnsmithTools(2) }),
            });
        }
    }
}
