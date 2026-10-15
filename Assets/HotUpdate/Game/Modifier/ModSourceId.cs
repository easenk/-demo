namespace AutoChess.Game
{
    /// <summary>
    /// 修饰器来源号段（设计文档 4.2）。SourceId 是整个修饰器系统的枢纽：
    /// 装备/Buff/羁绊都靠它做"整批卸载"。号段防止不同系统的 id 撞车
    /// （撞车 = 卸 A 时误伤 B 的修饰器）。实际 id = 基址 + 各系统自家序号。
    /// </summary>
    public static class ModSourceId
    {
        /// <summary>无来源（测试/临时修饰器）。0 不是"一个来源"——按源卸载时禁用 0。</summary>
        public const int None = 0;

        /// <summary>系统段（1~999）：羁绊组、战斗内 Buff、击碎/削抗等 debuff、格子/地形效果。</summary>
        public const int SystemBase = 1;
        public const int SystemMax = 999;

        /// <summary>装备段（1000~1999）：1000 + 槽位号。</summary>
        public const int EquipmentBase = 1000;
        public const int EquipmentMax = 1999;

        /// <summary>宝物段（2000~2999）：2000 + 实例序号。</summary>
        public const int TreasureBase = 2000;
        public const int TreasureMax = 2999;

        /// <summary>测试专用段（3000+）。</summary>
        public const int TestBase = 3000;
    }
}