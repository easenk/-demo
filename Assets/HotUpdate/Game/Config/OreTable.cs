using System;
using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// 晶矿奖励类型（P3-8，2026-10-29 修订为四类——推翻 2026-10-27"只有三种"口径）：
    /// 拆卸器低/中档掉落且两档数量不同、高档不掉（高档留给战力型奖励：装备散件/大额金币）。
    /// </summary>
    public enum OreRewardType
    {
        /// <summary>金币：Amount 就是发放数。</summary>
        Gold,

        /// <summary>经验：Amount 就是发放数。</summary>
        Xp,

        /// <summary>装备散件（决策 #12⑧：只给 3 种散件，大件不直接发——分支定制权永远在玩家升级时行使）。
        /// 批次6 起 PickOre 直接入背包（AddEquipment，查装备表）。</summary>
        Equipment,

        /// <summary>拆卸器（P3-8，2026-10-29 拍板）：Amount 就是赠送数量（低/中档池配条目、高档不配）。
        /// 批次6 起 PickOre 直接入背包（AddUnsmithTools）。</summary>
        UnsmithTool,
    }

    /// <summary>
    /// 单条奖励定义（编表纪律：Amount 与 EquipmentId 按类型二选一填，另一个恒 0；
    /// 类型字段自洽在表构造期校验，建表事故当场炸）。
    /// </summary>
    public readonly struct OreDef
    {
        public readonly int Id;

        /// <summary>奖励类型（四值枚举：金币/经验/装备散件/拆卸器）。</summary>
        public readonly OreRewardType Type;

        /// <summary>显示用描述（禁止当键）。</summary>
        public readonly string Desc;

        /// <summary>金币/经验/拆卸器数量（此三类必须 &gt; 0，编表纪律）；装备类恒 0（数量走 EquipmentId）。</summary>
        public readonly int Amount;

        /// <summary>装备散件 id（装备类必须 &gt; 0——构造期校验；"在装备表内"是编表纪律，
        /// 运行期由 AddEquipment 查表把关：表外 id = 纯拒绝、矿留场）；其余类恒 0。</summary>
        public readonly int EquipmentId;

        public OreDef(int id, OreRewardType type, string desc, int amount, int equipmentId)
        {
            Id = id;
            Type = type;
            Desc = desc ?? "";
            Amount = amount;
            EquipmentId = equipmentId;
        }
    }

    /// <summary>一个挡位的奖励池（开矿时 GameRandom 从池里抽 1 条立即发放，#17④）。</summary>
    public readonly struct OreTierDef
    {
        /// <summary>挡位 1~3（低/中/高，命名与视觉待用户定）。</summary>
        public readonly int Tier;

        /// <summary>池内 OreDef 的 id（非空、且都必须在定义集里——构造期校验）。</summary>
        public readonly int[] OreIds;

        public OreTierDef(int tier, int[] oreIds)
        {
            Tier = tier;
            OreIds = oreIds;
        }
    }

    /// <summary>
    /// 晶矿奖励表（三挡各一池；实例 + Default 双源模式，同 CharacterTable）。
    /// 构造即校验（fail fast，同 EnemyTable 纪律）：三挡 1/2/3 齐全且不重复（编成表任何挡位都可能出现，
    /// 缺挡 = 开矿期运行时炸，比构造期炸贵）、池非空、OreDef 的 id 唯一且 ≥1、
    /// 池内 id 必须在定义集里、类型字段自洽（金币/经验/拆卸器 Amount&gt;0、装备 EquipmentId&gt;0）。
    /// Default = 占位模板（D2-11：方向 2026-10-27 拍板 + P3-8 拆卸器 2026-10-29 拍板，具体数值平衡期随千场压测重定）。
    /// </summary>
    public sealed class OreTable
    {
        public static OreTable Default { get; } = BuildDefault();

        private readonly Dictionary<int, OreDef> _ores = new Dictionary<int, OreDef>();
        private readonly OreTierDef[] _tiers = new OreTierDef[3];   // 槽位 0/1/2 = 挡位 1/2/3

        public OreTable(IReadOnlyList<OreDef> ores, IReadOnlyList<OreTierDef> tiers)
        {
            if (ores == null) throw new ArgumentNullException(nameof(ores));
            if (tiers == null) throw new ArgumentNullException(nameof(tiers));

            foreach (var def in ores)
            {
                if (def.Id < 1)
                    throw new ArgumentException($"奖励 id 必须 ≥ 1（当前 {def.Id}）");
                if (_ores.ContainsKey(def.Id))
                    throw new ArgumentException($"奖励 id 重复：{def.Id}");
                switch (def.Type)
                {
                    case OreRewardType.Gold:
                    case OreRewardType.Xp:
                        if (def.Amount <= 0)
                            throw new ArgumentException($"奖励 {def.Id}：金币/经验的 Amount 必须 > 0（当前 {def.Amount}）");
                        break;
                    case OreRewardType.Equipment:
                        if (def.EquipmentId <= 0)
                            throw new ArgumentException($"奖励 {def.Id}：装备类的 EquipmentId 必须 > 0（当前 {def.EquipmentId}）");
                        break;
                    case OreRewardType.UnsmithTool:
                        if (def.Amount <= 0)
                            throw new ArgumentException($"奖励 {def.Id}：拆卸器的 Amount 必须 > 0（当前 {def.Amount}）");
                        break;
                }
                _ores[def.Id] = def;
            }

            foreach (var tier in tiers)
            {
                if (tier.Tier < 1 || tier.Tier > 3)
                    throw new ArgumentException($"挡位必须为 1~3（当前 {tier.Tier}）");
                if (tier.OreIds == null || tier.OreIds.Length == 0)
                    throw new ArgumentException($"挡位 {tier.Tier} 的奖励池不能为空");
                if (_tiers[tier.Tier - 1].OreIds != null)
                    throw new ArgumentException($"挡位重复：{tier.Tier}");
                foreach (int oreId in tier.OreIds)
                {
                    if (!_ores.ContainsKey(oreId))
                        throw new ArgumentException($"挡位 {tier.Tier} 池内的奖励 id {oreId} 未在定义集中登记");
                }
                _tiers[tier.Tier - 1] = new OreTierDef(tier.Tier, (int[])tier.OreIds.Clone());   // 防御性拷贝
            }

            for (int t = 1; t <= 3; t++)
            {
                if (_tiers[t - 1].OreIds == null)
                    throw new ArgumentException($"三挡必须齐全：挡位 {t} 未配置奖励池");
            }
        }

        /// <summary>某挡位的奖励池。挡位 1~3 构造期已保证齐全；0/越界 = 编成表数据事故，fail fast 当场炸。</summary>
        public OreTierDef GetTier(int tier)
        {
            if (tier < 1 || tier > 3 || _tiers[tier - 1].OreIds == null)
                throw new InvalidOperationException($"挡位 {tier} 未配置——编成表与矿池数据事故");
            return _tiers[tier - 1];
        }

        /// <summary>按 id 查奖励定义；查无返回 default（Id=0，防御性兜底——池内 id 构造期已保证存在）。</summary>
        public OreDef GetOre(int oreId)
            => _ores.TryGetValue(oreId, out var def) ? def : default;

        /// <summary>占位池（D2-11；P3-8 拆卸器条目批次6 填表）：T1 金3/经验2/拆卸器×1；T2 金6/经验4/拆卸器×2
        /// （两档数量不同，占位锚点）；T3 金10/经验6/装备散件（高档不配拆卸器——留战力型奖励，P3-8）。
        /// 装备占位 id=1（物理散件）；挡位命名与视觉、正式内容阶段3 由用户构思填表。</summary>
        private static OreTable BuildDefault()
        {
            var ores = new List<OreDef>
            {
                new OreDef(11, OreRewardType.Gold, "小袋金币：+3", 3, 0),
                new OreDef(12, OreRewardType.Xp, "记忆碎片：+2 经验", 2, 0),
                new OreDef(13, OreRewardType.UnsmithTool, "工具袋：拆卸器 ×1", 1, 0),
                new OreDef(21, OreRewardType.Gold, "金矿石：+6", 6, 0),
                new OreDef(22, OreRewardType.Xp, "学徒笔记：+4 经验", 4, 0),
                new OreDef(23, OreRewardType.UnsmithTool, "工具箱：拆卸器 ×2", 2, 0),
                new OreDef(31, OreRewardType.Gold, "富矿脉：+10", 10, 0),
                new OreDef(32, OreRewardType.Xp, "远古智慧：+6 经验", 6, 0),
                new OreDef(33, OreRewardType.Equipment, "神秘散件箱", 0, 1),
            };
            var tiers = new List<OreTierDef>
            {
                new OreTierDef(1, new[] { 11, 12, 13 }),
                new OreTierDef(2, new[] { 21, 22, 23 }),
                new OreTierDef(3, new[] { 31, 32, 33 }),
            };
            return new OreTable(ores, tiers);
        }
    }
}
