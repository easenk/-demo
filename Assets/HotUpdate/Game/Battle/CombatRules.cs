namespace AutoChess.Game
{
    /// <summary>
    /// 一场战斗的规则参数（设计文档 5.3）。CombatSystem 规则中立：
    /// 只执行这两个数字，不管它们来自宝物/羁绊/测试。
    /// 多来源的百分比由调用方恒定累加后注入（"恒累加"：多件来源各出各的份）。
    /// 阶段2 演进方向：升级为战斗钩子集合（战斗开始/出手前/出手后/受伤后/死亡时/tick结束），
    /// 这两个数值变成钩子实现之一——外壳换，逻辑不白写。纪律：钩子不得引入新随机源。
    /// </summary>
    public sealed class CombatRules
    {
        /// <summary>吸血：造成实际伤害的 % 回复攻击者（仅玩家侧单位享受）。</summary>
        public float PlayerLifestealPercent;

        /// <summary>击杀回复：击杀瞬间回攻击者有效上限的 %（仅玩家侧单位享受）。</summary>
        public float PlayerKillHealPercent;
    }
}