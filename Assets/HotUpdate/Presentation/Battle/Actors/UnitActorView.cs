using System.Collections.Generic;
using AutoChess.Game;
using UnityEngine;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 战斗视图管理器（批次4C 建骨架、批次4D 接弹道与技能视觉）：TimelinePlayer.NodeDispatched 的
    /// 序列帧解读器——4B2 调试立方体的真形态替换（DebugBattleView 退役，Player/TimelineCursor
    /// 零改动承诺兑现）。职责：单位演员池化建立与驱动 / 数值事件排水直设（血蓝条即时兑现）/
    /// 世界空间飘字（TextMesh，order 200）/ 晶矿临时黄钻（4U3 换真视觉）/ 逐 actor 按 playhead
    /// 参数化姿势 /【4D】弹体飞行插值与技能特效播发——演出时刻来自 ProjectileEffectPlan
    /// 预排（出弹/爆炸/短爆到点即播），受击者的震荡波与通用命中反馈走节点派发时路由
    /// （施法块受害者由「出手后未终结」账本配对）。特效粒子按速率旋钮驱驰（simulationSpeed
    /// 同步 + 暂停即冻结），寿命按 playhead 域释放——与倍速/暂停语义天然一致。
    /// 【4E】本类同时是排空闸门的画面侧读数源（动作在途/存活特效/在空飘字/未发火条目/在途弹/
    /// 倒地未完六计数）——BattleRoot 每帧收集喂 BattleDrainGate，画面彻底静默才开闸结算。
    /// 视觉键撮合不在本类（流与快照只带单位 id）——BattleRoot 开战时按
    /// 「PlayerBoard 查 CharId + EnemyRoster[i]↔编成表 Spawns[i] 顺序契约」算好喂进来。
    /// </summary>
    public sealed class UnitActorView : MonoBehaviour
    {
        private TimelinePlayer _player;
        private readonly Dictionary<int, UnitActor> _actors = new Dictionary<int, UnitActor>(24);
        private readonly List<UnitActor> _list = new List<UnitActor>(24);       // 遍历用（顺序=创建序）
        private readonly List<GameObject> _ores = new List<GameObject>(8);
        private readonly List<FloatText> _floats = new List<FloatText>(32);
        private readonly Dictionary<int, ResolvedUnitVisual> _visuals = new Dictionary<int, ResolvedUnitVisual>(24);
        private Vector3[] _cellWorld;                                            // [行×8+列]

        // ── 弹道与技能演出（批次4D）──
        private ProjectileEffectPlan _plan = ProjectileEffectPlan.Empty;         // 预排演出（BeginBattle 注入）
        private int _planCursor;                                                  // 已发火到第几条（FireAt 升序）
        private IReadOnlyDictionary<int, int> _charIdByUnit;                      // unitId → 棋子 id（命中反馈分系）
        private readonly Dictionary<int, PendingSkillVisual> _pendingSkill = new Dictionary<int, PendingSkillVisual>(4); // 施法块受害者路由
        private readonly Dictionary<string, GameObject> _effectTemplates = new Dictionary<string, GameObject>(32);     // 路径 → 预制体模板
        private readonly Dictionary<string, PrefabPool> _pools = new Dictionary<string, PrefabPool>(32);                // 路径 → 实例池
        private readonly List<ActiveProjectile> _projectiles = new List<ActiveProjectile>(16);                         // 在途弹（4E 排空闸门的数据口）
        private readonly List<ActiveEffect> _effects = new List<ActiveEffect>(24);
        private float _lastRate = 1f;
        private const float ProjectileHeight = 0.7f;                              // 弹道离地高度（v1 常量，4F 调）

        private GameObjectPool _rigPool;
        private GameObjectPool _textPool;
        private Sprite _white;                                                   // 程序生成 1×1 白块（假影/血条/飘字底）
        private Material _bodyMat;                                                // 立牌材质（BattleRoot 从 YooAsset 注入）
        private Material _barMat;                                                 // 血蓝条材质（不受光的 Sprites/Default）
        private Font _font;                                                       // 飘字字体（内置 LegacyRuntime）
        private Quaternion _camRot;

        /// <summary>调试观察口：在场单位数。</summary>
        public int ActorCount => _list.Count;
        /// <summary>调试观察口：已倒地单位数。</summary>
        public int DeadCount { get; private set; }
        /// <summary>调试观察口：场上掉落矿数。</summary>
        public int OreCount => _ores.Count;
        /// <summary>调试观察口（4D）：在途弹数——4E 排空闸门「在途子弹清零」的数据源。</summary>
        public int InFlightCount => _projectiles.Count;
        /// <summary>调试观察口（4D）：本场弹体/特效累计发火数（验收口径）。</summary>
        public int ProjectileSpawnCount { get; private set; }
        public int EffectSpawnCount { get; private set; }

        // ── 排空闸门读数（批次4E，BattleDrainGate 每帧收集）──
        /// <summary>条件②：动作在途的演员数（移动补间/攻击窗+收招定格未走完）。</summary>
        public int BusyActionCountAt(float t)
        {
            int n = 0;
            for (int i = 0; i < _list.Count; i++)
                if (_list[i].IsActionBusyAt(t)) n++;
            return n;
        }

        /// <summary>条件④：倒地动画未播完的演员数。</summary>
        public int PendingDeathCountAt(float t)
        {
            int n = 0;
            for (int i = 0; i < _list.Count; i++)
                if (_list[i].IsDeathPendingAt(t)) n++;
            return n;
        }

        /// <summary>条件②：存活特效数（playhead 域寿命未走完——含 Makespan 后寿终的晚生特效）。</summary>
        public int ActiveEffectCount => _effects.Count;

        /// <summary>条件②：在空飘字数（0.9 秒上飘淡出未走完）。</summary>
        public int ActiveFloatCount => _floats.Count;

        /// <summary>条件②：已排定但未发火的演出条目数（FireAt 尚未到点——闸门等它出生）。</summary>
        public int PendingVisualFireCount => _plan.Count - _planCursor;

        /// <summary>绑定播放器、立牌材质与特效预制体模板（全部由 BattleRoot 从 YooAsset 加载注入——
        /// 视图不碰资产加载）。effectTemplates = 路径 → 预制体；缺路径=BattleRoot 已打日志，本视图跳过该演出。</summary>
        public void Bind(TimelinePlayer player, Material billboardMaterial,
            IReadOnlyDictionary<string, GameObject> effectTemplates)
        {
            if (_player != null) _player.NodeDispatched -= OnNode;
            _player = player;
            if (_player != null) _player.NodeDispatched += OnNode;

            _bodyMat = billboardMaterial;
            if (effectTemplates != null)
                foreach (var kv in effectTemplates) _effectTemplates[kv.Key] = kv.Value;
            if (_barMat == null)
                _barMat = new Material(Shader.Find("Sprites/Default"));   // 条与飘字不吃光（UI 语义）
            if (_white == null)
            {
                var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                tex.SetPixel(0, 0, Color.white);
                tex.Apply();
                _white = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);   // 1×1 世界单位
            }
            if (_font == null)
                _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (_rigPool == null) _rigPool = new GameObjectPool("UnitRigs");
            if (_textPool == null) _textPool = new GameObjectPool("FloatTexts");
        }

        private void OnDestroy()
        {
            // 场景拆除期（停 Play/场景卸载）禁止再动层级与 Destroy：池根、租借件、矿钻、飘字
            // 全是场景物体，引擎会原生连带销毁；此处若再还池（SetParent）或 Dispose（Destroy），
            // 会与原生拆除竞态——实测每次停 Play 刷一排 t.GetParent() == nullptr 引擎断言。
            // 本组件与场景同寿命，运行期清场一律走 BeginBattle → ClearAll（池件全还、矿钻全销）；
            // 若未来出现「场景还在、组件先销毁」的新流，需先手动清场再销毁。
            if (_player != null) _player.NodeDispatched -= OnNode;
        }

        // ─────────────────────── 战斗生命周期 ───────────────────────

        /// <summary>按开战帧（tick 0 快照）建立全部演员；清掉上一场的一切残留。
        /// visuals = BattleRoot 撮合并解析好的 unitId → 视觉数据（缺失=数据事故，跳过并打错误日志）；
        /// plan = ProjectileEffectPlan 预排的弹道/特效演出（null=秒胜链空演出）；
        /// charIdByUnit = unitId → 棋子 id（通用命中反馈按施放者棋子分物理/魔法两系）。
        /// 秒胜链（快照空/visuals 空）= 无画面——逻辑已终局，直接结算（BattleRoot 处理）。</summary>
        public void BeginBattle(BattleSnapshotSequence snapshots, IReadOnlyDictionary<int, ResolvedUnitVisual> visuals,
            ProjectileEffectPlan plan, IReadOnlyDictionary<int, int> charIdByUnit)
        {
            ClearAll();
            _visuals.Clear();
            if (visuals != null)
                foreach (var kv in visuals) _visuals[kv.Key] = kv.Value;

            _plan = plan ?? ProjectileEffectPlan.Empty;
            _planCursor = 0;
            _charIdByUnit = charIdByUnit;
            _pendingSkill.Clear();
            ProjectileSpawnCount = 0;   // 计数器按场清零（验收口径：每场读数）
            EffectSpawnCount = 0;

            if (snapshots == null || snapshots.Count == 0) return;
            Camera cam = Camera.main;
            _camRot = cam != null ? cam.transform.rotation : Quaternion.identity;

            BattleSnapshot f0 = snapshots[0];
            for (int i = 0; i < f0.Units.Length; i++)
            {
                SnapshotUnit su = f0.Units[i];
                if (!_visuals.TryGetValue(su.UnitId, out ResolvedUnitVisual v))
                {
                    Debug.LogError($"[UnitActorView] 单位 {su.UnitId} 缺视觉数据（撮合/解析事故）——该单位不上台");
                    continue;
                }
                var actor = new UnitActor(_rigPool, _white, _bodyMat, _barMat, v,
                    su.UnitId, su.IsPlayerSide, su.Hp, su.Hp, su.Mana, CellWorld(su.X, su.Y), _camRot);
                _actors.Add(su.UnitId, actor);
                _list.Add(actor);
            }
        }

        private void ClearAll()
        {
            for (int i = 0; i < _list.Count; i++)
                _list[i].ReleaseTo(_rigPool);
            _actors.Clear();
            _list.Clear();
            for (int i = 0; i < _ores.Count; i++)
                if (_ores[i] != null) Destroy(_ores[i]);
            _ores.Clear();
            for (int i = 0; i < _floats.Count; i++)
                _textPool?.Release(_floats[i].Go);
            _floats.Clear();
            // 4D：在途弹与特效全数还池（下一场 BeginBattle 走池复用）
            for (int i = 0; i < _projectiles.Count; i++)
                ReleaseToPool(_projectiles[i].Path, _projectiles[i].Go);
            _projectiles.Clear();
            for (int i = 0; i < _effects.Count; i++)
                ReleaseToPool(_effects[i].Path, _effects[i].Go);
            _effects.Clear();
            _pendingSkill.Clear();
            _planCursor = 0;
            DeadCount = 0;
        }

        // ─────────────────────── 节点解读（字段表见 PlannedTimeline 注释） ───────────────────────

        private void OnNode(PlannedNode n)
        {
            float t = _player.Time;
            switch (n.Kind)
            {
                case PlannedNodeKind.Move:
                    if (TryGet(n.UnitId, out UnitActor mover) && _visuals.TryGetValue(n.UnitId, out var mv))
                        mover.PlayMove(t, CellWorld(n.CellX, n.CellY), CellWorld(n.ToX, n.ToY), mv.MoveDuration);
                    break;

                case PlannedNodeKind.Attack:
                    if (TryGet(n.UnitId, out UnitActor atk))
                        atk.PlayAttack(t, FaceTarget(atk, n.TargetId), false);
                    _pendingSkill.Remove(n.UnitId);   // 出手者的新动作 = 施法块终结（受害者路由账本清位）
                    break;

                case PlannedNodeKind.Cast:
                    if (TryGet(n.UnitId, out UnitActor caster))
                        caster.PlayAttack(t, FaceTarget(caster, n.TargetId), true);
                    // 施法块开账：本块内 Src=施法者的 Damage 到达时按技能表播受击者特效
                    // （块尾=清蓝/新动作，与规划器同一套终结探针——此处由 Attack/Cast 派发清位）
                    if (EffectVisualConfig.Skills.TryGetValue(n.SkillId, out var sv))
                        _pendingSkill[n.UnitId] = new PendingSkillVisual { Visual = sv };
                    break;

                case PlannedNodeKind.Damage:
                    if (TryGet(n.UnitId, out UnitActor hit))
                    {
                        hit.SetHp(n.V1);
                        hit.FlashDamageColor(t);
                        AddFloat(hit.HeadWorld, $"-{n.V0}", new Color(1f, 0.35f, 0.25f), t);
                        SpawnImpact(n, hit, t);   // 4D：技能受击特效或通用命中反馈（按来源分系）
                    }
                    break;

                case PlannedNodeKind.Heal:
                    if (TryGet(n.UnitId, out UnitActor healed))
                    {
                        healed.SetHp(n.V1);
                        healed.FlashHealColor(t);
                        AddFloat(healed.HeadWorld, $"+{n.V0}", new Color(0.45f, 1f, 0.55f), t);
                    }
                    break;

                case PlannedNodeKind.Mana:
                    if (TryGet(n.UnitId, out UnitActor m)) m.SetMana(n.V0);
                    break;

                case PlannedNodeKind.MaxHp:
                    if (TryGet(n.UnitId, out UnitActor mh)) mh.SetMaxHp(n.V0, n.V1);
                    break;

                case PlannedNodeKind.Death:
                    if (TryGet(n.UnitId, out UnitActor dead))
                    {
                        dead.PlayDeath(n.Start);   // 倒地起=致死包裹送达时刻（规划器排定）
                        DeadCount++;
                    }
                    break;

                case PlannedNodeKind.Remove:
                    if (TryGet(n.UnitId, out UnitActor rm)) rm.MarkRemovable(n.Start);
                    break;

                case PlannedNodeKind.OreDrop:
                {
                    // 掉矿随击杀包裹闪现在死亡格：小黄钻（三档=三档大小）——4U3 换真视觉
                    GameObject ore = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    ore.name = $"Ore_{n.UnitId}_T{n.V0}";
                    float size = n.V0 >= 3 ? 0.55f : n.V0 == 2 ? 0.42f : 0.3f;
                    ore.transform.localScale = new Vector3(size, size, size);
                    ore.transform.rotation = Quaternion.Euler(45f, 0f, 45f);
                    ore.transform.position = CellWorld(n.CellX, n.CellY) + new Vector3(0f, 0.35f + size * 0.3f, 0f);
                    ore.GetComponent<MeshRenderer>().material.color = new Color(1f, 0.85f, 0.25f, 1f);
                    _ores.Add(ore);
                    break;
                }

                case PlannedNodeKind.BattleEnd:
                    // 终局画面归 BattleRoot 面板陈述；视图无动作
                    break;
            }
        }

        // ─────────────────────── 每帧推进（按 playhead 参数化） ───────────────────────

        private void Update()
        {
            if (_player == null) return;
            float t = _player.Time;

            // 0) 速率旋钮驱驰（特效粒子跟随倍速/暂停——弹体与演员天然按 playhead 参数化不受影响）
            float rate = _player.Rate;
            if (!Mathf.Approximately(rate, _lastRate))
            {
                _lastRate = rate;
                for (int i = 0; i < _effects.Count; i++) SyncRate(_effects[i].Go, rate);
            }

            // 1) 预排演出发火：FireAt 到点即播（弹体/爆炸/挥砍/buff 短爆——时刻全由计划预排）
            while (_planCursor < _plan.Count && _plan.Items[_planCursor].FireAt <= t)
            {
                FireItem(_plan.Items[_planCursor], t, rate);
                _planCursor++;
            }

            // 2) 在途弹：格间插值（命中时刻=配对交付，箭落地才见血）
            for (int i = _projectiles.Count - 1; i >= 0; i--)
            {
                ActiveProjectile p = _projectiles[i];
                if (t >= p.ArriveAt)
                {
                    ReleaseToPool(p.Path, p.Go);
                    _projectiles.RemoveAt(i);
                    continue;
                }
                p.Go.transform.position = ProjectileMath.PositionAt(p.From, p.To, p.LaunchAt, p.ArriveAt, t);
            }

            // 3) 特效寿命（playhead 域释放——暂停即冻结、倍速不缩水）
            for (int i = _effects.Count - 1; i >= 0; i--)
            {
                ActiveEffect e = _effects[i];
                if (t - e.SpawnAt >= e.Duration)
                {
                    ReleaseToPool(e.Path, e.Go);
                    _effects.RemoveAt(i);
                }
            }

            if (_list.Count == 0 && _floats.Count == 0) return;

            for (int i = _list.Count - 1; i >= 0; i--)
            {
                UnitActor a = _list[i];
                a.Tick(t);
                if (a.Removed)
                {
                    _actors.Remove(a.UnitId);
                    _list.RemoveAt(i);
                    a.ReleaseTo(_rigPool);
                }
            }

            // 飘字（世界锚定、随 playhead 上升淡出）
            for (int i = _floats.Count - 1; i >= 0; i--)
            {
                FloatText f = _floats[i];
                float age = (t - f.Spawn) / 0.9f;
                if (age >= 1f)
                {
                    _textPool.Release(f.Go);
                    _floats.RemoveAt(i);
                    continue;
                }
                Transform tr = f.Go.transform;
                tr.position = f.World + new Vector3(0f, age * 0.8f, 0f);
                Color c = f.Color;
                c.a = 1f - age;
                f.Tm.color = c;
            }
        }

        // ─────────────────────── 弹道与技能演出（批次4D） ───────────────────────

        private void FireItem(PlannedVisual item, float t, float rate)
        {
            switch (item.Kind)
            {
                case PlannedVisualKind.Projectile:
                {
                    if (!TryGet(item.UnitId, out UnitActor from) || !TryGet(item.TargetId, out UnitActor to))
                        return;   // 撮合缺位的防御：无起点或无目标=不出弹（BattleRoot 已点名日志）
                    GameObject go = SpawnFromPool(item.Path, rate);
                    if (go == null) return;
                    Vector3 a = from.Position + new Vector3(0f, ProjectileHeight, 0f);
                    Vector3 b = to.Position + new Vector3(0f, ProjectileHeight, 0f);
                    go.transform.position = a;
                    go.transform.localScale = Vector3.one * item.Scale;
                    _projectiles.Add(new ActiveProjectile
                    {
                        Go = go, Path = item.Path, From = a, To = b,
                        LaunchAt = item.FireAt, ArriveAt = item.ArriveAt,
                    });
                    ProjectileSpawnCount++;
                    break;
                }
                case PlannedVisualKind.Effect:
                {
                    if (!TryGet(item.UnitId, out UnitActor anchor)) return;
                    SpawnEffectAt(item.Path, anchor.Position, item.Yaw, t, rate);
                    break;
                }
                case PlannedVisualKind.AllAlliesEffect:
                {
                    bool side = TryGet(item.UnitId, out UnitActor caster) && caster.PlayerSide;
                    for (int i = 0; i < _list.Count; i++)
                        if (_list[i].PlayerSide == side && !_list[i].Dead)
                            SpawnEffectAt(item.Path, _list[i].Position, 0f, t, rate);
                    break;
                }
            }
        }

        private void SpawnImpact(PlannedNode n, UnitActor victim, float t)
        {
            if (_pendingSkill.TryGetValue(n.TargetId, out PendingSkillVisual pending))
            {
                // 施法块受害者：技能专属受击特效（震荡波）+ 首受击者附加（淬毒毒雾）
                if (!string.IsNullOrEmpty(pending.Visual.VictimEffectPath))
                    SpawnEffectAt(pending.Visual.VictimEffectPath, victim.Position, 0f, t, _player.Rate);
                if (!string.IsNullOrEmpty(pending.Visual.VictimExtraPath) && !pending.ExtraUsed)
                {
                    SpawnEffectAt(pending.Visual.VictimExtraPath, victim.Position, 0f, t, _player.Rate);
                    pending.ExtraUsed = true;
                    _pendingSkill[n.TargetId] = pending;   // struct 账本回写
                }
                return;
            }
            // 通用命中反馈：按施放者棋子分系（术士=魔法紫电 / 其余与无棋子来源=物理枪械）
            int charId = _charIdByUnit != null && _charIdByUnit.TryGetValue(n.TargetId, out int cid) ? cid : 0;
            string path = EffectVisualConfig.IsMagicChar(charId)
                ? EffectVisualConfig.MagicImpactPath
                : EffectVisualConfig.PhysicalImpactPath;
            SpawnEffectAt(path, victim.Position, 0f, t, _player.Rate);
        }

        private void SpawnEffectAt(string path, Vector3 world, float yaw, float t, float rate)
        {
            GameObject go = SpawnFromPool(path, rate);
            if (go == null) return;
            go.transform.position = world;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            _effects.Add(new ActiveEffect
            {
                Go = go, Path = path, SpawnAt = t,
                Duration = _pools[path].Duration,
            });
            EffectSpawnCount++;
        }

        /// <summary>出池（含粒子重播与速率应用）；模板缺失（BattleRoot 已打日志）返回 null。</summary>
        private GameObject SpawnFromPool(string path, float rate)
        {
            if (string.IsNullOrEmpty(path) || !_effectTemplates.TryGetValue(path, out GameObject template))
                return null;
            if (!_pools.TryGetValue(path, out PrefabPool pool))
            {
                pool = new PrefabPool(template, template.name);
                _pools[path] = pool;
            }
            GameObject go = pool.Get();
            RestartPlayback(go, rate);
            return go;
        }

        private void ReleaseToPool(string path, GameObject go)
        {
            if (_pools.TryGetValue(path, out PrefabPool pool)) pool.Release(go);
            else if (go != null) Destroy(go);
        }

        /// <summary>粒子驱驰：速率旋钮 → simulationSpeed 同步 + 暂停/续播（重播=Stop 清屏再 Play）。</summary>
        private static void RestartPlayback(GameObject go, float rate)
        {
            var systems = go.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                var ps = systems[i];
                if (!ps.gameObject.activeSelf) continue;
                var main = ps.main;
                if (rate <= 0f)
                {
                    main.simulationSpeed = 1f;
                    ps.Pause(true);
                }
                else
                {
                    main.simulationSpeed = rate;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Play(true);
                }
            }
        }

        /// <summary>速率变化（不打断进度）：暂停即冻结粒子、恢复时按新速率续播。</summary>
        private static void SyncRate(GameObject go, float rate)
        {
            var systems = go.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                var ps = systems[i];
                if (!ps.gameObject.activeSelf) continue;
                var main = ps.main;
                if (rate <= 0f)
                {
                    ps.Pause(true);
                }
                else
                {
                    main.simulationSpeed = rate;
                    if (!ps.isPaused && !ps.isPlaying) ps.Play(true);
                    else if (ps.isPaused) ps.Play(true);
                }
            }
        }

        private struct ActiveProjectile
        {
            public GameObject Go;
            public string Path;
            public Vector3 From, To;
            public float LaunchAt, ArriveAt;
        }

        private struct ActiveEffect
        {
            public GameObject Go;
            public string Path;
            public float SpawnAt, Duration;
        }

        private struct PendingSkillVisual
        {
            public SkillVisual Visual;
            public bool ExtraUsed;
        }

        // ─────────────────────── 基建 ───────────────────────

        private bool TryGet(int unitId, out UnitActor a) => _actors.TryGetValue(unitId, out a);

        /// <summary>面向目标（找不到目标=朝对向——左右分：玩家在左朝右、敌在右朝左）。</summary>
        private bool FaceTarget(UnitActor from, int targetId)
        {
            if (targetId != 0 && TryGet(targetId, out UnitActor tgt))
                return tgt.Position.x >= from.Position.x;
            return from.PlayerSide;
        }

        private Vector3 CellWorld(byte x, byte y)
        {
            if (_cellWorld == null) BuildCellMap();
            return _cellWorld[y * GameConst.BoardWidth + x];
        }

        private void BuildCellMap()
        {
            int w = GameConst.BoardWidth, h = GameConst.BoardHeight;
            _cellWorld = new Vector3[w * h];
            for (int row = 0; row < h; row++)
                for (int col = 0; col < w; col++)
                {
                    GameObject cell = GameObject.Find($"Cell_{row}_{col}");
                    _cellWorld[row * w + col] = cell != null
                        ? cell.transform.position
                        : new Vector3(-7f + col * 2f, 0.12f, -5f + row * 2f);   // 回退公式（战台几何实测）
                }
        }

        private void AddFloat(Vector3 world, string text, Color color, float spawn)
        {
            GameObject go = _textPool.Get($"Float_{_floats.Count}");
            Transform tr = go.transform;
            tr.position = world;
            tr.rotation = _camRot;
            var tm = go.GetComponent<TextMesh>();
            if (tm == null)
            {
                tm = go.AddComponent<TextMesh>();
                tm.font = _font;
                tm.fontSize = 40;
                tm.characterSize = 0.085f;
                tm.anchor = TextAnchor.MiddleCenter;
                tm.alignment = TextAlignment.Center;
                go.GetComponent<MeshRenderer>().sortingOrder = 200;
                go.GetComponent<MeshRenderer>().sharedMaterial = _font.material;
            }
            tm.text = text;
            tm.color = color;
            _floats.Add(new FloatText { Go = go, Tm = tm, World = world, Color = color, Spawn = spawn });
        }

        private struct FloatText
        {
            public GameObject Go;
            public TextMesh Tm;
            public Vector3 World;
            public Color Color;
            public float Spawn;
        }
    }
}
