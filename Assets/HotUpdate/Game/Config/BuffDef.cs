using System;
using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// Buff 定义（表的每一行）。readonly struct——定义一旦入库永不修改。
    ///
    /// Buff 的三种生效载体（可组合，全空 = 无效果、编表事故、构造校验拒）：
    ///  · Modifiers   面板修饰器（随 Buff 挂/摘——战斗内瞬时挂账，不走备战期重算制）
    ///  · DotDamage   每 tick 伤害（DoT：持续伤害；0 = 无）
    ///  · HotHeal     每 tick 治疗（Hot：持续回复；0 = 无）
    ///
    /// 生命周期（阶段3-接口设计 第六章，决策：阶段3 条目 3）：
    ///  · DurationTicks 持续时间（战斗内时间：1 tick = 1/30 秒）；每次叠层刷新到满时长；
    ///  · MaxStacks     层数上限（1 = 不叠层）；每层独立计时；
    ///  · 战斗结束即清空（Buff 是战斗内状态，不跨战斗——与"蓝不跨战斗"同纪律）。
    ///
    /// 纪律：DoT/Hot 不引入新随机源（数值是表写死的）；死亡目标的 DoT 自然蒸发
    /// （账本清层、不再结算——尸体不在行动序里）。
    /// </summary>
    public readonly struct BuffDef
    {
        /// <summary>Buff id（>0；0 = 无/查无哨兵。开放内容表 → int，P3-5）。</summary>
        public readonly int Id;

        /// <summary>仅显示用（禁止当键）。</summary>
        public readonly string DisplayName;

        /// <summary>持续时间（tick 数，1 tick = 1/30 秒；每次叠层刷新回满）。</summary>
        public readonly int DurationTicks;

        /// <summary>层数上限（1 = 不叠层）。</summary>
        public readonly int MaxStacks;

        /// <summary>面板修饰器模板（null/空 = 无面板效果；SourceId 恒 0，挂载时盖章）。</summary>
        public readonly StatModifier[] Modifiers;

        /// <summary>每 tick 伤害（DoT；0 = 无。走真实伤害路径：不打折、不可暴击）。</summary>
        public readonly int DotDamage;

        /// <summary>每 tick 治疗（Hot；0 = 无）。</summary>
        public readonly int HotHeal;

        public BuffDef(int id, string displayName, int durationTicks, int maxStacks,
            StatModifier[] modifiers, int dotDamage, int hotHeal)
        {
            Id = id;
            DisplayName = displayName ?? "";
            DurationTicks = durationTicks;
            MaxStacks = maxStacks;
            Modifiers = modifiers == null ? Array.Empty<StatModifier>() : (StatModifier[])modifiers.Clone();
            DotDamage = dotDamage;
            HotHeal = hotHeal;
        }

        /// <summary>本 Buff 是否携带任何效果（构造校验用：全空 = 建表事故）。</summary>
        public bool HasAnyEffect
            => (Modifiers != null && Modifiers.Length > 0) || DotDamage > 0 || HotHeal > 0;
    }

    /// <summary>
    /// Buff 表（实例 + 可注入双源模式，同 CharacterTable/EquipmentTable 纪律）。
    /// 构造即校验（fail fast）：id 唯一且 ≥1、持续时间 ≥1 tick、层数上限 ≥1、
    /// 至少携带一种效果（修饰器/DoT/Hot）——空 Buff 是建表事故。
    /// </summary>
    public sealed class BuffTable
    {
        /// <summary>代码内置基线（占位模板，D2-11 纪律——正式内容由用户构想填表）。</summary>
        public static BuffTable Default { get; } = BuildDefault();

        private readonly Dictionary<int, BuffDef> _byId = new Dictionary<int, BuffDef>();
        private readonly List<BuffDef> _all = new List<BuffDef>();

        public BuffTable(IReadOnlyList<BuffDef> defs)
        {
            if (defs == null) throw new ArgumentNullException(nameof(defs));
            foreach (var def in defs)
            {
                if (def.Id < 1)
                    throw new ArgumentException($"Buff id 必须 ≥ 1（当前 {def.Id}）");
                if (_byId.ContainsKey(def.Id))
                    throw new ArgumentException($"Buff id 重复：{def.Id}");
                if (def.DurationTicks < 1)
                    throw new ArgumentException($"Buff {def.Id}「{def.DisplayName}」持续时间必须 ≥ 1 tick（1 tick = 1/30 秒）");
                if (def.MaxStacks < 1)
                    throw new ArgumentException($"Buff {def.Id}「{def.DisplayName}」层数上限必须 ≥ 1（1 = 不叠层）");
                if (!def.HasAnyEffect)
                    throw new ArgumentException($"Buff {def.Id}「{def.DisplayName}」必须至少携带一种效果（修饰器/DoT/Hot）");
                _byId[def.Id] = def;
                _all.Add(def);
            }
        }

        /// <summary>全部 Buff 定义（保持注册顺序）。</summary>
        public IReadOnlyList<BuffDef> All => _all;

        /// <summary>按 id 查定义；查无返回 default（Id=0）。调用方先查 Id 再用。</summary>
        public BuffDef Get(int id) => _byId.TryGetValue(id, out var def) ? def : default;

        /// <summary>
        /// 代码内置基线：1~4 = 批次4 的四个方向占位（攻击增益叠层 / DoT / Hot / 防御增益）；
        /// 5~13 = 7c-2 技能附带 Buff（淬毒 + 各 1~2 费小 buff + 圣殿之王全队 buff）。
        /// 数值 = 第一版锚点，平衡期随压测重定。
        /// </summary>
        private static BuffTable BuildDefault()
        {
            return new BuffTable(new List<BuffDef>
            {
                // 1 攻击增益·叠层型：3 秒内攻击 +10%，可叠 3 层（每层独立计时）
                new BuffDef(1, "战意", 90, 3,
                    new[] { new StatModifier(StatKind.AttackPower, StatOp.Percent, 10f) }, 0, 0),
                // 2 持续伤害·DoT：3 秒每 tick 1 点真实伤害（90 tick × 1 = 90 点总量）
                new BuffDef(2, "灼烧", 90, 1,
                    null, 1, 0),
                // 3 持续治疗·Hot：3 秒每 tick 1 点回复
                new BuffDef(3, "回复", 90, 1,
                    null, 0, 1),
                // 4 防御增益·不叠层：5 秒护甲 +15
                new BuffDef(4, "石肤", 150, 1,
                    new[] { new StatModifier(StatKind.Armor, StatOp.Flat, 15f) }, 0, 0),

                // ── 7c-2 技能附带 Buff（5~13；名字占位、数值 = 第一版锚点随压测重定）──
                // 5 淬毒（影刃·影袭附带）：4 秒每 tick 2 真实（总量 240）；封顶 1 层不刷新（与灼烧同构——旧毒掉完再挂新毒）
                new BuffDef(5, "淬毒", 120, 1, null, 2, 0),
                // 6 硬皮（野猪武士/民兵）：6 秒双抗 +25
                new BuffDef(6, "硬皮", 180, 1,
                    new[] { new StatModifier(StatKind.Armor, StatOp.Flat, 25f),
                            new StatModifier(StatKind.MagicResist, StatOp.Flat, 25f) }, 0, 0),
                // 7 符文壁垒（符文守卫）：6 秒双抗 +35（2 费坦的强档硬皮）
                new BuffDef(7, "符文壁垒", 180, 1,
                    new[] { new StatModifier(StatKind.Armor, StatOp.Flat, 35f),
                            new StatModifier(StatKind.MagicResist, StatOp.Flat, 35f) }, 0, 0),
                // 8 战吼（1 费射 ×2 自身）：5 秒攻击 +20%
                new BuffDef(8, "战吼", 150, 1,
                    new[] { new StatModifier(StatKind.AttackPower, StatOp.Percent, 20f) }, 0, 0),
                // 9 强化（2 费射：夜行自身 / 弓卫递给攻最高队友）：5 秒攻击 +25%
                new BuffDef(9, "强化", 150, 1,
                    new[] { new StatModifier(StatKind.AttackPower, StatOp.Percent, 25f) }, 0, 0),
                // 10 疾影（暗狼刺客自身）：4 秒攻击间隔 -30%（间隔负百分比 = 攻速提升；下限 0.1 由面板钳制承担）
                new BuffDef(10, "疾影", 120, 1,
                    new[] { new StatModifier(StatKind.AttackInterval, StatOp.Percent, -30f) }, 0, 0),
                // 11 汲魔（巫术学徒自身）：4 秒每秒回蓝 +5——走既有 ManaRegenPerSecond 面板修饰器维，无新载体
                new BuffDef(11, "汲魔", 120, 1,
                    new[] { new StatModifier(StatKind.ManaRegenPerSecond, StatOp.Flat, 5f) }, 0, 0),
                // 12 圣愈（随军牧师递给生命比例最低队友）：5 秒每 tick 3 点 Hot（总量 150 ≈ 1 费棋子 1/3 血）
                new BuffDef(12, "圣愈", 150, 1, null, 0, 3),
                // 13 王权庇护（圣殿之王·王权裁决，全队）：10 秒双抗 +30——5 费防御向终极的份量
                new BuffDef(13, "王权庇护", 300, 1,
                    new[] { new StatModifier(StatKind.Armor, StatOp.Flat, 30f),
                            new StatModifier(StatKind.MagicResist, StatOp.Flat, 30f) }, 0, 0),
            });
        }
    }
}
