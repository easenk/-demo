using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutoChess.Game;
using AutoChess.Runtime;
using UnityEngine;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 战台组合根（#23④，批次B2 立骨架、批次4C 接序列帧真视觉）：运行时串联 会话 → 视觉撮合 →
    /// 预演（后台线程）→ 规划 → 播放 全链。
    /// 调试驱动形态：整局会话 = L8 满级 + 注资买满阵容（预演基准同款构建，内容随种子）；
    /// 面板逐场点「开战」——预演后台 Task → await 主线程续接（预演期间主线程不碰会话，
    /// #23① 纪律）→ 规划器排时间轴 → TimelinePlayer 播放 → 播完 Finished 即结算
    /// （SettleBattle）——这是 4E 正式排空闸门接入前的调试口径，闸门四条件/保险丝归 4E。
    /// 场景零脚本铁律：本组件由 SceneFlowDriver 在 Main 激活后运行时装配。
    /// </summary>
    public sealed class BattleRoot : MonoBehaviour
    {
        // 布阵固定格位（玩家半场 4x6；近战贴中线 X3 列、远程 X0~2 纵向展开——左右分 2026-11-04 拍板，预演基准/平衡压测同款格表）
        private static readonly Pos[] FrontCells =
        {
            new Pos(3, 2), new Pos(3, 3), new Pos(3, 1), new Pos(3, 4), new Pos(3, 0), new Pos(3, 5),
            new Pos(2, 0), new Pos(2, 5),
        };
        private static readonly Pos[] BackCells =
        {
            new Pos(2, 2), new Pos(2, 3), new Pos(1, 2), new Pos(1, 3),
            new Pos(2, 1), new Pos(2, 4), new Pos(1, 1), new Pos(1, 4),
            new Pos(0, 2), new Pos(0, 3),
        };

        private GameSession _session;
        private BattlePreviewer _previewer;
        private TimelinePlayer _player;
        private UnitActorView _view;
        private uint _seed = 20261104u;
        private bool _busy;
        private bool _lastWon;
        private readonly List<string> _log = new List<string>(16);

        // ── 视觉资产缓存（批次4C）：30 个烘焙 SO + 立牌材质，Start 时一次性预载（会话级常驻） ──
        private readonly Dictionary<int, UnitActorClipSet> _clipSets = new Dictionary<int, UnitActorClipSet>(32);
        private Material _billboardMat;

        // ── 调试观察口（验收脚本只读） ──
        public bool DebugBusy => _busy;
        public int DebugRound => _session != null ? _session.Round : 0;
        public GamePhase DebugPhase => _session != null ? _session.Phase : GamePhase.PreRun;
        public bool DebugRunOver => _session != null && _session.RunOver;

        private void Start()
        {
            var playerGo = new GameObject("TimelinePlayer");
            playerGo.transform.SetParent(transform, false);
            _player = playerGo.AddComponent<TimelinePlayer>();

            var viewGo = new GameObject("UnitActorView");
            viewGo.transform.SetParent(transform, false);
            _view = viewGo.AddComponent<UnitActorView>();

            _player.Finished += OnPlaybackFinished;

            LoadVisualAssets();          // 30 个烘焙 SO + 立牌材质（缺失=烘焙没跑，日志点名）
            _view.Bind(_player, _billboardMat);

            NewRun();
        }

        // ─────────────────────── 视觉资产（批次4C：烘焙 SO 预载 + 撮合解析） ───────────────────────

        /// <summary>预载全部视觉资产：18 角色 + 12 怪种 SO、立牌材质（路径真源 = UnitActorConfig）。</summary>
        private void LoadVisualAssets()
        {
            for (int charId = 1; charId <= 18; charId++) LoadClipSet(charId);
            for (int enemy = 1; enemy <= 12; enemy++) LoadClipSet(UnitActorConfig.EnemyKeyBase + enemy);

            _billboardMat = AssetService.LoadAsset("Assets/Res/Art/UnitActorClips/M_Billboard") as Material;
            if (_billboardMat == null)
                Log("立牌材质缺失：Res/Art/UnitActorClips/M_Billboard——先跑菜单「工具/表现层序列帧烘焙」（会自动补建）");
        }

        private void LoadClipSet(int visualKey)
        {
            UnitVisualDef def = UnitActorConfig.Get(visualKey);
            if (def == null) { Log($"视觉键 {visualKey} 无配置——跳过"); return; }
            var set = AssetService.LoadAsset(def.ClipSetPath) as UnitActorClipSet;
            if (set == null) { Log($"烘焙 SO 缺失：{def.ClipSetPath}——先跑菜单「工具/表现层序列帧烘焙」"); return; }
            _clipSets[visualKey] = set;
        }

        /// <summary>开战撮合与解析（主线程、预演启动前——#23① 纪律：后台预演期间主线程不再碰会话）：
        /// 我方从 PlayerBoard 查 CharId；敌方按「EnemyRoster[i] ↔ 编成表 Spawns[i]」顺序契约
        /// （SpawnEncounter 表序生成、无条件入名册）拿怪种名换视觉键。逻辑层零改动、流/快照零改动。</summary>
        private Dictionary<int, ResolvedUnitVisual> BuildVisuals()
        {
            var visuals = new Dictionary<int, ResolvedUnitVisual>(24);
            var chars = GameTables.Default.Characters;

            foreach (Unit u in _session.PlayerBoard)
            {
                if (u.CharId < 1 || u.CharId > 18) continue;
                if (!ResolveOne(visuals, u.Id, u.CharId, chars.Get(u.CharId).BaseStats.AttackInterval,
                        chars.Get(u.CharId).BaseStats.MaxMana))
                    Log($"我方单位 {u.Id}（棋子 {u.CharId}）视觉缺失——该单位不上台");
            }

            Encounter encounter = GameTables.Default.Enemies.Get(_session.Round);
            if (!encounter.IsEmpty)
            {
                var roster = _session.EnemyRoster;
                int count = Math.Min(roster.Count, encounter.Spawns.Length);
                for (int i = 0; i < count; i++)
                {
                    Unit u = roster[i];
                    EnemySpawn spawn = encounter.Spawns[i];   // 顺序契约：Roster[i] ↔ Spawns[i]
                    int key = UnitActorConfig.EnemyKeyFromName(spawn.DisplayName);
                    if (key == 0 || !ResolveOne(visuals, u.Id, key, spawn.Stats.AttackInterval, 0))
                        Log($"敌方单位 {u.Id}「{spawn.DisplayName}」视觉缺失——该单位不上台");
                }
            }
            return visuals;
        }

        private bool ResolveOne(Dictionary<int, ResolvedUnitVisual> visuals, int unitId, int visualKey,
            float attackInterval, int maxMana)
        {
            if (!_clipSets.TryGetValue(visualKey, out UnitActorClipSet set)) return false;
            UnitVisualDef def = UnitActorConfig.Get(visualKey);
            if (def == null) return false;
            visuals[unitId] = UnitVisualResolver.Resolve(set, def, attackInterval, maxMana);
            return true;
        }

        private void OnDestroy()
        {
            if (_player != null) _player.Finished -= OnPlaybackFinished;
        }

        // ─────────────────────── 对局与会话构建 ───────────────────────

        private void NewRun()
        {
            _session = new GameSession(new EventBus(), _seed, GameTables.Default);
            if (!_session.StartRun()) { Log("StartRun 被拒——调试对局建立失败"); return; }

            // L8 满级 + 注资 + 买满备战席（架上任意有货就买——内容随种子，与预演基准同款）
            _session.Eco.AddGold(300);
            while (_session.Eco.Level < 8)
                if (!_session.BuyXp()) break;
            int guard = 0;
            while (_session.Bench.Count < GameSession.BenchCapacity && guard++ < 100)
            {
                int slot = -1;
                for (int i = 0; i < ShopSystem.Slots; i++)
                    if (_session.Shop.Offers[i].Id != 0) { slot = i; break; }
                if (slot < 0) { if (!_session.RefreshShop()) break; continue; }
                if (!_session.Buy(slot)) break;
            }
            Log($"调试对局就绪：种子 {_seed} · L{_session.Eco.Level} · 备战席 {_session.Bench.Count} 人 —— 点「开战」逐场播放");
        }

        // ─────────────────────── 驱动链（面板入口） ───────────────────────

        /// <summary>开战并播放下一场（面板按钮与验收脚本的统一入口）。</summary>
        public void PlayNext()
        {
            if (_busy) { Log("预演/播放中——不可重入"); return; }
            if (_session == null || _session.RunOver) { Log("对局已结束——点「重开一局」"); return; }
            if (_session.Phase == GamePhase.Settlement)
            {
                if (!_session.StartPrep()) { Log("StartPrep 被拒"); return; }
            }
            if (_session.Phase != GamePhase.Prep) { Log($"当前 {_session.Phase} 不可开战"); return; }

            ConsumeGates();
            FillBoard();
            RunPreviewAndPlay();
        }

        private async void RunPreviewAndPlay()
        {
            _busy = true;
            _previewer = new BattlePreviewer();
            if (!_previewer.Start(_session))
            {
                _busy = false;
                Log($"R{_session.Round} 开战被门禁拒（矿未捡完/策略未选完）");
                return;
            }

            // 撮合+解析（主线程、预演启动前——#23① 纪律的另一半：之后的预演期间不碰会话）
            Dictionary<int, ResolvedUnitVisual> visuals = BuildVisuals();

            Log($"R{_session.Round} 预演中（后台线程）…");
            BattlePreviewResult result;
            try
            {
                result = await _previewer.RunToEndInBackground();
            }
            catch (Exception e)
            {
                _busy = false;
                Log($"预演异常: {e.Message}");
                return;
            }

            // 主线程续接（Unity 同步上下文）——预演期间主线程未触碰会话（#23① 纪律）
            PlannedTimeline timeline = new BattlePlanner().Plan(result.Stream, BuildTimingTable(result, visuals));
            Log($"预演完成：{result.TickCount} 步 · 流 {result.Stream.Count} 条 → 时间轴 {timeline.Count} 节点 · Makespan {timeline.Makespan:F1}s");

            if (timeline.Count == 0)
            {
                // 秒胜链：战斗根本没发生——清台后直接结算
                _view.BeginBattle(null, null);
                SettleAfterPlayback();
                return;
            }

            _view.BeginBattle(result.Snapshots, visuals);   // 开战帧建立演员（序列帧真形态）
            _player.Play(timeline);
            Log($"R{_session.Round} 开始播放（速率旋钮随时可切）");
        }

        private void OnPlaybackFinished()
        {
            SettleAfterPlayback();
        }

        /// <summary>4E 正式闸门接入前的调试口径：播放完成即开闸结算。</summary>
        private void SettleAfterPlayback()
        {
            if (_session != null && _session.Phase == GamePhase.Battle && _session.Combat.IsOver)
            {
                _lastWon = _session.Combat.PlayerWon;
                bool ok = _session.SettleBattle();
                Log(ok
                    ? $"R{_session.Round} 结算完成：{(_lastWon ? "胜" : "败")} → {_session.Phase}"
                    : "结算被拒");
                if (_session.RunOver) Log("对局终局——点「重开一局」换种子再来");
            }
            _previewer?.Release();   // 播完即释放（#23②）——流与快照容量留给下一场
            _previewer = null;
            _busy = false;
        }

        private PlannerTimingTable BuildTimingTable(BattlePreviewResult result, Dictionary<int, ResolvedUnitVisual> visuals)
        {
            // 兜底：缺视觉数据的单位沿用占位时刻（规划全函数——宁可占位慢，不可半路炸）
            var fallback = new UnitTiming
            {
                AttackDuration = 0.45f, AttackHitPercent = 0.5f,
                CastDuration = 0.60f, CastHitPercent = 0.5f,
                MoveDuration = 0.30f, DeathDuration = 0.40f,
            };
            var table = new PlannerTimingTable(fallback);
            if (result.Snapshots == null || result.Snapshots.Count == 0) return table;

            // 真数据逐单位喂（#23⑥ 同一份数据两边各算各的：这里的时长=播放器换帧的同一窗）
            SnapshotUnit[] units = result.Snapshots[0].Units;
            for (int i = 0; i < units.Length; i++)
            {
                int id = units[i].UnitId;
                if (!visuals.TryGetValue(id, out ResolvedUnitVisual v)) continue;
                table.Set(id, new UnitTiming
                {
                    AttackDuration = v.AttackDuration,
                    AttackHitPercent = v.AttackHitPercent,
                    CastDuration = v.AttackDuration,        // 4C 口径：施法复用攻击窗（素材无独立施法动作）
                    CastHitPercent = v.AttackHitPercent,
                    MoveDuration = v.MoveDuration,
                    DeathDuration = v.DeathDuration,
                });
            }
            return table;
        }

        // ─────────────────────── 备战收尾（预演基准同款） ───────────────────────

        private void ConsumeGates()
        {
            int guard = 0;
            while (_session.OresOnGround.Count > 0)
            {
                if (++guard > 50) { Log("捡矿循环异常长，已防御性跳出"); break; }
                if (!_session.PickOre(_session.OresOnGround[0].Id)) { Log("捡矿被拒，已跳出"); break; }
            }
            if (_session.HasPolicyChoice)
                _session.PickPolicy(0);   // 调试固定选第一格
        }

        private void FillBoard()
        {
            while (_session.PlayerBoard.Count < _session.Eco.Level && _session.Bench.Count > 0)
            {
                Unit u = _session.Bench[0];
                bool ranged = GameTables.Default.Characters.Get(u.CharId).BaseStats.AttackRange > 1;
                bool placed = false;
                foreach (Pos cell in (ranged ? BackCells : FrontCells))
                    if (_session.PlaceOnBoard(u.Id, cell)) { placed = true; break; }
                if (!placed)
                    foreach (Pos cell in (ranged ? FrontCells : BackCells))
                        if (_session.PlaceOnBoard(u.Id, cell)) { placed = true; break; }
                if (!placed) break;
            }
        }

        // ─────────────────────── 调试面板 ───────────────────────

        private void OnGUI()
        {
            var panel = new Rect(12f, 12f, 372f, 320f);
            GUI.Box(panel, "自走棋 · 战台调试（批次4C）");

            float y = panel.y + 26f;
            if (_session == null)
            {
                GUI.Label(new Rect(panel.x + 16f, y, 340f, 22f), "会话未建立");
                return;
            }

            string state = _session.RunOver
                ? $"对局终局（{( _lastWon ? "胜" : "败" )}）"
                : $"R{_session.Round} · {_session.Phase}";
            GUI.Label(new Rect(panel.x + 16f, y, 340f, 22f), $"状态：{state} · 种子 {_seed}");
            y += 24f;

            // 主按钮
            bool canPlay = !_busy && !_session.RunOver
                && (_session.Phase == GamePhase.Prep || _session.Phase == GamePhase.Settlement);
            GUI.enabled = canPlay;
            if (GUI.Button(new Rect(panel.x + 16f, y, 170f, 30f), "开战（预演+播放）"))
                PlayNext();
            GUI.enabled = true;
            if (_session.RunOver)
            {
                if (GUI.Button(new Rect(panel.x + 196f, y, 160f, 30f), "重开一局（换种子）"))
                {
                    _seed++;
                    _view.BeginBattle(null, null);
                    _log.Clear();
                    NewRun();
                }
            }
            y += 36f;

            // 速率旋钮（0=暂停 / 0.5 / 1 / 2 同一旋钮）
            GUI.Label(new Rect(panel.x + 16f, y, 120f, 22f), $"速率 ×{_player.Rate:0.0#}");
            y += 22f;
            DrawRateButton(panel.x + 16f, y, "暂停", 0f);
            DrawRateButton(panel.x + 96f, y, "0.5×", 0.5f);
            DrawRateButton(panel.x + 176f, y, "1×", 1f);
            DrawRateButton(panel.x + 256f, y, "2×", 2f);
            y += 30f;

            // 时间轴进度
            float progress = _player.Progress;
            GUI.Box(new Rect(panel.x + 16f, y, 340f, 18f), "");
            GUI.Box(new Rect(panel.x + 16f, y, 340f * progress, 18f), "");
            y += 22f;
            GUI.Label(new Rect(panel.x + 16f, y, 340f, 22f),
                $"播放 {_player.Time:F1}s / {_player.Makespan:F1}s · 节点 {_player.DispatchedCount}/{_player.TotalCount}（封口 {_player.CappedCount}）");
            y += 22f;
            GUI.Label(new Rect(panel.x + 16f, y, 340f, 22f),
                $"场上 {_view.ActorCount} 体（阵亡 {_view.DeadCount}）· 矿 {_view.OreCount} 个");
            y += 26f;

            // 日志尾
            GUI.Label(new Rect(panel.x + 16f, y, 340f, 22f), "—— 运行日志 ——");
            y += 20f;
            int from = Math.Max(0, _log.Count - 7);
            for (int i = from; i < _log.Count; i++)
            {
                GUI.Label(new Rect(panel.x + 16f, y, 340f, 20f), _log[i]);
                y += 19f;
                if (y > panel.yMax - 18f) break;
            }
        }

        private void DrawRateButton(float x, float y, string label, float rate)
        {
            bool active = Mathf.Approximately(_player.Rate, rate);
            GUI.enabled = !active;
            if (GUI.Button(new Rect(x, y, 72f, 26f), active ? $"[{label}]" : label))
                _player.Rate = rate;
            GUI.enabled = true;
        }

        private void Log(string line)
        {
            _log.Add(line);
            Debug.Log($"[BattleRoot] {line}");
        }
    }
}
