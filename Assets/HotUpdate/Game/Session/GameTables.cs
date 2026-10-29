using System;

namespace AutoChess.Game
{
    /// <summary>
    /// 表捆绑注入包（阶段2-接口设计 9.1）：GameSession 构造时整包接收，全系统不各自持表——
    /// 换表只换这一包（阶段4 表现层从 SO 资产重建各表后注入，Session 永不认识 SO）。
    /// 批次7 版捆七表（阶段2 全量：角色/概率/回合/编成/羁绊/矿池/策略）；
    /// 阶段3 批次2 起捆八表（+技能表 Skills——施法循环的查表来源）；
    /// 批次3 起捆九表（+装备表 Equipment——穿戴/升级/卖返还的查表来源；null = 无装备表，装备操作全拒）。
    /// Default = 各表 Default 打包（测试基线与兜底，同 CharacterTable 双源模式）——表全部构造后只读，共享安全。
    /// </summary>
    public sealed class GameTables
    {
        public CharacterTable Characters;
        public ShopOdds Odds;
        public RoundTable Rounds;
        public EnemyTable Enemies;
        public SynergyTable Synergies;
        public OreTable Ores;
        public PolicyTable Policies;
        public SkillTable Skills;
        public EquipmentTable Equipment;

        public static GameTables Default { get; } = new GameTables
        {
            Characters = CharacterTable.Default,
            Odds = ShopOdds.Default,
            Rounds = RoundTable.Default,
            Enemies = EnemyTable.Default,
            Synergies = SynergyTable.Default,
            Ores = OreTable.Default,
            Policies = PolicyTable.Default,
            Skills = SkillTable.Default,
            Equipment = EquipmentTable.Default,
        };
    }
}
