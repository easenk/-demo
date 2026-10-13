using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace AutoChess.Runtime
{
    /// <summary>
    /// AOT 启动链总指挥：资源系统就位 → 反射移交控制权给热更组合根。
    /// 为什么移交必须反射：本程序集【不引用】热更程序集（HybridCLR 铁律：AOT 不认识热更），
    /// 编译器在 AOT 侧根本看不见热更类型，直接写 HotUpdateEntry.Run() 会编译失败。
    /// </summary>
    public sealed class GameLauncher : MonoBehaviour
    {
        [Header("启动链配置")]
        [SerializeField] private string packageName = "DefaultPackage";
        [SerializeField] private string entryTypeName = "AutoChess.Presentation.HotUpdateEntry";
        [SerializeField] private string entryMethodName = "Run";

        private IEnumerator Start()
        {
            DontDestroyOnLoad(gameObject);   // 跨场景存活：移交后它仍是 AOT 服务的宿主

            // ── 第①幕：资源系统就位 ──
            yield return AssetService.InitializePackage(packageName);
            if (!AssetService.IsReady)
            { Debug.LogError("[GameLauncher] 资源系统未就绪，启动链终止"); yield break; }

            // ── 第②幕：定位热更程序集 ──
            // 编辑器形态：热更程序集本来就编译在域里，遍历即得。
            // 真机形态（阶段6）：在反射之前插入"读 dll 字节 → Assembly.Load(字节)"，其余不变。
            Assembly hotAsm = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                if (asm.GetName().Name == "AutoChess.Presentation") { hotAsm = asm; break; }
            if (hotAsm == null)
            { Debug.LogError("[GameLauncher] 热更程序集未找到"); yield break; }

            // ── 第③幕：反射移交 ──
            var entryType = hotAsm.GetType(entryTypeName);
            var method = entryType?.GetMethod(entryMethodName, BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            { Debug.LogError($"[GameLauncher] 入口不存在: {entryTypeName}.{entryMethodName}"); yield break; }

            Debug.Log($"[GameLauncher] 移交控制权 → {entryTypeName}.{entryMethodName}");
            method.Invoke(null, new object[] { "启动链就绪" });
        }
    }
}