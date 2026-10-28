using System;
using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// 棋子查表：int id → CharacterDef。
    /// 为什么是"实例 + 可注入"而不是静态类：正式内容由阶段4 从 Res/Config 读取后
    /// 注入新实例；Default 是代码内置基线，测试与未注入配置时兜底。
    /// ScriptableObject 永不进逻辑层（引擎类型）；表现层只当"搬运工"：
    /// 读 SO → 填 CharacterDef struct → 喂给本构造器。
    /// </summary>
    public sealed class CharacterTable
    {
        private readonly Dictionary<int, CharacterDef> _byId;
        private readonly List<CharacterDef> _ordered;              // 保持传入顺序（All / GetByCost 都按它走）
        private readonly Dictionary<int, List<CharacterDef>> _byCost; // 阶段2 商店加权抽取用

        public CharacterTable(IReadOnlyList<CharacterDef> defs)
        {
            if (defs == null) throw new ArgumentNullException(nameof(defs));

            _byId = new Dictionary<int, CharacterDef>(defs.Count);
            _ordered = new List<CharacterDef>(defs.Count);
            _byCost = new Dictionary<int, List<CharacterDef>>();

            foreach (var def in defs)
            {
                if (def.Id <= 0)
                    throw new ArgumentException($"棋子 Id 必须为正整数（当前 {def.Id}，显示名 {def.DisplayName}）");
                if (_byId.ContainsKey(def.Id))
                    throw new ArgumentException($"棋子 Id 重复：{def.Id}（{def.DisplayName}）");

                _byId.Add(def.Id, def);
                _ordered.Add(def);

                if (!_byCost.TryGetValue(def.Cost, out var bucket))
                {
                    bucket = new List<CharacterDef>();
                    _byCost.Add(def.Cost, bucket);
                }
                bucket.Add(def);
            }
        }

        /// <summary>代码内置基线：测试与未注入配置时的兜底（正式名单阶段3 平衡期定）。</summary>
        public static CharacterTable Default { get; } = new CharacterTable(BuildDefaultDefs());

        public int Count => _ordered.Count;

        /// <summary>全部定义（保持构造时的传入顺序）。</summary>
        public IReadOnlyList<CharacterDef> All => _ordered;

        /// <summary>查表：未知 id 返回 CharacterDef.None（Id=0）。调用方先查 Id 再用。</summary>
        public CharacterDef Get(int id)
        {
            return _byId.TryGetValue(id, out var def) ? def : CharacterDef.None;
        }

        public bool TryGet(int id, out CharacterDef def)
        {
            if (_byId.TryGetValue(id, out def)) return true;
            def = CharacterDef.None;
            return false;
        }

        /// <summary>按费用取全部棋子（商店第二段加权抽取用，阶段2）。无该费档返回空表而非 null。</summary>
        public IReadOnlyList<CharacterDef> GetByCost(int cost)
        {
            return _byCost.TryGetValue(cost, out var bucket) ? bucket : Array.Empty<CharacterDef>();
        }

        /// <summary>
        /// 代码内置基线：决策 #22② 的 18 棋子矩阵（占位名 + 占位面板 + 双标签）——
        /// 与 SynergyTable.Default（六族 v1）、ShopPool（18 种 229 份）互为咬合的测试基线；
        /// 面板数值 = 第一版锚点，阶段3 平衡期连千场压测统一重定。
        /// 蓝条数据纪律（批次5.5）：3 费+棋子配 MaxMana>0（1~2 费恒 0 = 无蓝条单位）——
        /// 这是表数据纪律，不写 if(cost) 进战斗循环。
        /// 职业与技能标注（阶段3 批次1，决策 #22⑥）：每棋子一个职业（enum，回蓝速率轴）
        /// + 可选技能 id（int——开放内容表）。族标签同批 enum 化（SynergyId，P3-5）。
        /// </summary>
        private static CharacterDef[] BuildDefaultDefs()
        {
            return new[]
            {
                //  id  名字        费  族标签                              职业            技能  血   攻   甲  法抗 射程 攻间隔 移间隔 蓝上限
                Def( 1, "野猪武士",   1, new[] { SynergyId.Brute, SynergyId.Bulwark },  RoleId.Tank,     0,  60, 12, 35, 25, 1, 1.1f, 1.0f),
                Def( 2, "兽人投矛手", 1, new[] { SynergyId.Brute, SynergyId.Ranger },   RoleId.Marksman, 0,  55, 14, 25, 20, 2, 1.2f, 1.0f),
                Def( 3, "民兵",       1, new[] { SynergyId.Bulwark, SynergyId.Crown },  RoleId.Tank,     0,  65, 11, 30, 30, 1, 1.2f, 1.0f),
                Def( 4, "巫术学徒",   1, new[] { SynergyId.Arcane, SynergyId.Shadow },  RoleId.Mage,     0,  45,  9, 20, 25, 2, 1.4f, 1.0f),
                Def( 5, "魔弹射手",   1, new[] { SynergyId.Arcane, SynergyId.Ranger },  RoleId.Marksman, 0,  45, 13, 20, 20, 3, 1.3f, 1.1f),

                Def( 6, "暗狼刺客",   2, new[] { SynergyId.Brute, SynergyId.Shadow },   RoleId.Assassin, 0,  70, 16, 25, 20, 1, 1.0f, 0.9f),
                Def( 7, "符文守卫",   2, new[] { SynergyId.Bulwark, SynergyId.Arcane }, RoleId.Tank,     0,  80, 13, 40, 35, 1, 1.3f, 1.0f),
                Def( 8, "夜行狙击手", 2, new[] { SynergyId.Ranger, SynergyId.Shadow },  RoleId.Marksman, 0,  55, 17, 20, 20, 3, 1.4f, 1.0f),
                Def( 9, "弓卫侍从",   2, new[] { SynergyId.Crown, SynergyId.Ranger },   RoleId.Marksman, 0,  60, 15, 25, 25, 3, 1.2f, 1.0f),
                Def(10, "随军牧师",   2, new[] { SynergyId.Crown, SynergyId.Arcane },  RoleId.Mage,     0,  65, 12, 25, 30, 2, 1.3f, 1.0f),

                Def(11, "战团督军",   3, new[] { SynergyId.Brute, SynergyId.Bulwark },  RoleId.Warrior,  1, 150, 22, 45, 35, 1, 1.2f, 1.0f, maxMana: 60),
                Def(12, "王家神射",   3, new[] { SynergyId.Ranger, SynergyId.Crown },   RoleId.Marksman, 2,  90, 26, 25, 25, 4, 1.3f, 1.0f, maxMana: 60),
                Def(13, "战地法师",   3, new[] { SynergyId.Arcane, SynergyId.Shadow },  RoleId.Mage,     3,  85, 14, 25, 35, 2, 1.5f, 1.0f, maxMana: 60),

                Def(14, "影刃",       4, new[] { SynergyId.Shadow, SynergyId.Ranger },  RoleId.Assassin, 4, 160, 32, 30, 25, 1, 0.9f, 0.8f, maxMana: 70),
                Def(15, "铁甲巨兽",   4, new[] { SynergyId.Bulwark, SynergyId.Brute },  RoleId.Tank,     5, 320, 24, 60, 50, 1, 1.6f, 1.1f, maxMana: 70),
                Def(16, "宫廷大法师", 4, new[] { SynergyId.Crown, SynergyId.Arcane },  RoleId.Mage,     6, 150, 18, 30, 40, 3, 1.5f, 1.0f, maxMana: 70),

                Def(17, "圣殿之王",   5, new[] { SynergyId.Crown, SynergyId.Bulwark },  RoleId.Tank,     7, 500, 35, 70, 60, 1, 1.4f, 1.0f, maxMana: 80),
                Def(18, "魔渊兽王",   5, new[] { SynergyId.Shadow, SynergyId.Brute },   RoleId.Warrior,  8, 450, 45, 50, 45, 1, 1.1f, 0.9f, maxMana: 80),
            };

            // 速记工厂：族标签/职业/技能 + hp/atk/armor(双抗同值)/range/间隔 + 可选蓝条（3 费+）；
            // 法强基准 100、暴击出厂基准 5/50（#10/#12 口径）；星级放大只动血/攻（阶段1 规则）。
            // 回蓝两维来自职业表（决策 #22⑥）：职业是回蓝速率轴的唯一真源——
            // 无蓝条单位（MaxMana=0）不建回蓝账本，故两维恒 0。
            static CharacterDef Def(int id, string name, int cost, SynergyId[] synergies, RoleId roleId, int skillId,
                int hp, int atk, int armor, int mr, int range, float atkIv, float moveIv, int maxMana = 0)
            {
                RoleDef role = RoleTable.Default.Get(roleId);
                if (roleId != RoleId.None && role.Id == RoleId.None)
                    throw new ArgumentException($"棋子 {id}「{name}」的职业 {roleId} 不在职业表里");

                bool hasMana = maxMana > 0;
                return new CharacterDef(id, name, cost,
                    new UnitStats
                    {
                        MaxHp = hp,
                        AttackPower = atk,
                        Armor = armor,
                        MagicResist = mr,
                        AbilityPower = 100,
                        AttackRange = range,
                        AttackInterval = atkIv,
                        MoveInterval = moveIv,
                        CritChance = 5,
                        CritDamage = 50,
                        MaxMana = maxMana,
                        ManaPerHit = hasMana ? role.ManaPerHit : 0,
                        ManaRegenPerSecond = hasMana ? role.ManaRegenPerSecond : 0,
                    },
                    synergies, roleId, skillId);
            }
        }
    }
}