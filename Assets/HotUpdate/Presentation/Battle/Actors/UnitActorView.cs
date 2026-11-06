using System.Collections.Generic;
using AutoChess.Game;
using UnityEngine;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 战斗视图管理器（批次4C）：TimelinePlayer.NodeDispatched 的序列帧解读器——4B2 调试立方体的
    /// 真形态替换（DebugBattleView 退役，Player/TimelineCursor 零改动承诺兑现）。
    /// 职责：单位演员池化建立与驱动 / 数值事件排水直设（血蓝条即时兑现）/ 世界空间飘字
    /// （TextMesh，order 200）/ 晶矿临时黄钻（4U3 换真视觉）/ 逐 actor 按 playhead 参数化姿势。
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

        /// <summary>绑定播放器与立牌材质（材质由 BattleRoot 从 YooAsset 加载注入——视图不碰资产加载）。</summary>
        public void Bind(TimelinePlayer player, Material billboardMaterial)
        {
            if (_player != null) _player.NodeDispatched -= OnNode;
            _player = player;
            if (_player != null) _player.NodeDispatched += OnNode;

            _bodyMat = billboardMaterial;
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
            if (_player != null) _player.NodeDispatched -= OnNode;
            ClearAll();
            _rigPool?.Dispose();
            _textPool?.Dispose();
        }

        // ─────────────────────── 战斗生命周期 ───────────────────────

        /// <summary>按开战帧（tick 0 快照）建立全部演员；清掉上一场的一切残留。
        /// visuals = BattleRoot 撮合并解析好的 unitId → 视觉数据（缺失=数据事故，跳过并打错误日志）。
        /// 秒胜链（快照空/visuals 空）= 无画面——逻辑已终局，直接结算（BattleRoot 处理）。</summary>
        public void BeginBattle(BattleSnapshotSequence snapshots, IReadOnlyDictionary<int, ResolvedUnitVisual> visuals)
        {
            ClearAll();
            _visuals.Clear();
            if (visuals != null)
                foreach (var kv in visuals) _visuals[kv.Key] = kv.Value;

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
                    break;

                case PlannedNodeKind.Cast:
                    if (TryGet(n.UnitId, out UnitActor caster))
                        caster.PlayAttack(t, FaceTarget(caster, n.TargetId), true);
                    break;

                case PlannedNodeKind.Damage:
                    if (TryGet(n.UnitId, out UnitActor hit))
                    {
                        hit.SetHp(n.V1);
                        hit.FlashDamageColor(t);
                        AddFloat(hit.HeadWorld, $"-{n.V0}", new Color(1f, 0.35f, 0.25f), t);
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
            if (_player == null || _list.Count == 0 && _floats.Count == 0) return;
            float t = _player.Time;

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
