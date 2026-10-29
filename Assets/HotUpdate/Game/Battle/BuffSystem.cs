using System;
using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// Buff 施加者（战斗内运行时）：谁在给目标挂 Buff——钩子/技能/装备效果都经它统一入口。
    /// 只带两样东西：Buff id + 来源章（SourceId，注销句柄），行为查表执行——
    /// 施加者自己不藏逻辑（表是唯一真源，同 EquipmentInstance 的"身份不藏数据"纪律）。
    ///
    /// 为什么做成独立的轻量 struct：钩子载荷（AttackResult/DamageResult）不方便直接传
    /// 自定义对象（IBattleHook 接口签名固定），BuffApplier 是"我想给目标挂个 Buff"
    /// 这句话的参数化表达——测试与内容侧（技能表/装备/策略词条）都用它。
    /// </summary>
    public readonly struct BuffApplier
    {
        /// <summary>Buff id（查 BuffTable）。</summary>
        public readonly int BuffId;

        /// <summary>来源章（注销句柄：Buff 按来源整批移除用；0 = 无来源，不可整批卸载）。</summary>
        public readonly int SourceId;

        public BuffApplier(int buffId, int sourceId)
        {
            BuffId = buffId;
            SourceId = sourceId;
        }
    }

    /// <summary>
    /// 战斗内 Buff 账本（批次4，阶段3-接口设计 第六章）。
    ///
    /// 职责：给单位挂 Buff / 每 tick 计时结算 / 到期摘除 / 按来源整批移除 / 战斗结束清空。
    /// 生命周期全部战斗内——实例随 CombatSystem 每场新建（StartBattle 重建），绝不跨场。
    ///
    /// 结算顺序（每 tick 末尾，经 IBattleHook.OnTickEnd 分发——恒在胜负结算之前）：
    ///   1) 逐单位逐 Buff 逐层：DoT 结算（死亡即止）→ Hot 结算 → 层数计时递减 → 到 0 摘层；
    ///   2) 层全掉 → 摘 Buff（面板修饰器按章整批摘）。
    ///
    /// 纪律：
    ///  · 不引入新随机源（DoT/Hot 数值表写死）；
    ///  · 修饰器挂账走 ModSourceId.SystemBase 起的"战斗内 Buff"章——
    ///    与羁绊面板段（族 id）、羁绊叠层段（100+族 id）三段互不干扰；
    ///  · DoT 走真实伤害路径（不打折、不可暴击）——灼烧不该被护甲减免；
    ///  · 死亡单位的账目保留到"按来源清"或战斗结束（尸体不再结算，但账可查——战绩/断言用）；
    ///  · Buff 结算在 OnTickEnd 分发里发生 → 与"每 tick 结束钩子恒在胜负结算之前"的既有顺序一致。
    /// </summary>
    public sealed class BuffSystem
    {
        /// <summary>战斗内 Buff 修饰器章基址：实际 id = BuffSystem.BuffSourceBase + Buff id。
        /// 与族面板段（1~999）/ 族叠层段（100+族 id）互不干扰——三段并存于同一单位账本。</summary>
        public const int BuffSourceBase = 500;

        private sealed class Layer
        {
            public int RemainingTicks;   // 本层剩余 tick（每 tick 末递减，到 0 摘层）
        }

        private sealed class BuffState
        {
            public int BuffId;
            public int SourceId;         // 施加者来源章（按来源整批移除的句柄）
            public readonly List<Layer> Layers = new List<Layer>();   // 每层独立计时
            public bool ModifiersApplied; // 面板修饰器是否在账（挂/摘配对）
        }

        private readonly BuffTable _table;
        private readonly EventBus _bus;
        private readonly Dictionary<Unit, List<BuffState>> _byUnit = new Dictionary<Unit, List<BuffState>>();

        public BuffSystem(BuffTable table, EventBus bus)
        {
            _table = table ?? throw new ArgumentNullException(nameof(table));
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        }

        // ─────────────────────── 挂 Buff ───────────────────────

        /// <summary>
        /// 给目标挂一层 Buff：已存在同 id Buff → 叠一层（不超上限，超了静默不叠）并刷新该层计时；
        /// 不存在 → 建 Buff（面板修饰器立刻挂账）。返回实际叠上的层数（0 = 未挂上：表外 id / 目标已死）。
        /// </summary>
        public int Apply(Unit target, in BuffApplier applier)
        {
            if (target == null || !target.IsAlive) return 0;
            BuffDef def = _table.Get(applier.BuffId);
            if (def.Id == 0) return 0;                       // 表外 id：静默拒绝

            if (!_byUnit.TryGetValue(target, out var list))
            {
                list = new List<BuffState>();
                _byUnit[target] = list;
            }

            BuffState state = null;
            for (int i = 0; i < list.Count; i++)
                if (list[i].BuffId == applier.BuffId) { state = list[i]; break; }

            if (state == null)
            {
                state = new BuffState { BuffId = applier.BuffId, SourceId = applier.SourceId };
                list.Add(state);
            }
            else
            {
                state.SourceId = applier.SourceId;           // 后来的施加者接管来源章（最近一次为准）
            }

            if (state.Layers.Count >= def.MaxStacks) return state.Layers.Count;   // 已满：静默不叠

            state.Layers.Add(new Layer { RemainingTicks = def.DurationTicks });
            if (!state.ModifiersApplied && def.Modifiers.Length > 0)
            {
                for (int m = 0; m < def.Modifiers.Length; m++)
                    target.AddModifier(Stamp(def.Modifiers[m], BuffSourceBase + def.Id));
                state.ModifiersApplied = true;
            }
            return state.Layers.Count;
        }

        // ─────────────────────── 每 tick 结算（OnTickEnd 驱动） ───────────────────────

        /// <summary>
        /// 每 tick 末尾结算：逐单位逐 Buff 逐层——DoT（死亡即止）→ Hot → 计时递减 → 到期摘层；
        /// 层全掉 → 摘 Buff（面板修饰器按章整批摘）。幂等、无随机。
        /// 执行序焊死：先伤害后治疗（同一目标同 tick 内先见血后回复，与普攻/施法的时序契约同构）。
        /// </summary>
        public void TickAll()
        {
            foreach (var pair in _byUnit)
            {
                Unit unit = pair.Key;
                List<BuffState> buffs = pair.Value;
                for (int b = buffs.Count - 1; b >= 0; b--)
                {
                    BuffState state = buffs[b];
                    BuffDef def = _table.Get(state.BuffId);
                    if (def.Id == 0) { RemoveAt(buffs, b, unit); continue; }   // 理论不可达（表外 id 挂不进来）

                    // 1) DoT：每层每 tick 一跳（死亡即止——账留尸上但不结算）
                    if (def.DotDamage > 0 && unit.IsAlive)
                        for (int l = 0; l < state.Layers.Count; l++)
                            unit.TakeDamage(def.DotDamage, null);

                    // 2) Hot：每层每 tick 一跳
                    if (def.HotHeal > 0 && unit.IsAlive)
                        for (int l = 0; l < state.Layers.Count; l++)
                            unit.Heal(def.HotHeal);

                    // 3) 计时递减 + 到期摘层
                    for (int l = state.Layers.Count - 1; l >= 0; l--)
                    {
                        state.Layers[l].RemainingTicks--;
                        if (state.Layers[l].RemainingTicks <= 0)
                            state.Layers.RemoveAt(l);
                    }

                    if (state.Layers.Count == 0)
                        RemoveAt(buffs, b, unit);
                }
            }
        }

        // ─────────────────────── 查询（战绩/断言/UI 用，只读） ───────────────────────

        /// <summary>某单位某 Buff 的当前层数（0 = 没挂）。</summary>
        public int GetStacks(Unit unit, int buffId)
        {
            if (unit == null || !_byUnit.TryGetValue(unit, out var list)) return 0;
            for (int i = 0; i < list.Count; i++)
                if (list[i].BuffId == buffId) return list[i].Layers.Count;
            return 0;
        }

        /// <summary>某单位当前挂着的全部 Buff id（快照拷贝，UI/断言用）。</summary>
        public int[] GetBuffIds(Unit unit)
        {
            if (unit == null || !_byUnit.TryGetValue(unit, out var list)) return Array.Empty<int>();
            var ids = new int[list.Count];
            for (int i = 0; i < list.Count; i++) ids[i] = list[i].BuffId;
            return ids;
        }

        // ─────────────────────── 移除 ───────────────────────

        /// <summary>按来源整批移除（来源章 = BuffApplier.SourceId）：该来源挂的全部 Buff 全部层。
        /// 修饰器按"Buff 章"摘（BuffSourceBase + Buff id）——同 Buff 不同施加者也一并摘（按来源整批的语义）。
        /// 返回移除的 Buff 数。sourceId = 0 抛（无来源不是"一个来源"，同 ModSourceId 纪律）。</summary>
        public int RemoveBySource(Unit unit, int sourceId)
        {
            if (sourceId == ModSourceId.None)
                throw new ArgumentException("SourceId=0 表示无来源，不是可卸载的来源段", nameof(sourceId));
            if (unit == null || !_byUnit.TryGetValue(unit, out var list)) return 0;

            int removed = 0;
            for (int b = list.Count - 1; b >= 0; b--)
            {
                if (list[b].SourceId != sourceId) continue;
                RemoveAt(list, b, unit);
                removed++;
            }
            return removed;
        }

        /// <summary>战斗结束清空入口（CombatSystem 场景销毁时调用；实例随场新建，本方法是防御性的显式语义）。</summary>
        public void ClearAll()
        {
            foreach (var pair in _byUnit)
            {
                Unit unit = pair.Key;
                List<BuffState> buffs = pair.Value;
                for (int b = buffs.Count - 1; b >= 0; b--)
                    RemoveAt(buffs, b, unit);
            }
            _byUnit.Clear();
        }

        // ─────────────────────── 内部 ───────────────────────

        private void RemoveAt(List<BuffState> buffs, int index, Unit target)
        {
            BuffState state = buffs[index];
            BuffDef def = _table.Get(state.BuffId);
            if (def.Id != 0 && state.ModifiersApplied && target != null)
                target.RemoveModifiersBySource(BuffSourceBase + def.Id);
            buffs.RemoveAt(index);
        }

        /// <summary>表内模板盖 Buff 章（模板 SourceId 恒 0；章 = BuffSourceBase + Buff id）。</summary>
        private static StatModifier Stamp(StatModifier tmpl, int sourceId)
            => new StatModifier(tmpl.Kind, tmpl.Op, tmpl.Value, sourceId);
    }
}
