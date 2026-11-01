using System;
using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// 策略效果类型（决策 #18③ 正负同构；7c-1 扩到十二类）。
    /// 负面语义载体：金币负数=代价（钱包层，与棋子无关）；
    /// 修饰器负值=debuff（棋子层，只挂场上玩家单位——"只有上战场的棋子才算"）；
    /// 战后扣血=玩家血量层的例行代价（每场都扣，与棋子无关）。
    /// 7c-1 新增六类"规则修正"效果（改系统参数而非发资源）：利息封顶/买经验折扣/定向刷新/升级折扣/败血豁免/随机装备。
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

        /// <summary>装备散件（#12⑧ 只给散件，固定 id）。</summary>
        Equipment,

        /// <summary>战后额外扣血（2026-10-27 拍板 A 案：每场战斗结束都扣，胜也扣；Amount=扣血值）。
        /// 作用对象是玩家血量不是棋子——全场覆灭照样生效。</summary>
        PostBattleHpLoss,

        /// <summary>拆卸器（P3-8：策略是晶矿之外的第二个拆卸器来源；Amount=赠送数量）。</summary>
        UnsmithTool,

        // ── 7c-1 新增：规则修正类（改系统参数，7c-1 用户定稿 2026-11-01）──

        /// <summary>利息封顶加值（Amount=加值；5 → 5+加值，Economy 侧永久生效、只加不减）。</summary>
        InterestCapUp,

        /// <summary>买经验减价（Amount=减价；升到 6 级后生效：4 → 4-减价，钳 ≥1）。</summary>
        XpCostDown,

        /// <summary>定向刷新（Amount=周期 N；每第 N 次刷新全架出"备战席最左侧棋子的费用档"）。
        /// 备战席为空时该次不触发（计数保留，不消耗定向机会）。</summary>
        ShopAffinity,

        /// <summary>装备升级费折扣（Amount=减价；EquipmentId=限定系别：0=全部、1=物理系、2=法术系、3=防御系）。</summary>
        UpgradeDiscount,

        /// <summary>战败免扣血（Amount=次数；非 Boss 关战败时消耗一次跳过扣血——奖励关豁免与 Boss 清零判负不适用）。</summary>
        LossShield,

        /// <summary>随机基础装备（Amount=件数；EquipmentId=池限定：0=三种散件随机、3=仅防御散件）。
        /// 抽取用会话随机源——确定性同种子同结果。</summary>
        RandomEquipment,
    }

    /// <summary>
    /// 单条效果。正负同构（决策 #18③）：一条策略持多个条目，正负共存、玩家看总量权衡。
    /// HookFactory 与 Type 正交（附加字段）：任何条目都可附带钩子——"既发效果又带钩子"是合法复合。
    /// </summary>
    public readonly struct PolicyEffect
    {
        public readonly PolicyEffectType Type;

        /// <summary>金币（可负、≠0）/ 经验（恒 &gt;0）/ 战后扣血（恒 &gt;0）/ 拆卸器（恒 &gt;0）/
        /// 利息加值 / 经验减价 / 刷新周期 / 升级减价 / 豁免次数 / 随机件数（各恒 &gt;0）；修饰器与装备类恒 0。</summary>
        public readonly int Amount;

        /// <summary>TeamModifier 时非空（构造期校验）；模板 SourceId 恒 0（选中时盖实例章）。</summary>
        public readonly StatModifier[] Modifiers;

        /// <summary>Equipment 时 &gt;0（固定散件 id）；UpgradeDiscount/RandomEquipment 时 = 系别限定（0=全部/1=物理/2=法术/3=防御）；其余恒 0。</summary>
        public readonly int EquipmentId;

        /// <summary>钩子工厂（与 Type 正交）：选中当场留存，每场 StartBattle 新造一只注册
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

        /// <summary>利息封顶加值（7c-1）。</summary>
        public static PolicyEffect InterestCap(int bonus)
            => new PolicyEffect(PolicyEffectType.InterestCapUp, bonus, null, 0, null);

        /// <summary>买经验减价（7c-1：L6 后生效）。</summary>
        public static PolicyEffect XpDiscount(int down)
            => new PolicyEffect(PolicyEffectType.XpCostDown, down, null, 0, null);

        /// <summary>定向刷新（7c-1：每第 N 刷触发一次）。</summary>
        public static PolicyEffect ShopAffinity(int period)
            => new PolicyEffect(PolicyEffectType.ShopAffinity, period, null, 0, null);

        /// <summary>装备升级费折扣（7c-1；filter：0=全部、1=物理系、2=法术系、3=防御系）。</summary>
        public static PolicyEffect UpgradeCostDown(int down, int filter = 0)
            => new PolicyEffect(PolicyEffectType.UpgradeDiscount, down, null, filter, null);

        /// <summary>战败免扣血（7c-1：非 Boss 关，次数制）。</summary>
        public static PolicyEffect LossShields(int count)
            => new PolicyEffect(PolicyEffectType.LossShield, count, null, 0, null);

        /// <summary>随机基础装备（7c-1；filter：0=三种散件、3=仅防御散件）。</summary>
        public static PolicyEffect RandomGear(int count, int filter = 0)
            => new PolicyEffect(PolicyEffectType.RandomEquipment, count, null, filter, null);
    }

    /// <summary>一条策略 = 正/负效果条目的组合（#18③）；7c-1 起为九条正式词条（用户定稿 2026-11-01）。</summary>
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
    /// Id 唯一且 ≥1、条目数组非空、类型字段自洽（修饰器类带数组、金币 ≠0、经验 &gt;0、固定装备带 id、
    /// 战后扣血/拆卸器/利息/折扣/刷新/豁免/随机件数 &gt;0、系别限定 ∈ 0~3）。
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
                    case PolicyEffectType.InterestCapUp:
                    case PolicyEffectType.XpCostDown:
                    case PolicyEffectType.ShopAffinity:
                    case PolicyEffectType.LossShield:
                    case PolicyEffectType.RandomEquipment:
                        if (e.Amount <= 0)
                            throw new ArgumentException($"策略 {def.Id}「{def.DisplayName}」规则修正类条目（{e.Type}）必须恒正");
                        break;
                    case PolicyEffectType.UpgradeDiscount:
                        if (e.Amount <= 0)
                            throw new ArgumentException($"策略 {def.Id}「{def.DisplayName}」升级折扣条目必须恒正");
                        if (e.EquipmentId < 0 || e.EquipmentId > 3)
                            throw new ArgumentException($"策略 {def.Id}「{def.DisplayName}」升级折扣的系别限定必须是 0~3（当前 {e.EquipmentId}）");
                        break;
                }
            }
            _byId[def.Id] = def;
            _all.Add(def);
        }

        /// <summary>
        /// 九条正式词条（7c-1 用户定稿 2026-11-01；命名 = 占位可改，正式名待用户定）：
        /// 1 放贷人（经济）/ 2 学者会（经验）/ 3 占卜师（定向刷）/ 4 战意灌注（物理增益+钩子）/
        /// 5 奥术共鸣（法术增益+钩子）/ 6 军械商（装备+全局折扣）/ 7 军械师·防（防御装备+防御折扣）/
        /// 8 辎重队（三件随机）/ 9 免死金牌（败血豁免 ×2）。
        /// </summary>
        private static PolicyTable BuildDefault()
        {
            return new PolicyTable(new List<PolicyDef>
            {
                new PolicyDef(1, "放贷人", "金币 +20，利息封顶 +2（5 → 7）",
                    new[] { PolicyEffect.Gold(20), PolicyEffect.InterestCap(2) }),
                new PolicyDef(2, "学者会", "经验 +10，升到 6 级后买经验 -1 金",
                    new[] { PolicyEffect.Xp(10), PolicyEffect.XpDiscount(1) }),
                new PolicyDef(3, "占卜师", "每刷新 6 次，该次刷新全部出备战席最左侧棋子的费用档",
                    new[] { PolicyEffect.ShopAffinity(6) }),
                new PolicyDef(4, "战意灌注", "我方物理伤害 +30%，攻击 +10",
                    new PolicyEffect[]
                    {
                        // 钩子与效果正交：同一条修饰器条目附带全队类型增伤钩子（合法复合）
                        new PolicyEffect(PolicyEffectType.TeamModifier, 0,
                            new[] { new StatModifier(StatKind.AttackPower, StatOp.Flat, 10f) }, 0,
                            members => new CombatHookLibrary.TypeDamageAmpHook(members, DamageType.Physical, 30f,
                                ModSourceId.StrategyBase + 91)),
                    }),
                new PolicyDef(5, "奥术共鸣", "我方法术伤害 +20%，法强 +15",
                    new PolicyEffect[]
                    {
                        new PolicyEffect(PolicyEffectType.TeamModifier, 0,
                            new[] { new StatModifier(StatKind.AbilityPower, StatOp.Flat, 15f) }, 0,
                            members => new CombatHookLibrary.TypeDamageAmpHook(members, DamageType.Magic, 20f,
                                ModSourceId.StrategyBase + 92)),
                    }),
                new PolicyDef(6, "军械商", "随机 1 件基础装备，装备升级费 -1",
                    new[] { PolicyEffect.RandomGear(1), PolicyEffect.UpgradeCostDown(1, 0) }),
                new PolicyDef(7, "军械师·防", "随机 2 件基础防御装备，防御系升级费 -1",
                    new[] { PolicyEffect.RandomGear(2, 3), PolicyEffect.UpgradeCostDown(1, 3) }),
                new PolicyDef(8, "辎重队", "随机 3 件基础装备",
                    new[] { PolicyEffect.RandomGear(3) }),
                new PolicyDef(9, "免死金牌", "非 Boss 关战败时免扣血，共 2 次",
                    new[] { PolicyEffect.LossShields(2) }),
            });
        }
    }
}
