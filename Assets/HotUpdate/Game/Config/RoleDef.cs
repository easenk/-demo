namespace AutoChess.Game
{
    /// <summary>
    /// 职业（决策 #22⑥：职业轴独立于羁绊族，阶段3 落地）。
    ///
    /// 为什么是 enum 而不是常量类：坦/战/刺/射/术是决策定死的封闭集合。
    /// 划线标准 = 取值空间封闭与否（封闭 → enum、开放内容表 → int）：
    ///   RoleId 五个成员永不增减 → enum（编译器拦得住"把别的 id 当职业传"）；
    ///   对比 SkillId 是开放内容表（阶段4 可任意加技能条目）→ 保持 int。
    /// 职业与羁绊是两套正交的标签：棋子可同时有族标签（SynergyDef）与职业，互不覆盖——
    /// 族决定"和谁抱团"，职业决定"自己怎么回蓝"。
    /// </summary>
    public enum RoleId
    {
        /// <summary>无职业（表外棋子 / 测试自建棋子）。0 不是"一个职业"，查表按查无处理。</summary>
        None = 0,

        /// <summary>坦：前排承伤，命中回蓝慢（5）。</summary>
        Tank = 1,

        /// <summary>战：近战前排输出，命中回蓝 10。</summary>
        Warrior = 2,

        /// <summary>刺：近战爆发，命中回蓝 10。</summary>
        Assassin = 3,

        /// <summary>射：远程物理，命中回蓝 10。</summary>
        Marksman = 4,

        /// <summary>术：法系，命中回蓝 7 + 每秒固定回蓝 2（唯一有被动回蓝的职业）。</summary>
        Mage = 5,
    }

    /// <summary>
    /// 职业定义（表的每一行）。readonly struct——定义一旦入库永不修改。
    ///
    /// 数值口径（2026-10-16 用户定案的回蓝职业口径）：
    ///   坦 命中回蓝 5 / 战 10 / 刺 10 / 射 10 / 术 7 且每秒固定回蓝 2。
    /// 为什么"术"独享每秒回蓝：术士靠技能吃饭，蓝必须自己回；
    /// 其余职业靠普攻命中攒蓝，攻速就是回蓝速度。
    ///
    /// 纪律（重要）：
    ///   回蓝两维只在"有蓝条"的棋子上生效——蓝条上限=施法阈值是同一个字段，
    ///   MaxMana=0 的单位连回蓝账本都不建（Unit.AddMana 静默返回）。
    ///   所以 1~2 费棋子的面板回蓝恒 0（它们没有蓝条，见 CharacterTable 的数据纪律）。
    /// </summary>
    public readonly struct RoleDef
    {
        /// <summary>职业（enum；None=无职业）。</summary>
        public readonly RoleId Id;

        /// <summary>仅显示用（禁止当键）。</summary>
        public readonly string DisplayName;

        /// <summary>普攻命中回蓝点（0 = 不回蓝）。</summary>
        public readonly int ManaPerHit;

        /// <summary>每秒固定回蓝点（0 = 无被动回蓝）。</summary>
        public readonly int ManaRegenPerSecond;

        public RoleDef(RoleId id, string displayName, int manaPerHit, int manaRegenPerSecond)
        {
            Id = id;
            DisplayName = displayName ?? "";
            ManaPerHit = manaPerHit;
            ManaRegenPerSecond = manaRegenPerSecond;
        }
    }
}
