using System;

namespace AutoChess.Game
{
    /// <summary>
    /// 棋子静态定义（表的每一行）。readonly struct——定义一旦入库永不修改。
    /// ID 体系（设计文档 3.4）：所有跨系统引用一律 int Id，从 1 开始，0 恒表示"无/非法"；
    /// DisplayName 只给日志/UI，禁止当键（键给机器用，名字给人看）。
    /// </summary>
    public readonly struct CharacterDef
    {
        public readonly int Id;          // 全局唯一，> 0（0 = 非法/未找到）
        public readonly string DisplayName; // 仅显示用
        public readonly int Cost;        // 商店费用 1~5
        public readonly UnitStats BaseStats;   // 1 星基准（含暴击 5/50 出厂基准）
        public readonly SynergyId[] Synergies;   // 羁绊标签数组（enum——封闭集合，P3-5；阶段2 建表）
        public readonly RoleId RoleId;   // 职业（enum——封闭集合，阶段3-接口设计 P3-5；决策 #22⑥ 职业轴）
        public readonly int SkillId;     // 技能 id（int——开放内容表，P3-5；0 = 无技能；"3 费+才有技能"是数据录入纪律，不写 if(cost)）

        // ── 星级规则（全局常数，千场压测统一调）──

        /// <summary>每升一星 HP ×1.8（3 星复利 = ×3.24）。</summary>
        public const float HpPerStar = 1.8f;

        /// <summary>每升一星 攻击 ×1.5（3 星复利 = ×2.25）。</summary>
        public const float AtkPerStar = 1.5f;

        /// <summary>
        /// 五参构造器（阶段2 原有签名，原样保留）：职业与技能恒无（RoleId.None / 0）。
        /// 为什么保留重载而不是改签名：既有 11 处调用点（测试自建棋子）不该为新字段被迫改写——
        /// 新字段是"可选标注"，不是必填项，改签名会把无关改动带进测试基线。
        /// </summary>
        public CharacterDef(int id, string displayName, int cost, UnitStats baseStats, SynergyId[] synergies)
            : this(id, displayName, cost, baseStats, synergies, RoleId.None, 0)
        {
        }

        /// <summary>七参构造器（阶段3：带职业与技能标注）。</summary>
        public CharacterDef(int id, string displayName, int cost, UnitStats baseStats, SynergyId[] synergies,
            RoleId roleId, int skillId)
        {
            Id = id;
            DisplayName = displayName ?? "";
            Cost = cost;
            BaseStats = baseStats;
            // 防御性拷贝：def 是值语义，不许外部数组事后改动污染表内容
            Synergies = synergies == null ? Array.Empty<SynergyId>() : (SynergyId[])synergies.Clone();
            RoleId = roleId;
            SkillId = skillId;
        }

        /// <summary>未知/未找到的哨兵：Id = 0。调用方先查 Id 再碰其他字段。</summary>
        public static CharacterDef None => default;

        /// <summary>
        /// 按星级缩放面板：星级放大"量"（血/攻复利），不放大"质"——
        /// 护甲/法抗/法强/射程/攻速/移速/暴击两维原样保留。
        /// 乘子全程 float 累乘，只在最后取整一次（D2 口径）。
        /// </summary>
        public UnitStats StatsForStar(int star)
        {
            if (star < 1 || star > 3)
                throw new ArgumentOutOfRangeException(nameof(star), "星级必须为 1~3");

            if (star == 1)
                return BaseStats;

            float hpMul = 1f, atkMul = 1f;
            for (int i = 1; i < star; i++) // 复利：逐星相乘，中途不取整
            {
                hpMul *= HpPerStar;
                atkMul *= AtkPerStar;
            }

            UnitStats s = BaseStats;
            s.MaxHp = GameMath.RoundToInt(BaseStats.MaxHp * hpMul);
            s.AttackPower = GameMath.RoundToInt(BaseStats.AttackPower * atkMul);
            return s;
        }

        /// <summary>合成该星级所需的 1 星棋子数：1 / 3 / 9。</summary>
        public static int CopiesForStar(int star)
        {
            if (star < 1 || star > 3)
                throw new ArgumentOutOfRangeException(nameof(star), "星级必须为 1~3");

            int copies = 1;
            for (int i = 1; i < star; i++) copies *= 3;
            return copies;
        }

        /// <summary>
        /// 卖出价 = 总投入 −(星级−1)：升星折损每级 1 块——
        /// 惩罚"反复横跳"（升星再卖要亏），不惩罚"试错"（1 星买入卖出零损耗）。
        /// </summary>
        public int SellPrice(int star)
        {
            return CopiesForStar(star) * Cost - (star - 1);
        }
    }
}