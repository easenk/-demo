using System;
using UnityEngine;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 解析后的单位视觉数据（批次4C）：烘焙 SO + 视觉定义 + 攻隔 → 规划器与播放器共用的最终口径。
    /// 由 UnitVisualResolver 一次性算好（同一份数据、两边各算各的——#23⑥）：
    /// 规划器读时长/命中百分比（BattleRoot 喂 PlannerTimingTable），播放器读帧序列与缩放朝向。
    /// </summary>
    public sealed class ResolvedUnitVisual
    {
        public Sprite[] Idle, Walk, Attack, Death;   // Attack/Death 已截窗（守约纪律）
        public float IdleFps, WalkFps, AttackFps, DeathFps;
        public float AttackDuration;                 // 截窗后时长（恒 ≤ 攻隔——守约由截窗恢复）
        public float AttackHitPercent;               // 命中帧的窗内百分比
        public float DeathDuration;                  // 尾部窗时长
        public float MoveDuration;                   // 一格移动补间
        public float Scale;                          // 最终世界缩放（BaseHeight/素材身高×体型倍率）
        public float FeetOffset;                     // 脚底对齐偏移（scale=1 世界单位）
        public float BodyHeight;                     // 素材原始身高（scale=1 世界单位）
        public bool SourceFacesRight;                // 素材原画朝向
        public int MaxMana;                          // 我方单位蓝上限（敌方=0 不显示蓝条）
    }

    /// <summary>SO + 定义 + 攻隔 → ResolvedUnitVisual（截窗切片 + 缩放归一）。</summary>
    public static class UnitVisualResolver
    {
        public static ResolvedUnitVisual Resolve(UnitActorClipSet set, UnitVisualDef def,
            float attackInterval, int maxMana)
        {
            if (set == null) throw new ArgumentNullException(nameof(set));
            if (def == null) throw new ArgumentNullException(nameof(def));

            var aw = UnitActorConfig.ResolveAttackWindow(set.Attack.Frames.Length,
                set.Attack.FramesPerSecond, set.Attack.Duration, attackInterval, def.AttackHitPercent);
            var dw = UnitActorConfig.ResolveDeathWindow(set.Death.Frames.Length,
                set.Death.FramesPerSecond, set.Death.Duration, def.DeathMaxDuration);

            return new ResolvedUnitVisual
            {
                Idle = set.Idle.Frames,
                Walk = set.Walk.Frames,
                Attack = Slice(set.Attack.Frames, aw.Start, aw.Count),
                Death = Slice(set.Death.Frames, dw.Start, dw.Count),
                IdleFps = set.Idle.FramesPerSecond,
                WalkFps = set.Walk.FramesPerSecond,
                AttackFps = set.Attack.FramesPerSecond,
                DeathFps = set.Death.FramesPerSecond,
                AttackDuration = aw.Duration,
                AttackHitPercent = aw.HitPercent,
                DeathDuration = dw.Duration,
                MoveDuration = def.MoveDuration,
                Scale = UnitActorConfig.BaseHeight / Mathf.Max(set.BodyHeight, 0.01f) * def.SizeMultiplier,
                FeetOffset = set.FeetOffset,
                BodyHeight = set.BodyHeight,
                SourceFacesRight = def.SourceFacesRight,
                MaxMana = maxMana,
            };
        }

        private static Sprite[] Slice(Sprite[] src, int start, int count)
        {
            if (start == 0 && count == src.Length) return src;   // 全窗零拷贝
            var dst = new Sprite[count];
            Array.Copy(src, start, dst, 0, count);
            return dst;
        }
    }

    /// <summary>
    /// 单位演员（批次4C）：序列帧状态机 + 血条蓝条 + 程序化闪色 + 让格淡出。
    /// 纯 C# 类（非 MonoBehaviour，由 UnitActorView 统一驱动）——一切姿势按 player.Time
    /// 参数化（#23 播放器制式，与 4B2 调试立方体同构）：暂停即姿势冻结、倍速零漂移、
    /// 单次动作播完停帧（素材 clip 全循环标的"末帧诈尸"从机制上不存在）。
    /// 换帧四动作：待机/移动=循环；攻击/施法/死亡=单次播完停帧（死亡末帧永驻到让格淡出）。
    /// 渲染拼装 order 阶梯（照抄 HD-2D 参考）：假影 1 / 身体 2 / 血条 150,151 / 蓝条 152,153。
    /// </summary>
    public sealed class UnitActor
    {
        private enum Act { None, Idle, Walk, Attack, Death }

        public readonly int UnitId;
        public readonly bool PlayerSide;

        // ── 演出骨架（程序拼装，池化复用） ──
        private GameObject _root;
        private Transform _tr;
        private SpriteRenderer _body;       // order 2
        private SpriteRenderer _shadow;     // order 1（黑块染 α0.35——参考工程同款假影）
        private Transform _bars;            // 血蓝条根（spawn 时对齐主相机，战台相机固定）
        private SpriteRenderer _hpBg, _hpFill;     // order 150/151
        private SpriteRenderer _mpBg, _mpFill;     // order 152/153（我方专属）

        // ── 视觉数据（ResolvedUnitVisual 的切片） ──
        private Sprite[] _idleF, _walkF, _atkF, _deathF;
        private float _idleFps, _walkFps, _atkFps, _deathFps;
        private float _atkDur, _deathDur, _atkHold;   // _atkHold：收招定格到回待机的缓冲
        private float _scale, _feet;
        private bool _srcFacesRight;

        // ── 状态（全部随 playhead 推进） ──
        public int Hp, MaxHp, Mana;
        private int _maxMana;
        public bool Dead;
        public bool Removed { get; private set; }
        private bool _removable;
        private float _removeAt;
        private float _flashUntil;
        private Color _flashColor = Color.white;
        private bool _faceRight;

        private Act _act;
        private Sprite[] _frames;
        private float _fps;
        private bool _loop;
        private float _actStart;
        private int _lastFrame = -1;

        private Vector3 _basePos;
        private bool _moving;
        private Vector3 _moveFrom, _moveTo;
        private float _moveStart, _moveDur;

        private static readonly Color FlashDamage = new Color(1f, 0.35f, 0.3f);
        private static readonly Color FlashHeal = new Color(0.45f, 1f, 0.55f);
        private static readonly Color FlashCast = new Color(0.55f, 0.75f, 1f);
        private const float BarWidth = 0.72f, BarHeight = 0.07f, FillInset = 0.02f;

        public UnitActor(GameObjectPool pool, Sprite white, Material bodyMat, Material barMat,
            ResolvedUnitVisual v, int unitId, bool playerSide, int hp, int maxHp, int mana, Vector3 cellWorld,
            Quaternion cameraRotation)
        {
            UnitId = unitId;
            PlayerSide = playerSide;
            Hp = hp; MaxHp = maxHp; Mana = mana; _maxMana = v.MaxMana;

            _idleF = v.Idle; _walkF = v.Walk; _atkF = v.Attack; _deathF = v.Death;
            _idleFps = v.IdleFps; _walkFps = v.WalkFps; _atkFps = v.AttackFps; _deathFps = v.DeathFps;
            _atkDur = v.AttackDuration; _deathDur = v.DeathDuration; _atkHold = 0.2f;
            _scale = v.Scale; _feet = v.FeetOffset * v.Scale; _srcFacesRight = v.SourceFacesRight;
            _faceRight = playerSide;   // 默认面向敌方半场：玩家在左朝右、敌在右朝左
            _basePos = cellWorld;

            // ── 骨架拼装 ──
            _root = pool.Get($"Unit_{unitId}");
            _tr = _root.transform;
            _tr.position = cellWorld;
            _tr.localScale = Vector3.one;
            _tr.rotation = Quaternion.identity;

            _shadow = MakeSprite(_root.transform, "Shadow", white, bodyMat, 1);
            _shadow.color = new Color(0f, 0f, 0f, 0.35f);
            _shadow.transform.localPosition = new Vector3(0f, 0.02f, 0f);
            _shadow.transform.localScale = new Vector3(0.55f * _scale, 0.16f, 1f);

            _body = MakeSprite(_root.transform, "Body", null, bodyMat, 2);
            _body.transform.localPosition = new Vector3(0f, _feet, 0f);
            _body.transform.localScale = new Vector3(_scale, _scale, 1f);

            // 血蓝条（对齐相机的公告板；战台相机固定——spawn 时一次性对齐即可）
            var barsGo = new GameObject("Bars");
            barsGo.transform.SetParent(_tr, false);
            barsGo.transform.localPosition = new Vector3(0f, v.BodyHeight * _scale + 0.12f, 0f);
            barsGo.transform.rotation = cameraRotation;
            _bars = barsGo.transform;

            _hpBg = MakeSprite(_bars, "HpBg", white, barMat, 150);
            _hpBg.color = new Color(0.1f, 0.1f, 0.1f, 0.85f);
            _hpBg.transform.localScale = new Vector3(BarWidth, BarHeight, 1f);
            _hpFill = MakeSprite(_bars, "HpFill", white, barMat, 151);
            _hpFill.color = playerSide ? new Color(0.3f, 0.95f, 0.4f) : new Color(0.95f, 0.35f, 0.3f);
            _hpFill.transform.localPosition = new Vector3(0f, 0f, -0.001f);

            bool hasMana = playerSide && _maxMana > 0;
            if (hasMana)
            {
                _mpBg = MakeSprite(_bars, "MpBg", white, barMat, 152);
                _mpBg.color = new Color(0.1f, 0.1f, 0.15f, 0.85f);
                _mpBg.transform.localPosition = new Vector3(0f, BarHeight + 0.015f, 0f);
                _mpBg.transform.localScale = new Vector3(BarWidth, BarHeight * 0.6f, 1f);
                _mpFill = MakeSprite(_bars, "MpFill", white, barMat, 153);
                _mpFill.color = new Color(0.35f, 0.6f, 1f);
                _mpFill.transform.localPosition = new Vector3(0f, BarHeight + 0.015f, -0.001f);
            }

            PlayIdle(0f);
        }

        // ─────────────────────── 事件入口（节点派发时调用） ───────────────────────

        /// <summary>移动：走帧 + 一格补间（时长=MoveDuration，来自 ResolvedUnitVisual）。</summary>
        public void PlayMove(float t, Vector3 fromWorld, Vector3 toWorld, float duration)
        {
            if (Dead) return;                       // 死亡不接动作（封口语义的兜底）
            _moving = true;
            _moveFrom = fromWorld; _moveTo = toWorld;
            _moveStart = t; _moveDur = Mathf.Max(0.01f, duration);
            if (Mathf.Abs(toWorld.x - fromWorld.x) > 0.01f) _faceRight = toWorld.x > fromWorld.x;
            SetAct(Act.Walk, _walkF, _walkFps, true, t);
        }

        /// <summary>攻击/施法（4C 口径：施法复用攻击窗；施法的区别只在闪色）。</summary>
        public void PlayAttack(float t, bool faceRight, bool cast)
        {
            if (Dead) return;
            _faceRight = faceRight;
            SetAct(Act.Attack, _atkF, _atkFps, false, t);
            if (cast) Flash(FlashCast, t, 0.3f);
        }

        /// <summary>死亡：倒地起=致死包裹送达时刻（规划器排定），末帧永驻。</summary>
        public void PlayDeath(float deathStart)
        {
            Dead = true;
            Hp = 0;
            _moving = false;
            SetAct(Act.Death, _deathF, _deathFps, false, deathStart);
        }

        public void SetHp(int hp) => Hp = hp;
        public void SetMaxHp(int maxHp, int hp) { MaxHp = maxHp; Hp = hp; }
        public void SetMana(int mana) => Mana = mana;
        public void MarkRemovable(float at) { _removable = true; _removeAt = at; }

        public void FlashDamageColor(float t) => Flash(FlashDamage, t, 0.25f);
        public void FlashHealColor(float t) => Flash(FlashHeal, t, 0.25f);

        public Vector3 HeadWorld => _tr.position + new Vector3(0f, _feet + 1.0f * _scale, 0f);
        public Vector3 Position => _tr.position;

        // ─────────────────────── 每帧姿势（按 playhead 参数化） ───────────────────────

        public void Tick(float t)
        {
            if (_root == null || Removed) return;

            // 1) 帧推进（循环取模；单次钳在末帧——播完停帧）
            if (_frames != null && _frames.Length > 0)
            {
                float elapsed = Mathf.Max(0f, t - _actStart);
                int idx = _loop
                    ? (int)(elapsed * _fps) % _frames.Length
                    : Mathf.Min(_frames.Length - 1, (int)(elapsed * _fps));
                if (idx != _lastFrame)
                {
                    _body.sprite = _frames[idx];
                    _lastFrame = idx;
                }

                // 收招定格后回待机（攻击动作整体已装进攻隔——回待机不会晚于下一次出手）
                if (_act == Act.Attack && t > _actStart + _atkDur + _atkHold)
                    SetAct(Act.Idle, _idleF, _idleFps, true, t);
            }

            // 2) 位置：移动补间（播完落位；帧切回待机）
            Vector3 pos = _basePos;
            if (_moving)
            {
                float p = _moveDur <= 0f ? 1f : Mathf.Clamp01((t - _moveStart) / _moveDur);
                pos = Vector3.Lerp(_moveFrom, _moveTo, p);
                if (p >= 1f)
                {
                    _moving = false;
                    _basePos = _moveTo;
                    if (_act == Act.Walk) SetAct(Act.Idle, _idleF, _idleFps, true, t);
                }
            }
            _tr.position = pos;

            // 3) 朝向（flipX 在身体上翻——血条公告板与假影不受镜像影响）
            _body.flipX = _faceRight != _srcFacesRight;

            // 4) 闪色（表演秒账本——暂停即冻结）
            _body.color = t < _flashUntil ? _flashColor : Color.white;

            // 5) 血蓝条（数值事件排水直设——宽度即时兑现）
            UpdateBars();

            // 6) 让格淡出（Remove 时刻起 0.25s 缩没——尸体让位）
            if (_removable)
            {
                float p = Mathf.Clamp01((t - _removeAt) / 0.25f);
                if (p > 0f)
                {
                    _tr.localScale = Vector3.one * (1f - p);
                    if (p >= 1f) Removed = true;
                }
            }
        }

        /// <summary>还池（调用方负责从名单摘除；骨架子件随根复用，状态由下一次构造重写）。</summary>
        public void ReleaseTo(GameObjectPool pool)
        {
            if (_root == null) return;
            _tr.localScale = Vector3.one;
            _tr.rotation = Quaternion.identity;
            _body.sprite = null;
            pool.Release(_root);
            _root = null;
        }

        // ─────────────────────── 内部 ───────────────────────

        private void PlayIdle(float t) => SetAct(Act.Idle, _idleF, _idleFps, true, t);

        private void SetAct(Act act, Sprite[] frames, float fps, bool loop, float startT)
        {
            _act = act;
            _frames = frames;
            _fps = Mathf.Max(1f, fps);
            _loop = loop;
            _actStart = startT;
            _lastFrame = -1;
        }

        private void Flash(Color c, float t, float dur)
        {
            _flashColor = c;
            _flashUntil = t + dur;
        }

        private void UpdateBars()
        {
            if (_bars == null) return;
            float ratio = MaxHp > 0 ? Mathf.Clamp01(Hp / (float)MaxHp) : 0f;
            float fillW = (BarWidth - FillInset * 2f) * ratio;
            _hpFill.transform.localScale = new Vector3(Mathf.Max(fillW, 0.001f), BarHeight - FillInset * 2f, 1f);
            _hpFill.transform.localPosition = new Vector3(-BarWidth / 2f + FillInset + fillW / 2f, 0f, -0.001f);
            if (_mpFill != null)
            {
                float m = _maxMana > 0 ? Mathf.Clamp01(Mana / (float)_maxMana) : 0f;
                float w = (BarWidth - FillInset * 2f) * m;
                float h = BarHeight * 0.6f - FillInset * 2f;
                _mpFill.transform.localScale = new Vector3(Mathf.Max(w, 0.001f), h, 1f);
                _mpFill.transform.localPosition = new Vector3(
                    -BarWidth / 2f + FillInset + w / 2f, BarHeight + 0.015f, -0.001f);
            }
        }

        private static SpriteRenderer MakeSprite(Transform parent, string name, Sprite sprite,
            Material material, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var r = go.AddComponent<SpriteRenderer>();
            r.sprite = sprite;
            r.sharedMaterial = material;
            r.sortingOrder = order;
            return r;
        }
    }
}
