using System;
using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// 羁绊族（决策 #22：六族 v1）。
    ///
    /// 为什么是 enum：六族是决策定死的封闭集合——封闭/开放划线原则（阶段3-接口设计 P3-5，
    /// 与 RoleId 同款）：封闭集合用 enum（编译器拦得住跨类误传），开放内容表用 int。
    /// enum 数值是契约（SynergyTests 锁死）：它同时是 ModSourceId 羁绊号段（1~999）里的
    /// 来源 id（D2-9"羁绊 id 即 SourceId"，无需二次发号）——改值 = 破坏号段纪律与旧存档。
    /// </summary>
    public enum SynergyId
    {
        /// <summary>无羁绊（哨兵，0 不是"一族"）。</summary>
        None = 0,

        /// <summary>蛮兽：攻速/普攻（#22①）。</summary>
        Brute = 1,

        /// <summary>铁壁：双抗/前排。</summary>
        Bulwark = 2,

        /// <summary>秘法：法强/回蓝。</summary>
        Arcane = 3,

        /// <summary>游侠：暴击/物理。</summary>
        Ranger = 4,

        /// <summary>暗影：吸血/击杀。</summary>
        Shadow = 5,

        /// <summary>王庭：全队图腾（唯一"全队"族）。</summary>
        Crown = 6,
    }

    /// <summary>
    /// 羁绊单档定义：Count = 达档所需"不同棋子数"（计数规则见 SynergySystem）。
    /// 效果三载体（#22①）：
    ///  · TraitModifiers   本族面板修饰器 → 只挂场上带该标签的玩家单位；
    ///  · TeamModifiers    全队面板修饰器 → 只挂场上全部玩家单位；
    ///    （备战席一律不享受，2026-10-27 用户拍板推翻原 D2-2"含备战席"口径）
    ///  · HookFactories    战斗钩子工厂数组 → 开战时收到"场上带该标签的己方单位"名单，造本场专用钩子注册。
    /// 覆盖制编表纪律（2026-10-19 用户拍板：激活高档时高档增益覆盖低档——高档比低档高，互斥取最高不叠加）：
    /// 运行时只挂激活档，因此高档条目必须**自带完整效果集**（低档效果在高档条目内重述），覆盖才不留空洞。
    /// 为什么是双数组而不是一个 Scope 字段：#22 的单档效果存在混范围（秘法 6 档 = 全队法强 +25、本族每秒回蓝 +2；
    /// 游侠 6 档 = 全队暴击率 +8%、本族暴伤 +60%）——每档两组各管各的范围，比一个 Scope 字段 + 拆档位更直白。
    /// 表内模板 SourceId 恒 0，挂载时由 SynergySystem 盖族章。
    /// </summary>
    public readonly struct SynergyTierDef
    {
        /// <summary>达档所需"不同棋子数"（同 CharId 只计一次，D2-1）。</summary>
        public readonly int Count;

        /// <summary>档位描述（显示用：如"2 蛮兽：本族攻速 +12%"）。</summary>
        public readonly string Desc;

        /// <summary>本族面板效果模板（null = 无）。</summary>
        public readonly StatModifier[] TraitModifiers;

        /// <summary>全队面板效果模板（null = 无）。</summary>
        public readonly StatModifier[] TeamModifiers;

        /// <summary>钩子效果工厂数组（null = 无）：每个工厂收到开战瞬间场上带该标签的己方单位名单，产出本场专用钩子。</summary>
        public readonly Func<IReadOnlyList<Unit>, IBattleHook>[] HookFactories;

        public SynergyTierDef(int count, string desc,
            StatModifier[] traitModifiers = null,
            StatModifier[] teamModifiers = null,
            Func<IReadOnlyList<Unit>, IBattleHook>[] hookFactories = null)
        {
            Count = count;
            Desc = desc ?? "";
            TraitModifiers = traitModifiers;
            TeamModifiers = teamModifiers;
            HookFactories = hookFactories;
        }

        /// <summary>本档是否有任何效果（构造校验用：空档 = 建表事故）。</summary>
        public bool HasAnyEffect
            => (TraitModifiers != null && TraitModifiers.Length > 0)
            || (TeamModifiers != null && TeamModifiers.Length > 0)
            || (HookFactories != null && HookFactories.Length > 0);
    }

    /// <summary>
    /// 羁绊定义（表的每一行）。Id 直接当修饰器 SourceId 用（1~999 羁绊号段，D2-9 无需二次发号）——
    /// Id 是 enum（SynergyId），跨到 ModSourceId（int 号段）的边界处统一 (int) 强转。
    /// 档位 Count 必须严格升序（构造校验）；激活档 = 满足的最高档（覆盖制：只挂激活档，
    /// 高档条目自带完整效果集——低档效果在高档条目内重述）。
    /// </summary>
    public readonly struct SynergyDef
    {
        public readonly SynergyId Id;
        public readonly string DisplayName;
        public readonly SynergyTierDef[] Tiers;

        public SynergyDef(SynergyId id, string displayName, SynergyTierDef[] tiers)
        {
            Id = id;
            DisplayName = displayName ?? "";
            Tiers = tiers;
        }
    }

    /// <summary>羁绊快照行（事件与只读视图的展示单元）。TierIndex = -1 表示未激活（Desc 为 null）。</summary>
    public struct SynergyInfo
    {
        public SynergyId SynergyId;
        public int Count;
        public int TierIndex;
        public string TierDesc;
    }
}
