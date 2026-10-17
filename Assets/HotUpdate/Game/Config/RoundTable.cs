using System;
using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// 回合类型（决策 #20：回合节点编排细化，2026-10-17 定案）。
    /// 结算金币制 = 决策 #21① 固定段位（5/6/7）+ 利息 + 连胜三笔分账——
    /// 参考工程"按节点类型发钱"的老制（奖励关 +3 / 精英胜 +5）已退役，
    /// RoundDef 不再有 GoldBonus 字段（奖励关的意义 = 晶矿 + 策略，不再是金币）。
    /// R15 隐藏 Boss 仅设计预留（#20⑤）：当前一律按 14 回合制作，不进任何表与排期。
    /// </summary>
    public enum RoundType
    {
        /// <summary>奖励关（#20①）：特殊怪物（专用怪种、战力较低），死亡 100% 掉晶矿；战败不掉血。</summary>
        Reward,

        /// <summary>普通关（#20③）：填表纪律 = 回合越靠后 PowerLevel 指标越高、怪物越强；战败扣 DamageBase（3→5→7 爬坡）。</summary>
        Normal,

        /// <summary>精英关（#20②）：战力检验门槛——检验玩家阵容战力是否跟上该阶段强度；战败固定 -8 血且收入段位不涨（#21⑨）。</summary>
        Elite,

        /// <summary>最终 Boss（#20④）：战败 = 判负；获胜按限时回血（#21⑨：90 秒内回 35、超时只回 10、封顶 100）。</summary>
        Boss,
    }

    /// <summary>一个回合节点的静态定义（readonly struct——定义一旦入库永不修改）。</summary>
    public readonly struct RoundDef
    {
        /// <summary>回合号（1 起，表内连续递增）。</summary>
        public readonly int Number;

        /// <summary>回合类型。</summary>
        public readonly RoundType Type;

        /// <summary>战败固定扣血（奖励关 0 = 不掉血；普通关 3/5/7 爬坡；精英关 8；Boss 0 = 判负不走数值）。结算细则在 GameSession（批次5）。</summary>
        public readonly int DamageBase;

        public RoundDef(int number, RoundType type, int damageBase)
        {
            Number = number;
            Type = type;
            DamageBase = damageBase;
        }
    }

    /// <summary>
    /// 回合编排表（实例 + 可注入，同 CharacterTable 双源模式：
    /// Default 给测试与兜底，正式内容阶段4 由 SO 资产搬运工重建后注入）。
    /// 14 回合结构照参考工程（R1/5/10 奖励、R6/11 精英、R14 Boss、其余小怪伤害爬坡），
    /// 但结算金币制不抄参考工程（GoldBonus 退役，改 #21① 固定段位制）。
    /// </summary>
    public sealed class RoundTable
    {
        public static RoundTable Default { get; } = new RoundTable(BuildDefaultRounds());

        private readonly RoundDef[] _rounds;

        public RoundTable(IReadOnlyList<RoundDef> rounds)
        {
            if (rounds == null) throw new ArgumentNullException(nameof(rounds));
            if (rounds.Count == 0)
                throw new ArgumentException("回合表不能为空：至少要有一条回合定义", nameof(rounds));

            _rounds = new RoundDef[rounds.Count];
            for (int i = 0; i < rounds.Count; i++)
            {
                // 编号必须从 1 连续递增：回合号 = 数组下标 + 1 是全表的前提，
                // 编号错位 = 建表事故——构造期发现最便宜（fail fast）。
                if (rounds[i].Number != i + 1)
                    throw new ArgumentException(
                        $"回合编号必须从 1 连续递增：第 {i} 条的 Number = {rounds[i].Number}，应为 {i + 1}");
                _rounds[i] = rounds[i];
            }
        }

        /// <summary>回合总数。</summary>
        public int Count => _rounds.Length;

        /// <summary>回合号（1~Count）→ 回合定义。越界由调用方保证（GameSession 状态机管住），自然抛 = fail fast。</summary>
        public RoundDef Get(int round) => _rounds[round - 1];

        private static RoundDef[] BuildDefaultRounds()
        {
            return new RoundDef[]
            {
                new RoundDef(1,  RoundType.Reward, 0),   // 奖励关：特殊低战力怪、必掉矿、败不掉血
                new RoundDef(2,  RoundType.Normal, 3),
                new RoundDef(3,  RoundType.Normal, 3),
                new RoundDef(4,  RoundType.Normal, 3),
                new RoundDef(5,  RoundType.Reward, 0),
                new RoundDef(6,  RoundType.Elite,  8),   // 精英关：战力检验门槛；败 -8 血（#21⑨）
                new RoundDef(7,  RoundType.Normal, 5),
                new RoundDef(8,  RoundType.Normal, 5),
                new RoundDef(9,  RoundType.Normal, 5),
                new RoundDef(10, RoundType.Reward, 0),
                new RoundDef(11, RoundType.Elite,  8),
                new RoundDef(12, RoundType.Normal, 7),
                new RoundDef(13, RoundType.Normal, 7),
                new RoundDef(14, RoundType.Boss,   0),   // Boss 败 = 判负；胜按限时回血（#21⑨）
            };
        }
    }
}
