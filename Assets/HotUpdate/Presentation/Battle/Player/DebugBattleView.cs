using System.Collections.Generic;
using AutoChess.Game;
using UnityEngine;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 调试战斗视图（批次B2）：TimelinePlayer.NodeDispatched 的立方体解读器 + 血条标签/飘字/掉矿。
    /// 立方体占位形态——4C UnitActor（序列帧）整体替换本类，TimelinePlayer/TimelineCursor 零改动。
    ///
    /// 一切姿势按 player.Time 参数化（移动补间/攻击突进/倒地/受击闪色/飘字上升）：
    /// 倍速/暂停对全部演出自动生效（同一旋钮），暂停即姿势冻结、零漂移。
    /// 格→世界映射：场景 GridCell 下 Cell_{行}_{列}（行=Pos.Y 0~5、列=Pos.X 0~7），
    /// 找不到时回退公式（-7+列×2, 0.12, -5+行×2）——战台几何 2026-11-04 实测核对。
    /// </summary>
    public sealed class DebugBattleView : MonoBehaviour
    {
        private TimelinePlayer _player;
        private readonly Dictionary<int, Actor> _actors = new Dictionary<int, Actor>(24);
        private readonly List<Actor> _actorList = new List<Actor>(24);       // 遍历用（顺序=创建序）
        private readonly List<GameObject> _ores = new List<GameObject>(8);
        private readonly List<FloatText> _floats = new List<FloatText>(32);
        private Vector3[] _cellWorld;                                        // [行×8+列]
        private GUIStyle _labelStyle;

        private static readonly Color PlayerColor = new Color(0.25f, 0.5f, 1f, 1f);
        private static readonly Color EnemyColor = new Color(1f, 0.3f, 0.25f, 1f);
        private static readonly Color DeadColor = new Color(0.35f, 0.35f, 0.35f, 1f);

        /// <summary>调试观察口：在场立方体数。</summary>
        public int ActorCount => _actorList.Count;
        /// <summary>调试观察口：已倒地立方体数。</summary>
        public int DeadCount { get; private set; }
        /// <summary>调试观察口：场上掉落矿数。</summary>
        public int OreCount => _ores.Count;

        public void Bind(TimelinePlayer player)
        {
            if (_player != null) _player.NodeDispatched -= OnNode;
            _player = player;
            if (_player != null) _player.NodeDispatched += OnNode;
        }

        private void OnDestroy()
        {
            if (_player != null) _player.NodeDispatched -= OnNode;
            ClearAll();
        }

        // ─────────────────────── 战斗生命周期 ───────────────────────

        /// <summary>按开战帧（tick 0 快照）建立全部立方体；清掉上一场的一切残留。
        /// 秒胜链（快照空）= 无画面——逻辑已终局，直接结算（BattleRoot 处理）。</summary>
        public void BeginBattle(BattleSnapshotSequence snapshots)
        {
            ClearAll();
            if (snapshots == null || snapshots.Count == 0) return;
            BattleSnapshot f0 = snapshots[0];
            for (int i = 0; i < f0.Units.Length; i++)
                CreateActor(f0.Units[i]);
        }

        private void ClearAll()
        {
            foreach (Actor a in _actorList)
                if (a.Body != null) Destroy(a.Body);
            _actors.Clear();
            _actorList.Clear();
            for (int i = 0; i < _ores.Count; i++)
                if (_ores[i] != null) Destroy(_ores[i]);
            _ores.Clear();
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
                    if (TryGet(n.UnitId, out Actor mover))
                    {
                        mover.Action = n;
                        mover.MoveFrom = CellWorld(n.CellX, n.CellY) + Up * ActorHalfHeight;
                        mover.MoveTo = CellWorld(n.ToX, n.ToY) + Up * ActorHalfHeight;
                    }
                    break;

                case PlannedNodeKind.Attack:
                    if (TryGet(n.UnitId, out Actor atk))
                    {
                        atk.Action = n;
                        atk.LungeAmp = n.V0 == 1 ? 0.6f : 0.35f;   // 暴击突进更深
                        atk.LungeDir = LungeDirection(atk, n.TargetId);
                    }
                    break;

                case PlannedNodeKind.Cast:
                    if (TryGet(n.UnitId, out Actor caster))
                    {
                        caster.Action = n;
                        caster.LungeAmp = 0.25f;
                        caster.LungeDir = LungeDirection(caster, n.TargetId);
                        caster.FlashUntil = t + 0.3f;
                        caster.FlashColor = new Color(0.5f, 0.7f, 1f, 1f);   // 施法者蓝闪
                    }
                    break;

                case PlannedNodeKind.Damage:
                    if (TryGet(n.UnitId, out Actor hit))
                    {
                        hit.Hp = n.V1;
                        hit.FlashUntil = t + 0.25f;
                        hit.FlashColor = new Color(1f, 0.15f, 0.1f, 1f);
                        AddFloat(hit.Tr.position + Up * 1.0f, $"-{n.V0}", new Color(1f, 0.3f, 0.2f), t);
                    }
                    break;

                case PlannedNodeKind.Heal:
                    if (TryGet(n.UnitId, out Actor healed))
                    {
                        healed.Hp = n.V1;
                        healed.FlashUntil = t + 0.25f;
                        healed.FlashColor = new Color(0.3f, 1f, 0.4f, 1f);
                        AddFloat(healed.Tr.position + Up * 1.0f, $"+{n.V0}", new Color(0.4f, 1f, 0.5f), t);
                    }
                    break;

                case PlannedNodeKind.Mana:
                    if (TryGet(n.UnitId, out Actor m)) m.Mana = n.V0;
                    break;

                case PlannedNodeKind.MaxHp:
                    if (TryGet(n.UnitId, out Actor mh)) { mh.MaxHp = n.V0; mh.Hp = n.V1; }
                    break;

                case PlannedNodeKind.Death:
                    if (TryGet(n.UnitId, out Actor dead))
                    {
                        dead.Dead = true;
                        dead.Hp = 0;
                        dead.Action = null;                        // 倒地覆盖一切动作姿势
                        dead.DeathStart = n.Start;
                        dead.DeathDur = n.Duration <= 0f ? 0.4f : n.Duration;
                        dead.BasePos = CellWorld(n.CellX, n.CellY) + Up * ActorHalfHeight;
                        DeadCount++;
                    }
                    break;

                case PlannedNodeKind.Remove:
                    if (TryGet(n.UnitId, out Actor rm)) { rm.Removable = true; rm.RemoveAt = n.Start; }
                    break;

                case PlannedNodeKind.OreDrop:
                {
                    // 掉矿随击杀包裹闪现在死亡格：小黄钻（三档=三档大小）
                    GameObject ore = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    ore.name = $"Ore_{n.UnitId}_T{n.V0}";
                    float size = n.V0 >= 3 ? 0.55f : n.V0 == 2 ? 0.42f : 0.3f;
                    ore.transform.localScale = new Vector3(size, size, size);
                    ore.transform.rotation = Quaternion.Euler(45f, 0f, 45f);   // 斜放成钻形
                    ore.transform.position = CellWorld(n.CellX, n.CellY) + Up * (0.35f + size * 0.3f);
                    ore.GetComponent<MeshRenderer>().material.color = new Color(1f, 0.85f, 0.25f, 1f);
                    _ores.Add(ore);
                    break;
                }

                case PlannedNodeKind.BattleEnd:
                    // 终局画面归 BattleRoot 面板陈述；视图无动作
                    break;
            }
        }

        // ─────────────────────── 每帧姿势（按 player.Time 参数化） ───────────────────────

        private void Update()
        {
            if (_player == null || _actorList.Count == 0) return;
            float t = _player.Time;

            for (int i = _actorList.Count - 1; i >= 0; i--)
            {
                Actor a = _actorList[i];
                if (a.Body == null) continue;

                Vector3 pos = a.BasePos;

                // 让格后淡出销毁（尸体让位：Remove 时刻起 0.25s 缩没）
                if (a.Removable && !a.Removed)
                {
                    float p = Mathf.Clamp01((t - a.RemoveAt) / 0.25f);
                    if (p > 0f)
                    {
                        a.Tr.localScale = Vector3.one * (1f - p);
                        if (p >= 1f)
                        {
                            a.Removed = true;
                            Destroy(a.Body);
                            _actors.Remove(a.UnitId);
                            _actorList.RemoveAt(i);
                            continue;
                        }
                    }
                }

                // 动作姿势（移动补间 / 攻击·施法突进）——按 playhead 参数化，倍速/暂停自动跟随
                if (a.Action.HasValue)
                {
                    PlannedNode an = a.Action.Value;
                    float p = an.Duration <= 0f ? 1f : Mathf.Clamp01((t - an.Start) / an.Duration);
                    if (an.Kind == PlannedNodeKind.Move)
                        pos = Vector3.Lerp(a.MoveFrom, a.MoveTo, p);
                    else
                        pos = a.BasePos + a.LungeDir * (Mathf.Sin(Mathf.PI * p) * a.LungeAmp)
                                         + Up * (Mathf.Sin(Mathf.PI * p) * 0.15f);   // 小跳增强可读性
                    if (p >= 1f)
                    {
                        if (an.Kind == PlannedNodeKind.Move) a.BasePos = a.MoveTo;
                        a.Action = null;
                    }
                }

                // 倒地：绕 X 轴前扑 90° + 微沉（起于致死交付、长=倒地时长）
                if (a.Dead)
                {
                    float fp = a.DeathDur <= 0f ? 1f : Mathf.Clamp01((t - a.DeathStart) / a.DeathDur);
                    a.Tr.rotation = Quaternion.Euler(90f * fp, 0f, 0f);
                    pos.y = a.BasePos.y - 0.18f * fp;
                }
                else
                {
                    a.Tr.rotation = Quaternion.identity;
                }

                a.Tr.position = pos;

                // 闪色（表演秒账本——暂停即冻结）
                Color c = a.Dead ? DeadColor : (a.PlayerSide ? PlayerColor : EnemyColor);
                if (t < a.FlashUntil) c = a.FlashColor;
                a.Mat.color = c;
            }
        }

        // ─────────────────────── 屏面：血条标签 + 飘字 ───────────────────────

        private void OnGUI()
        {
            if (_actorList.Count == 0) return;
            if (_labelStyle == null)
                _labelStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 14, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold,
                };

            Camera cam = Camera.main;
            if (cam == null) return;

            // 飘字（世界锚定、随 playhead 上升淡出）
            float t = _player != null ? _player.Time : 0f;
            for (int i = _floats.Count - 1; i >= 0; i--)
            {
                FloatText f = _floats[i];
                float age = (t - f.Spawn) / 1.0f;
                if (age >= 1f) { _floats.RemoveAt(i); continue; }
                Vector3 sp = cam.WorldToScreenPoint(f.World + Up * age * 0.8f);
                if (sp.z <= 0f) continue;
                var rect = new Rect(sp.x - 40f, Screen.height - sp.y, 80f, 22f);
                Color dc = f.Color;
                dc.a = 1f - age;
                _labelStyle.normal.textColor = dc;
                GUI.Label(rect, f.Text, _labelStyle);
            }

            // 血条标签（死亡后灰显 HP0）
            for (int i = 0; i < _actorList.Count; i++)
            {
                Actor a = _actorList[i];
                if (a.Body == null || a.Removed) continue;
                Vector3 sp = cam.WorldToScreenPoint(a.Tr.position + Up * 0.75f);
                if (sp.z <= 0f) continue;
                var rect = new Rect(sp.x - 40f, Screen.height - sp.y, 80f, 22f);
                _labelStyle.normal.textColor = a.Dead
                    ? new Color(0.7f, 0.7f, 0.7f, 0.9f)
                    : (a.PlayerSide ? new Color(0.75f, 0.9f, 1f) : new Color(1f, 0.75f, 0.7f));
                GUI.Label(rect, a.Dead ? "阵亡" : $"HP{a.Hp} 蓝{a.Mana}", _labelStyle);
            }
        }

        // ─────────────────────── 基建 ───────────────────────

        private void CreateActor(SnapshotUnit su)
        {
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = $"Unit_{su.UnitId}";
            body.transform.localScale = new Vector3(0.8f, 1.2f, 0.8f);
            var a = new Actor
            {
                UnitId = su.UnitId,
                PlayerSide = su.IsPlayerSide,
                Body = body,
                Tr = body.transform,
                Mat = body.GetComponent<MeshRenderer>().material,
                Hp = su.Hp,
                MaxHp = su.Hp,
                Mana = su.Mana,
                BasePos = CellWorld(su.X, su.Y) + Up * ActorHalfHeight,
            };
            a.Tr.position = a.BasePos;
            a.Mat.color = a.PlayerSide ? PlayerColor : EnemyColor;
            _actors.Add(a.UnitId, a);
            _actorList.Add(a);
        }

        private bool TryGet(int unitId, out Actor a) => _actors.TryGetValue(unitId, out a);

        private Vector3 LungeDirection(Actor from, int targetId)
        {
            if (targetId != 0 && TryGet(targetId, out Actor tgt))
            {
                Vector3 d = tgt.Tr.position - from.BasePos;
                d.y = 0f;
                if (d.sqrMagnitude > 0.0001f) return d.normalized;
            }
            return from.PlayerSide ? Vector3.right : Vector3.left;   // 找不到目标=朝对向（左右分：玩家在左、敌在右）
        }

        private void AddFloat(Vector3 world, string text, Color color, float spawn)
        {
            _floats.Add(new FloatText { World = world, Text = text, Color = color, Spawn = spawn });
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

        private static readonly Vector3 Up = Vector3.up;
        private const float ActorHalfHeight = 0.6f;

        private sealed class Actor
        {
            public GameObject Body;
            public Transform Tr;
            public Material Mat;
            public int UnitId;
            public bool PlayerSide;
            public int Hp, MaxHp, Mana;
            public bool Dead;
            public float DeathStart, DeathDur;
            public bool Removable, Removed;
            public float RemoveAt;
            public float FlashUntil;
            public Color FlashColor;
            public PlannedNode? Action;             // 当前动作节点（Move/Attack/Cast；null=无动作）
            public Vector3 BasePos, MoveFrom, MoveTo;
            public Vector3 LungeDir;
            public float LungeAmp;
        }

        private struct FloatText
        {
            public Vector3 World;
            public string Text;
            public Color Color;
            public float Spawn;
        }
    }
}
