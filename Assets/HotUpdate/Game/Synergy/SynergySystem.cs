using System;
using System.Collections.Generic;

namespace AutoChess.Game
{
    /// <summary>
    /// 羁绊重算管线（幂等全量重算）：
    ///   1) 计数：只数场上玩家单位、同 CharId 只计一次（羁绊数的是"不同棋子"，D2-1）；
    ///   2) 摘干净：全部玩家单位（场上 + 备战席）按族号段两段一刀全摘
    ///      （族 id = 面板修饰器；100+族 id = 战斗内动态修饰器如叠层）——
    ///      档位互斥与降档的保证，也顺带清掉上一场战斗钩子挂的动态修饰器（D2-4）；
    ///      备战席必须参与这一步：刚下场的单位不能把旧加成带回去；
    ///   3) 重挂：只挂激活档这一档（覆盖制，2026-10-19 用户拍板：激活高档时高档增益覆盖低档——高档比低档高，
    ///      互斥取最高不叠加）；且只挂棋盘上的玩家单位——
    ///      本族修饰器挂场上带标签者，全队修饰器挂场上全体；
    ///      备战席不享受（2026-10-27 用户拍板推翻原 D2-2"含备战席"口径）；模板盖族章后入账；
    ///   4) 快照：逐族记 SynergyInfo，与上次比 Count + TierIndex，有变化才广播 SynergiesChangedEvent。
    /// 战斗期间不重算（羁绊开战瞬间冻结——TFT 同款语义）；触发点全部在备战期操作（批次5 接线）+ EnterPrep 兜底自愈。
    /// 钩子注册走 RegisterBattleHooks（开战瞬间调用一次）。
    /// SynergyId 是 enum（封闭集合，P3-5）：跨到 ModSourceId（int 号段）的边界处统一 (int) 强转——
    /// D2-9"羁绊 id 即 SourceId"语义不变。
    /// </summary>
    public sealed class SynergySystem
    {
        /// <summary>族战斗内动态修饰器（叠层等）来源基址：实际 id = 100 + 族 id。与族面板（族 id 本身，1~999 段）两段互不干扰。</summary>
        public const int BattleDynamicSourceBase = 100;

        private readonly CharacterTable _characters;
        private readonly SynergyTable _synergies;
        private readonly EventBus _bus;
        private SynergyInfo[] _lastSnapshot;

        public SynergySystem(CharacterTable characters, SynergyTable synergies, EventBus bus)
        {
            _characters = characters ?? throw new ArgumentNullException(nameof(characters));
            _synergies = synergies ?? throw new ArgumentNullException(nameof(synergies));
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        }

        /// <summary>最近一次重算的快照（首次重算前为 null——表现层判空）。</summary>
        public IReadOnlyList<SynergyInfo> LastSnapshot => _lastSnapshot;

        public void Recalculate(IReadOnlyList<Unit> playerRoster, BoardData board)
        {
            if (playerRoster == null) throw new ArgumentNullException(nameof(playerRoster));
            if (board == null) throw new ArgumentNullException(nameof(board));

            // 1) 计数：仅场上、同 CharId 去重
            var counted = new HashSet<int>();
            var counts = new Dictionary<SynergyId, int>();
            var boardPlayers = new List<Unit>();
            board.CollectUnitsOnBoard(true, boardPlayers);
            foreach (var u in boardPlayers)
            {
                if (!counted.Add(u.CharId)) continue;   // 同 CharId 第二份：不重复计数
                var def = _characters.Get(u.CharId);
                if (def.Id == 0) continue;              // 表外单位：无标签
                foreach (var tag in def.Synergies)
                {
                    counts.TryGetValue(tag, out var n);
                    counts[tag] = n + 1;
                }
            }

            // 2) 摘干净：两段号段一刀全摘（面板段 + 战斗内动态段）
            foreach (var u in playerRoster)
            {
                if (!u.IsPlayerSide) continue;
                foreach (var def in _synergies.All)
                {
                    u.RemoveModifiersBySource((int)def.Id);
                    u.RemoveModifiersBySource(BattleDynamicSourceBase + (int)def.Id);
                }
            }

            // 3) 重挂：只挂激活档（覆盖制），且只挂 boardPlayers（棋盘上的玩家单位）。
            //    playerRoster 仅用于上一步摘账——刚下场者也要被摘到；备战席绝不被挂上（2026-10-27 拍板）。
            var snapshot = new SynergyInfo[_synergies.All.Count];
            for (int i = 0; i < _synergies.All.Count; i++)
            {
                var def = _synergies.All[i];
                counts.TryGetValue(def.Id, out int n);
                int tier = -1;
                for (int t = 0; t < def.Tiers.Length; t++)
                    if (n >= def.Tiers[t].Count) tier = t;   // Count 升序 → 循环走完留下的即最高满足档

                if (tier >= 0)
                {
                    // 覆盖制：只挂激活档——高档增益覆盖低档（高档比低档高，互斥取最高）
                    var td = def.Tiers[tier];
                    if (td.TraitModifiers != null && td.TraitModifiers.Length > 0)
                    {
                        foreach (var u in boardPlayers)
                        {
                            if (!HasTag(u, def.Id)) continue;
                            foreach (var tmpl in td.TraitModifiers)
                                u.AddModifier(Stamp(tmpl, def.Id));
                        }
                    }
                    if (td.TeamModifiers != null && td.TeamModifiers.Length > 0)
                    {
                        foreach (var u in boardPlayers)
                        {
                            foreach (var tmpl in td.TeamModifiers)
                                u.AddModifier(Stamp(tmpl, def.Id));
                        }
                    }
                }

                snapshot[i] = new SynergyInfo
                {
                    SynergyId = def.Id,
                    Count = n,
                    TierIndex = tier,
                    TierDesc = tier >= 0 ? def.Tiers[tier].Desc : null,
                };
            }

            // 4) 快照：有变化才广播（无变化的重算保持沉默——只陈述真发生的事实）
            if (!SnapshotEquals(_lastSnapshot, snapshot))
            {
                _lastSnapshot = snapshot;
                _bus.Publish(new SynergiesChangedEvent((SynergyInfo[])snapshot.Clone()));
            }
            else
            {
                _lastSnapshot = snapshot;   // 内容等价：静默更新引用
            }
        }

        /// <summary>
        /// 开战注册（GameSession.StartBattle 调用；#22④ 钩子型羁绊的唯一入口）：
        /// 取当前快照的激活档（覆盖制：只注册激活档的钩子），以"场上带该标签的己方单位名单"
        /// （开战瞬间冻结）造钩并注册进战斗。返回注册数。战斗期间不再调用（羁绊冻结）。
        /// </summary>
        public int RegisterBattleHooks(CombatSystem combat, BoardData board)
        {
            if (combat == null) throw new ArgumentNullException(nameof(combat));
            if (board == null) throw new ArgumentNullException(nameof(board));
            if (_lastSnapshot == null) return 0;   // 未重算过：无羁绊可注册

            // 场上玩家单位按标签分桶（开战瞬间冻结的成员名单）
            var boardPlayers = new List<Unit>();
            board.CollectUnitsOnBoard(true, boardPlayers);
            var membersBySynergy = new Dictionary<SynergyId, List<Unit>>();
            foreach (var u in boardPlayers)
            {
                var def = _characters.Get(u.CharId);
                if (def.Id == 0) continue;
                foreach (var tag in def.Synergies)
                {
                    if (!membersBySynergy.TryGetValue(tag, out var list))
                    {
                        list = new List<Unit>();
                        membersBySynergy[tag] = list;
                    }
                    list.Add(u);
                }
            }

            int registered = 0;
            foreach (var def in _synergies.All)
            {
                int tierIndex = FindTierIndex(def.Id);   // 从快照读（开战瞬间冻结的激活档）
                if (tierIndex < 0) continue;
                membersBySynergy.TryGetValue(def.Id, out var members);
                var factories = def.Tiers[tierIndex].HookFactories;   // 覆盖制：只注册激活档的钩子组
                if (factories == null) continue;
                foreach (var factory in factories)
                {
                    combat.AddHook(factory(members ?? (IReadOnlyList<Unit>)Array.Empty<Unit>()));
                    registered++;
                }
            }
            return registered;
        }

        // ── 内部 ──

        private bool HasTag(Unit u, SynergyId synergyId)
        {
            var def = _characters.Get(u.CharId);
            if (def.Id == 0) return false;
            foreach (var t in def.Synergies)
                if (t == synergyId) return true;
            return false;
        }

        /// <summary>表内模板盖族章（模板 SourceId 恒 0；族章 = enum 值强转 int 落 ModSourceId 号段，D2-9）。</summary>
        private static StatModifier Stamp(StatModifier tmpl, SynergyId sourceId)
            => new StatModifier(tmpl.Kind, tmpl.Op, tmpl.Value, (int)sourceId);

        private int FindTierIndex(SynergyId synergyId)
        {
            if (_lastSnapshot == null) return -1;
            foreach (var info in _lastSnapshot)
                if (info.SynergyId == synergyId) return info.TierIndex;
            return -1;
        }

        /// <summary>快照等价：逐族比计数与激活档（Desc 由这两者唯一决定，不必比字符串）。</summary>
        private static bool SnapshotEquals(SynergyInfo[] a, SynergyInfo[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i].Count != b[i].Count || a[i].TierIndex != b[i].TierIndex) return false;
            return true;
        }
    }
}
