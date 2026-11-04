using AutoChess.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AutoChess.Presentation
{
    /// <summary>三段场景流驱动器（批次4S 骨架，P4-4）。
    /// Bootstrap → Start → Main 的推进、Start 侧临时开始按钮都由它承担。
    /// 为什么跨场景存活：它生于 Bootstrap（移交瞬间），Single 模式加载会卸掉
    /// Bootstrap 的全部内容——不挂 DontDestroyOnLoad 就会在第一跳时陪葬。
    /// 4U1：FGUI 开始界面替换临时按钮（ClickStartGame 保留为统一入口）；
    /// 4B2：Main 侧战台组合根 BattleRoot 接管，本驱动器自毁退场。</summary>
    public sealed class SceneFlowDriver : MonoBehaviour
    {
        private const string StartLocation = "Assets/Res/Scenes/Start";   // YooAsset 路径制，去扩展名
        private const string StartScenePath = "Assets/Res/Scenes/Start.scene";
        private const string MainLocation = "Assets/Res/Scenes/Main";
        private const string MainScenePath = "Assets/Res/Scenes/Main.scene";

        private bool _mainRequested;
        private string _envProbedPath;   // 已按天空盒重算过环境光探针的场景路径（见 Update 内注释）

        private void Start()
        {
            _envProbedPath = SceneManager.GetActiveScene().path;   // 当前=Bootstrap，编辑器已算好探针，跳过

            // 第一跳：Bootstrap → Start（开始界面场景）
            AssetService.LoadScene(StartLocation);
            Debug.Log($"[SceneFlow] 场景流启动 → 请求加载 Start: {StartLocation}");
        }

        private void OnGUI()
        {
            if (_mainRequested) return;
            if (SceneManager.GetActiveScene().path != StartScenePath) return;   // Start 未就位前不亮按钮

            // 临时开始按钮（IMGUI 占位，4U1 由 FGUI 开始界面替换）
            var rect = new Rect(Screen.width * 0.5f - 110f, Screen.height * 0.62f, 220f, 64f);
            if (GUI.Button(rect, "开始游戏"))
                ClickStartGame();
        }

        /// <summary>点击"开始游戏"的统一入口（4U1 的 FGUI 按钮与验收脚本都走这里）。</summary>
        public void ClickStartGame()
        {
            if (_mainRequested) return;
            _mainRequested = true;
            Debug.Log("[SceneFlow] 开始游戏 → 请求加载 Main（战台）");
            AssetService.LoadScene(MainLocation);   // 第二跳：Start → Main
        }

        private void Update()
        {
            var path = SceneManager.GetActiveScene().path;

            // 场景激活后按本场景的天空盒重算环境光探针。
            // 为什么必须补：Unity 只在"编辑器打开场景"时自动按天空盒算环境光探针，
            // 运行时 LoadScene 切场景不重算——不补这刀，链路进入的场景探针全零、
            // 天空盒环境光整段缺失，画面比直接打开明显发暗（2026-11-04 实测抓回）。
            if (path != _envProbedPath)
            {
                DynamicGI.UpdateEnvironment();
                _envProbedPath = path;
                Debug.Log($"[SceneFlow] 场景激活，环境光探针按本场景天空盒重算: {path}");
            }

            // 使命完成检测：Main 已成为活动场景 → 装配战台组合根（4B2 BattleRoot）后自毁退场
            if (_mainRequested && path == MainScenePath)
            {
                var rootGo = new GameObject("BattleRoot");
                rootGo.AddComponent<BattleRoot>();
                Debug.Log("[SceneFlow] Main 战台已就位 → 装配 BattleRoot 组合根，场景流驱动器退场");
                Destroy(gameObject);
            }
        }
    }
}
