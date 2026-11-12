using System;
using System.Collections.Generic;
using AutoChess.Game;
using UnityEngine;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 模板实例池（批次4D）：预制体模板 + 栈式复用——弹体/特效的 GameObject 生死归它。
    /// 与 GameObjectPool 的分工：那是「裸物体池」（UnitActor 程序拼装骨架），本类是
    /// 「预制体克隆池」（素材自带子件与粒子，直接实例化）。首次克隆时摘除素材包自带的
    /// 自走/自毁脚本（ProjectileMover/AutoDestroyPS——2026-11-12 拍板口径：位置归播放器
    /// 插值、寿命归池账本，素材脚本两不沾）；克隆只发生一次，池内复用天然干净。
    /// </summary>
    public sealed class PrefabPool
    {
        private readonly GameObject _template;
        private readonly Stack<GameObject> _stack = new Stack<GameObject>(16);
        private readonly Transform _root;

        /// <summary>模板的原生演出时长（秒，模板时刻）：粒子发射时长+首尾粒子寿命，
        /// 一次测得永久复用——释放判定用它（playhead 域恒定，与速率旋钮无关）。</summary>
        public readonly float Duration;

        public PrefabPool(GameObject template, string name)
        {
            _template = template != null ? template : throw new ArgumentNullException(nameof(template));
            var go = new GameObject($"Pool_{name}");
            go.SetActive(false);                 // 收纳根常隐：闲置件不参与渲染与更新
            _root = go.transform;
            Duration = MeasureDuration(template);
        }

        /// <summary>取一件（池空则克隆模板）。出池即激活——粒子重播由调用方驱动。</summary>
        public GameObject Get()
        {
            GameObject go = _stack.Count > 0 ? _stack.Pop() : CreateClone();
            go.transform.SetParent(null, false);
            go.SetActive(true);
            return go;
        }

        /// <summary>还一件（失活归藏）。</summary>
        public void Release(GameObject go)
        {
            if (go == null) return;
            go.SetActive(false);
            go.transform.SetParent(_root, false);
            _stack.Push(go);
        }

        private GameObject CreateClone()
        {
            var go = UnityEngine.Object.Instantiate(_template);
            go.name = _template.name;
            // 摘除与 #23 播放器制式冲突的素材脚本：自走（位置归播放器插值）与自毁（寿命归池）。
            // UVscroll 等纯视觉脚本不摘——材质流动感是素材本色，与制式无冲突。
            var comps = go.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null) continue;                         // 挂失脚本的槽位是 null（摘要保护）
                string n = c.GetType().Name;
                if (n == "ProjectileMover" || n == "AutoDestroyPS")
                    UnityEngine.Object.Destroy(c);
            }
            return go;
        }

        private static float MeasureDuration(GameObject template)
        {
            float max = 0.4f;
            var systems = template.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                var ps = systems[i];
                if (!ps.gameObject.activeSelf) continue;
                var main = ps.main;
                float d = MaxOf(main.startDelay) + main.duration + MaxOf(main.startLifetime);
                if (d > max) max = d;
            }
            return Mathf.Min(max + 0.25f, 8f);   // 兜底：最长 8 秒必收池（观感上限归 4F 调）
        }

        /// <summary>MinMaxCurve 各模式取最大值（本版引擎无 EvaluateMax——按模式展开）。</summary>
        private static float MaxOf(ParticleSystem.MinMaxCurve c)
        {
            switch (c.mode)
            {
                case ParticleSystemCurveMode.Constant: return c.constant;
                case ParticleSystemCurveMode.TwoConstants: return c.constantMax;
                case ParticleSystemCurveMode.Curve:
                    return c.curveMultiplier * MaxKeyValue(c.curve?.keys);
                case ParticleSystemCurveMode.TwoCurves:
                    return c.curveMultiplier * MaxKeyValue(c.curveMax?.keys);
                default: return 0f;
            }
        }

        private static float MaxKeyValue(Keyframe[] keys)
        {
            if (keys == null || keys.Length == 0) return 0f;
            float m = keys[0].value;
            for (int i = 1; i < keys.Length; i++)
                if (keys[i].value > m) m = keys[i].value;
            return m;
        }
    }

    /// <summary>
    /// 弹道插值纯函数（批次4D）：起点→终点按 playhead 线性补间——launch 前钉在起点、
    /// arrive 后钉在终点，时长=交付-出弹（#23⑥ 弹速的落地形态=推导值，不另编参数）。
    /// 「箭落地才见血」由构造保证：arrive 恒取自配对的交付节点时刻（计划器已排定）。
    /// </summary>
    public static class ProjectileMath
    {
        public static Vector3 PositionAt(Vector3 from, Vector3 to, float launchAt, float arriveAt, float t)
        {
            if (t >= arriveAt) return to;      // 先判落点：零时长（launch==arrive）退化=直接落点
            if (t <= launchAt) return from;
            float span = arriveAt - launchAt;
            float k = span <= 0f ? 1f : (t - launchAt) / span;
            return Vector3.LerpUnclamped(from, to, k);
        }
    }

    /// <summary>计划出的一个视觉演出条目（枚举见 PlannedVisualKind；字段语义随 Kind 变化）。</summary>
    public enum PlannedVisualKind : byte
    {
        /// <summary>弹体：UnitId=出手者、TargetId=目标；FireAt=出弹、ArriveAt=命中（配对交付）。</summary>
        Projectile = 0,
        /// <summary>定点特效：UnitId=锚单位；FireAt=播发时刻；Yaw=绕 Y 旋转（十字纵刀）。</summary>
        Effect = 1,
        /// <summary>全队短爆（王权裁决）：UnitId=施法者（取其阵营全员格各播一份 BuffBurst）。</summary>
        AllAlliesEffect = 2,
    }

    public struct PlannedVisual
    {
        public PlannedVisualKind Kind;
        public float FireAt;
        public float ArriveAt;      // 仅 Projectile 消费；其余条目 = FireAt
        public int UnitId;
        public int TargetId;
        public string Path;
        public float Scale;
        public float Yaw;
    }

    /// <summary>
    /// 弹道与技能演出计划（批次4D）：把时间轴一次性展开成「到点即播」的演出条目序列，
    /// 视图层每帧按 playhead 消费（FireAt ≤ t 即发火）——出弹时刻/锚点爆炸/挥砍出手/
    /// buff 短爆全部预先排定，不再在节点派发时临时推算。
    /// 配对规则与规划器同源（#23③ 同一套语义的视觉侧复读，不重复排时刻、只读已排定的）：
    ///  · 普攻弹体：攻击节点的紧邻下一条 Damage（流序焊死）= 命中；出弹=命中帧%（素材攻击窗锚）；
    ///  · 施法块：Cast 后 Src=施法者的 Damage 连串 = 受击名单（首笔=命中），块尾=施法者清蓝；
    ///  · 纯 buff：无 Damage——短爆排在施法窗走完（Start+Duration），目标按目标规则展开；
    ///  · 钳制：命中早于出弹（病理级联可达）→ 保底飞行 0.05 秒，序不破只是迟到让步。
    /// 锚单位口径：SelfShape（含 18 十字）锚=施法者，其余锚=主目标（NearestEnemyShape 的
    /// 锚点=名单首位=流条目的主目标，13/16 的 3x3/6x6 都以它为中心）。
    /// 纯 C#（UnityEngine 只用 Vector3——EditMode 直测；实体 GameObject 归视图）。
    /// </summary>
    public sealed class ProjectileEffectPlan
    {
        private readonly List<PlannedVisual> _items;
        public IReadOnlyList<PlannedVisual> Items => _items;
        public int Count => _items.Count;

        private ProjectileEffectPlan(List<PlannedVisual> sorted)
        {
            _items = sorted;
        }

        /// <summary>空计划（秒胜链/无演出的兜底）。</summary>
        public static readonly ProjectileEffectPlan Empty =
            new ProjectileEffectPlan(new List<PlannedVisual>(0));

        /// <summary>把时间轴展开成演出计划。参数全部只读；charIdByUnit 缺席 = 该单位无弹体演出。</summary>
        public static ProjectileEffectPlan Build(PlannedTimeline timeline,
            IReadOnlyDictionary<int, ResolvedUnitVisual> visuals,
            IReadOnlyDictionary<int, int> charIdByUnit,
            SkillTable skills)
        {
            if (timeline == null || timeline.Count == 0) return Empty;
            var items = new List<PlannedVisual>(timeline.Count / 4 + 8);

            // Seq 序副本（时间轴本体按 (Start,Seq) 排——配对要按流序逐条看邻居）
            var bySeq = new PlannedNode[timeline.Count];
            for (int i = 0; i < timeline.Count; i++) bySeq[i] = timeline[i];
            Array.Sort(bySeq, (a, b) => a.Seq.CompareTo(b.Seq));

            for (int i = 0; i < bySeq.Length; i++)
            {
                PlannedNode n = bySeq[i];
                if (n.Kind == PlannedNodeKind.Attack)
                {
                    string path = ProjectilePathFor(charIdByUnit, n.UnitId);
                    if (path == null) continue;                       // 近战/怪种无弹体
                    float hitPct = HitPercent(visuals, n.UnitId);
                    float launchAt = n.Start + n.Duration * hitPct;
                    float arriveAt = launchAt + MinFlight;
                    if (i + 1 < bySeq.Length)
                    {
                        PlannedNode next = bySeq[i + 1];
                        if (next.Kind == PlannedNodeKind.Damage
                            && next.TargetId == n.UnitId && next.UnitId == n.TargetId)
                            arriveAt = next.Start;
                    }
                    if (arriveAt < launchAt + MinFlight) arriveAt = launchAt + MinFlight;
                    items.Add(new PlannedVisual
                    {
                        Kind = PlannedVisualKind.Projectile,
                        FireAt = launchAt, ArriveAt = arriveAt,
                        UnitId = n.UnitId, TargetId = n.TargetId,
                        Path = path, Scale = 1f,
                    });
                }
                else if (n.Kind == PlannedNodeKind.Cast)
                {
                    SkillDef skill = skills.Get(n.SkillId);
                    if (skill.Id == 0) continue;
                    if (!EffectVisualConfig.Skills.TryGetValue(n.SkillId, out var v)) continue;
                    float hitPct = HitPercent(visuals, n.UnitId);
                    float launchAt = n.Start + n.Duration * hitPct;

                    if (!skill.HasDamageSegment)
                    {
                        // 纯 buff：短爆排在施法窗走完，目标按规则展开（Self=施法者/友单体=主目标/全体=施法者阵营）
                        if (string.IsNullOrEmpty(v.BuffBurstPath)) continue;
                        float at = n.Start + n.Duration;
                        if (skill.TargetRule == SkillTargetRule.AllAllies)
                            items.Add(new PlannedVisual
                            {
                                Kind = PlannedVisualKind.AllAlliesEffect, FireAt = at,
                                UnitId = n.UnitId, Path = v.BuffBurstPath,
                            });
                        else
                            items.Add(new PlannedVisual
                            {
                                Kind = PlannedVisualKind.Effect, FireAt = at,
                                UnitId = skill.TargetRule == SkillTargetRule.Self ? n.UnitId : n.TargetId,
                                Path = v.BuffBurstPath,
                            });
                        continue;
                    }

                    // 伤害技能：受击名单=块内 Src=施法者的 Damage 连串，块尾=施法者清蓝
                    float arriveAt = launchAt + MinFlight;
                    int j = i + 1;
                    while (j < bySeq.Length)
                    {
                        PlannedNode nx = bySeq[j];
                        if (nx.Kind == PlannedNodeKind.Mana && nx.UnitId == n.UnitId) break;   // 清蓝=块尾
                        if (nx.Kind == PlannedNodeKind.Attack || nx.Kind == PlannedNodeKind.Cast
                            || nx.Kind == PlannedNodeKind.Move) break;                          // 新动作=防御探针
                        if (nx.Kind == PlannedNodeKind.Damage && nx.TargetId == n.UnitId)
                        {
                            if (arriveAt < nx.Start) arriveAt = nx.Start;    // 首笔即命中（钳制口径=max）
                        }
                        else if (nx.Kind == PlannedNodeKind.Damage) break;    // 异源伤害=防御探针
                        j++;
                    }
                    if (arriveAt < launchAt + MinFlight) arriveAt = launchAt + MinFlight;
                    // 演出路由按配置有无（Delivery 只决定弹体）：锚点特效随命中时刻、施法者特效随出手时刻
                    // ——深渊裂击（Area）既有十字双刀又有中心震荡波，靠配置两路并存表达。
                    int anchor = skill.TargetRule == SkillTargetRule.SelfShape ? n.UnitId : n.TargetId;

                    if (skill.Delivery == SkillDelivery.Projectile && !string.IsNullOrEmpty(v.ProjectilePath))
                        items.Add(new PlannedVisual
                        {
                            Kind = PlannedVisualKind.Projectile,
                            FireAt = launchAt, ArriveAt = arriveAt,
                            UnitId = n.UnitId, TargetId = n.TargetId,
                            Path = v.ProjectilePath, Scale = v.ProjectileScale,
                        });

                    if (!string.IsNullOrEmpty(v.AnchorEffectPath))
                        items.Add(new PlannedVisual
                        {
                            Kind = PlannedVisualKind.Effect, FireAt = arriveAt,
                            UnitId = anchor, Path = v.AnchorEffectPath,
                        });

                    if (!string.IsNullOrEmpty(v.CasterEffectPath))
                        items.Add(new PlannedVisual
                        {
                            Kind = PlannedVisualKind.Effect, FireAt = launchAt,
                            UnitId = n.UnitId, Path = v.CasterEffectPath,
                        });
                    if (!string.IsNullOrEmpty(v.CasterEffect2Path))
                        items.Add(new PlannedVisual
                        {
                            Kind = PlannedVisualKind.Effect, FireAt = launchAt,
                            UnitId = n.UnitId, Path = v.CasterEffect2Path,
                            Yaw = v.CasterEffect2Yaw,
                        });
                }
            }

            items.Sort((a, b) => a.FireAt.CompareTo(b.FireAt));
            return new ProjectileEffectPlan(items);
        }

        /// <summary>病理级联兜底：命中早于出弹时保底的最短飞行（秒）。</summary>
        public const float MinFlight = 0.05f;

        private static string ProjectilePathFor(IReadOnlyDictionary<int, int> charIdByUnit, int unitId)
        {
            if (charIdByUnit == null) return null;
            return charIdByUnit.TryGetValue(unitId, out int charId)
                ? EffectVisualConfig.ProjectilePathForChar(charId)
                : null;
        }

        private static float HitPercent(IReadOnlyDictionary<int, ResolvedUnitVisual> visuals, int unitId)
        {
            if (visuals != null && visuals.TryGetValue(unitId, out var v)) return v.AttackHitPercent;
            return 0.5f;   // 缺视觉数据的兜底（4C 的占位口径）
        }
    }
}
