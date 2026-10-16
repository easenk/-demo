namespace AutoChess.Game
{
    /// <summary>
    /// 修饰器来源号段（设计文档 4.2；2026-10-16 随决策 #17/#18 定稿）。
    /// SourceId 是整个修饰器系统的枢纽：装备/Buff/羁绊都靠它做"整批卸载"。
    /// 号段防止不同系统的 id 撞车（撞车 = 卸 A 时误伤 B 的修饰器）。
    /// 实际 id = 基址 + 各系统自家序号。
    /// 变更记录：原宝物段(2000~2999)让位给策略系统（宝物三选一被策略取代，宝物系统不做）；
    /// 原测试段(3000+)让位给晶矿奖励段，测试专用段推到 4000+。
    /// 纪律：测试代码一律引用这里的常量，禁止裸数字 SourceId——号段再调整时只改这一处。
    /// </summary>
    public static class ModSourceId
    {
        /// <summary>无来源（测试/临时修饰器）。0 不是"一个来源"——按源卸载时禁用 0。</summary>
        public const int None = 0;

        /// <summary>系统段（1~999）：羁绊组、战斗内 Buff、击碎/削抗等 debuff、格子/地形效果。</summary>
        public const int SystemBase = 1;
        public const int SystemMax  = 999;

        /// <summary>装备段（1000~1999）：1000 + 槽位号。</summary>
        public const int EquipmentBase = 1000;
        public const int EquipmentMax  = 1999;

        /// <summary>策略段（2000~2999）：2000 + 实例序号。原宝物段让位——宝物三选一被策略系统取代（决策#18）。</summary>
        public const int StrategyBase = 2000;
        public const int StrategyMax  = 2999;

        /// <summary>晶矿奖励段（3000~3999）：3000 + 档位/实例序号；全队修饰器类奖励（决策#17）。</summary>
        public const int OreRewardBase = 3000;
        public const int OreRewardMax  = 3999;

        /// <summary>测试专用段（4000+）。测试代码一律引用本常量，禁止裸数字。</summary>
        public const int TestBase = 4000;
    }
}
