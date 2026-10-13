using System.Collections;
using UnityEngine;
using YooAsset;

namespace AutoChess.Runtime
{
    /// <summary>
    /// YooAsset 加载门面（AOT 侧）。
    /// 为什么需要它：YooAsset 取货 API 是泛型的（LoadAssetAsync<T>）。热更代码直接调
    /// AOT 泛型方法会踩 HybridCLR 的"AOT 泛型补充元数据"坑（真机上缺实例化直接崩）。
    /// 门面把包/版本/清单的复杂度封死在 AOT 内，热更区只见简单签名。
    /// </summary>
    public static class AssetService
    {
        private static ResourcePackage _package;
        private static SceneHandle _mainSceneHandle;   // 句柄必须长期持有：否则场景可能被误判闲置而卸载

        public static bool IsReady { get; private set; }

        /// <summary>五步初始化链（编辑器=EditorSimulate 彩排：内存里模拟构建，不落盘）。</summary>
        public static IEnumerator InitializePackage(string packageName)
        {
            if (IsReady) yield break;                                   // 幂等：重复调用无副作用

            if (YooAssets.IsInitialized == false)                      // ① 全局开关，全工程只开一次
                YooAssets.Initialize();

            _package = YooAssets.CreatePackage(packageName);           // ② 拿到包的"遥控器"

            var buildResult = EditorSimulateBuildInvoker.Build(       // ③ 彩排：按收集器配置模拟构建
                packageName, (int)EBundleType.VirtualAssetBundle);
            var options = new EditorSimulateModeOptions();
            options.EditorFileSystemParameters =
                FileSystemParameters.CreateDefaultEditorFileSystemParameters(buildResult.PackageRootDirectory);
            var initOp = _package.InitializePackageAsync(options);
            yield return initOp;
            if (initOp.Status != EOperationStatus.Succeeded)
            { Debug.LogError($"[AssetService] 包初始化失败: {initOp.Error}"); yield break; }

            var versionOp = _package.RequestPackageVersionAsync();     // ④ 问"当前该用哪个版本"
            yield return versionOp;
            if (versionOp.Status != EOperationStatus.Succeeded)
            { Debug.LogError($"[AssetService] 请求包版本失败: {versionOp.Error}"); yield break; }

            var manifestOp = _package.LoadPackageManifestAsync(       // ⑤ 装载清单——装完才有"货架"
                new LoadPackageManifestOptions(versionOp.PackageVersion, 60));
            yield return manifestOp;
            if (manifestOp.Status != EOperationStatus.Succeeded)
            { Debug.LogError($"[AssetService] 装载清单失败: {manifestOp.Error}"); yield break; }

            IsReady = true;
            Debug.Log($"[AssetService] 资源包就绪: {packageName} v{versionOp.PackageVersion}（EditorSimulate）");
        }

        /// <summary>加载主场景（Single：替换当前场景）。location = 资产路径去扩展名。</summary>
        public static void LoadMainScene(string location)
        {
            if (!IsReady) { Debug.LogError("[AssetService] 包未就绪"); return; }
            _mainSceneHandle = _package.LoadSceneAsync(location);
            Debug.Log($"[AssetService] 场景加载请求已发出: {location}");
        }
    }
}