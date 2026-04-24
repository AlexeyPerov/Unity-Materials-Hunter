// ReSharper disable CommentTypo
// #define HUNT_ADDRESSABLES
// #define HUNT_LAYOUT

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
// ReSharper disable StringLiteralTypo
// ReSharper disable UnusedParameter.Local
// ReSharper disable UnassignedGetOnlyAutoProperty

#if HUNT_ADDRESSABLES
using UnityEditor.AddressableAssets;
#endif
#if HUNT_LAYOUT
using Oddworm.EditorFramework;
#endif

// ReSharper disable once CheckNamespace
namespace MaterialsHunter
{
    /// <summary>
    /// Scans project for all asset materials and renderers
    /// Finds
    /// - refs to default assets (empty materials in renderers, empty textures in materials)
    /// - duplicate materials
    /// </summary>
    public class MaterialsHunterWindow : EditorWindow
    {
        private class Result
        {
            public List<RendererComponentData> Renderers { get; } = new();
            public List<MaterialAssetData> Materials { get; } = new();
            public string OutputDescription { get; set; }
            public Dictionary<string, int> ShaderUsageCounts { get; set; }
            
            public List<RendererComponentData> FilteredRenderers { get; private set; } = new();
            public List<MaterialAssetData> FilteredMaterials { get; private set; } = new();

            public void Filter(SearchPatternsSettings searchPatternsSettings)
            {
                FilteredRenderers = Renderers.Where(x => IsPathValidForOutput(x.Path, searchPatternsSettings.IgnoredPatterns)).ToList();
                FilteredMaterials = Materials.Where(x => IsPathValidForOutput(x.Path, searchPatternsSettings.IgnoredPatterns)).ToList();
            }
             
            private bool IsPathValidForOutput(string path, List<string> ignoreInOutputPatterns)
            {
                return ignoreInOutputPatterns.All(pattern 
                    => string.IsNullOrEmpty(pattern) || !Regex.Match(path, pattern).Success);
            }
        }
        
        private class AnalysisSettings
        {
            public const int DefaultGCStep = 100000;
            
            public bool DefaultMaterialsAreErrors { get; set; } = true;
            public bool NullMaterialsAreErrors { get; set; }
            public bool DefaultTexturesAreErrors { get; set; } = true;
            public bool NullTexturesAreErrors { get; set; }
            public bool DuplicateMaterialsAreErrors { get; set; } = true;
            public bool UnusedMaterialsAreErrors { get; set; } = true;
            public bool BuiltinShadersAreErrors { get; set; } = true;
            
            public bool VariantChainsAreErrors { get; set; } = true;
            public bool VariantHeavyOverridesAreErrors { get; set; } = true;
            public int VariantDeepChainThreshold { get; set; } = 3;
            public int VariantHeavyOverridesThreshold { get; set; } = 8;
            
            public bool InstancingDisabledAreErrors { get; set; } = true;
            public bool SrpBatcherIncompatibleAreErrors { get; set; } = true;
            
            public int GarbageCollectStep { get; set; } = DefaultGCStep;
            
            // limit number of assets in analysis to perform it faster for debug purposes
            public int DebugLimit;
            
#if HUNT_LAYOUT            
            public RichBuildLayout BuildLayout { get; set; }

            public string GetBundleNameByAssetPath(string assetPath)
            {
                return BuildLayout == null ? string.Empty : BuildLayout.GetBundleNameByAssetPath(assetPath);
            }
#endif
        }

        private class IgnoredPatternsAsset : ScriptableObject
        {
            // ReSharper disable once InconsistentNaming
            public List<string> IgnoredPatterns = new List<string>();
        }
        
        private class SearchPatternsSettings
        {
            // ReSharper disable once StringLiteralTypo
            public readonly List<string> DefaultIgnorePatterns = new()
            {
                "/Editor/",
                "/Editor Default Resources/",
                @"/Editor Resources/",
                "ProjectSettings/"
            };

            public List<string> IgnoredPatterns
            {
                get
                {
                    if (IgnoredPatternsAsset == null)
                        return DefaultIgnorePatterns;
                    return IgnoredPatternsAsset.IgnoredPatterns;
                }
            }

            public bool IsIgnoredPatternsAssetUsed => IgnoredPatternsAsset != null;
            public bool TriedLoadingIgnoredPatterns { get; private set; }

            public IgnoredPatternsAsset IgnoredPatternsAsset { get; private set; }

            private const string IgnoredPatternsFileName = "MaterialsHunterIgnorePatterns.asset";
            private static readonly string IgnoredPatternsFilePath = Path.Combine("Assets/Editor", IgnoredPatternsFileName);

            public void CreateIgnoredPatternsAsset()
            {
                try
                {
                    if (!AssetDatabase.IsValidFolder("Assets/Editor"))
                    {
                        AssetDatabase.CreateFolder("Assets", "Editor");
                    }
                    
                    var asset = CreateInstance<IgnoredPatternsAsset>();
                    asset.IgnoredPatterns = new List<string>(DefaultIgnorePatterns);

                    AssetDatabase.CreateAsset(asset, IgnoredPatternsFilePath);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();

                    IgnoredPatternsAsset = asset;
                }
                catch (Exception e)
                {
                    Debug.LogError($"Failed to save SearchIgnorePatterns: {e}");
                }
            }

            public void TryLoadIgnoredPatternsAsset()
            {
                try
                {
                    TriedLoadingIgnoredPatterns = true;
                    
                    if (!File.Exists(IgnoredPatternsFilePath))
                    {
                        return;
                    }

                    IgnoredPatternsAsset = AssetDatabase.LoadAssetAtPath<IgnoredPatternsAsset>(IgnoredPatternsFilePath);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Failed to load IgnoredPatterns file: {e}");
                }
            }

            public void DeleteIgnoredPatternsAsset()
            {
                try
                {
                    if (File.Exists(IgnoredPatternsFilePath))
                    {
                        File.Delete(IgnoredPatternsFilePath);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"Failed to delete IgnoredPatterns file: {e}");
                }
                finally
                {
                    AssetDatabase.Refresh();
                }
            }
        }
     
        private enum OutputFilterType
        {
            MaterialAssets,
            RendererComponents
        }
        
        private class OutputSettings
        {
            public const int PageSize = 50;

            public string PathFilter { get; set; }
            public OutputFilterType TypeFilter { get; set; }
            
            public MaterialAssetsOutputSettings MaterialAssetsSettings { get; } = new();
            public RendererComponentsOutputSettings RendererComponentsSettings { get; } = new();
        }

        private class RendererComponentsOutputSettings : IPaginationSettings
        {
            public int? PageToShow { get; set; } = 0;
            
            /// <summary>
            /// Sorting types.
            /// By warning level: 0: A-Z, 1: Z-A
            /// By path: 2: A-Z, 3: Z-A
            /// By material count: 4: A-Z, 5: Z-A
            /// By warnings count: 6: A-Z, 7: Z-A
            /// </summary>
            public int SortType { get; set; }
            
            public bool WarningsOnly { get; set; }
        }
        
        private class MaterialAssetsOutputSettings : IPaginationSettings
        {
            public int? PageToShow { get; set; } = 0;
            
            /// <summary>
            /// Sorting types.
            /// By warning level: 0: A-Z, 1: Z-A
            /// By path: 2: A-Z, 3: Z-A
            /// By size: 4: A-Z, 5: Z-A
            /// </summary>
            public int SortType { get; set; }
            
            public bool WarningsOnly { get; set; }
        }
        
        private interface IPaginationSettings
        {
            int? PageToShow { get; set; }
        }

        private abstract class ItemDataBase
        {
            public int WarningLevel { get; private set; }
            
            public void TrySetWarningLevel(int level)
            {
                if (level <= WarningLevel) return;
                WarningLevel = level;
            }

            public List<string> CustomWarnings { get; private set; }

            public void AddCustomWarning(string warning)
            {
                CustomWarnings ??= new List<string>();
                CustomWarnings.Add(warning);
            }
        }

        /// <summary>Single source of truth for <see cref="ItemDataBase.AddCustomWarning"/> user-visible strings and shared match tokens.</summary>
        private static class CustomWarningMessages
        {
            public const string NullMaterial = "Null material";
            public const string NullMaterialSlot = "Null material slot";
            public const string UnityBuiltinMaterialPrefix = "unity_builtin material at ";
            public const string UnableToLoad = "Unable to load";
            public const string TextureNullPrefix = "Texture is null at ";
            public const string UnityBuiltinTexturePrefix = "unity_builtin texture at ";
            public const string ShaderIsNull = "Shader is null";
            public const string ShaderInternalErrorShader = "Shader is missing (InternalErrorShader)";
            public const string BuiltInShaderPrefix = "Built-in shader: ";
            public const string RenderQueueOverridePrefix = "Render queue override: ";
            public const string DuplicateOfPrefix = "Duplicate of ";
            public const string NotReferencedUnused = "Not referenced by any renderer, not in Resources, not Addressable";
            public const string NotReferencedPrefix = "Not referenced by any renderer";
            public const string VariantParentInvalid = "Material variant: parent is missing or invalid";
            public const string GpuInstancingOff = "GPU instancing is disabled but shader supports it";
            public const string TokenSrpBatcher = "is not SRP Batcher compatible";

            public const string TokenVariantChainDepth = "Variant chain depth";
            public const string TokenExceedsThreshold = "exceeds threshold";
            public const string TokenHeavyVariantOverrides = "Heavy variant overrides";
            public const string HeavyVariantOverridesPrefix = "Heavy variant overrides: ";
            public const string TokenGpuInstancingDisabled = "GPU instancing is disabled";

            public static string UnityBuiltinMaterialAt(string fullTransformName) => UnityBuiltinMaterialPrefix + fullTransformName;
            public static string TextureIsNullAt(string propertyName) => TextureNullPrefix + propertyName;
            public static string UnityBuiltinTextureAt(string propertyName) => UnityBuiltinTexturePrefix + propertyName;
            public static string BuiltInShaderLine(string shaderName) => BuiltInShaderPrefix + shaderName;
            public static string RenderQueueOverrideLine(int current, int? shaderDefault) =>
                $"{RenderQueueOverridePrefix}{current} (shader default: {shaderDefault})";
            public static string DuplicateOfLine(int otherCount, string otherNamesJoined) =>
                $"{DuplicateOfPrefix}{otherCount} material(s): {otherNamesJoined}";
            public static string VariantChainDepthLine(int chainDepth, int threshold) =>
                $"{TokenVariantChainDepth} {chainDepth} {TokenExceedsThreshold} {threshold}";
            public static string HeavyVariantOverridesLine(int? overrideCount, int threshold) =>
                $"{HeavyVariantOverridesPrefix}{overrideCount} (threshold {threshold})";
            public static string ShaderNotSrpBatcherLine(string shaderName) => $"Shader \"{shaderName}\" {TokenSrpBatcher}";
        }

        private static class CustomWarningTooltips
        {
            private static readonly (Func<string, bool> Match, string Tip)[] Rules = BuildRules();

            private static (Func<string, bool> M, string T)[] BuildRules() => new[]
            {
                (Eq(CustomWarningMessages.NullMaterial),
                    "This renderer's sharedMaterials list is empty. Rendering may be missing, pink, or use pipeline defaults until a material is assigned."),
                (Eq(CustomWarningMessages.NullMaterialSlot),
                    "At least one entry in sharedMaterials is null. Submesh or slot mapping may be out of date; fix slots or the mesh's material list."),
                (StartsWith(CustomWarningMessages.UnityBuiltinMaterialPrefix),
                    "A Unity built-in / internal default material is assigned on a slot. Use a project material for predictable and portable results."),
                (Eq(CustomWarningMessages.UnableToLoad),
                    "The material asset could not be loaded. It may be missing, broken, or blocked by a script/import error."),
                (StartsWith(CustomWarningMessages.TextureNullPrefix),
                    "The shader's texture property has no texture assigned. Assign a texture or clear unused slots if the shader still expects a binding."),
                (StartsWith(CustomWarningMessages.UnityBuiltinTexturePrefix),
                    "This texture property points to a built-in/embedded Unity resource. Use a project texture for stable packaging and art direction."),
                (Eq(CustomWarningMessages.ShaderIsNull),
                    "The material's shader is missing. The asset cannot render correctly until a valid shader is assigned."),
                (Eq(CustomWarningMessages.ShaderInternalErrorShader),
                    "Unity is using the InternalErrorShader placeholder because the real shader is missing, broken, or not compiled for this SRP."),
                (StartsWith(CustomWarningMessages.BuiltInShaderPrefix),
                    "The material uses a Unity built-in shader. If your project standard is custom/URP/HDRP shaders, replace or upgrade as needed."),
                (StartsWith(CustomWarningMessages.RenderQueueOverridePrefix),
                    "Render queue is set differently from the shader's default, which can change transparency sorting and when the draw happens."),
                (StartsWith(CustomWarningMessages.DuplicateOfPrefix),
                    "This material's serialized fingerprint matches other materials. Merging duplicates can cut asset count, variants, and maintenance."),
                (Eq(CustomWarningMessages.NotReferencedUnused),
                    "No renderer in the scan references this material, and it is not in Resources/ nor marked Addressable, so it may be dead or only loaded indirectly."),
                (Eq(CustomWarningMessages.VariantParentInvalid),
                    "This Material Variant (or an equivalent parent link) has no resolvable parent, so inherited values may be wrong or the setup is broken."),
                (w => w.Contains(CustomWarningMessages.TokenVariantChainDepth, StringComparison.Ordinal) &&
                    w.Contains(CustomWarningMessages.TokenExceedsThreshold, StringComparison.Ordinal),
                    "The parent-to-parent chain is longer than the configured threshold, which is harder to author and reason about than shallow variants."),
                (StartsWith(CustomWarningMessages.HeavyVariantOverridesPrefix),
                    "The variant changes many things versus its parent, so a standalone material or a new parent may be simpler than a deep override list."),
                (Eq(CustomWarningMessages.GpuInstancingOff),
                    "The shader can batch instances, but the material has GPU instancing off, so you may be missing draw-call savings on repeated meshes."),
                (w => w.Contains(CustomWarningMessages.TokenSrpBatcher, StringComparison.Ordinal),
                    "The shader is reported as not compatible with the SRP Batcher; you may not get the same per-frame CPU batching benefits as SRP Batcher-friendly shaders.")
            };

            private static Func<string, bool> Eq(string s) => w => w == s;
            private static Func<string, bool> StartsWith(string p) => w => w.StartsWith(p, StringComparison.Ordinal);

            public static string GetTooltipOrEmpty(string warning)
            {
                if (string.IsNullOrEmpty(warning))
                    return string.Empty;
                foreach (var (m, t) in Rules)
                {
                    if (m(warning))
                        return t;
                }

                return string.Empty;
            }
        }
        
        private class RendererComponentData : ItemDataBase
        {
            public string Path { get; }
            public string GameObjectName => System.IO.Path.GetFileName(Path);
            
            public string ChildName { get; }
         
            public bool Foldout { get; set; }
            
            public string Bundle { get; }
            public int MaterialSlotsCount { get; set; }
            public int WarningsCount => CustomWarnings?.Count ?? 0;
            
            public RendererComponentData(
                string path,
                string childName,
                AnalysisSettings analysisSettings)
            {
                Path = path;
                ChildName = childName;
#if HUNT_LAYOUT
                Bundle = analysisSettings.GetBundleNameByAssetPath(path);
#endif
            }
        }

        private class MaterialPropertyData
        {
            public string Name { get; }
            public string Type { get; }
            public string Value { get; }
            public string ReadableSize { get; }
            public List<string> UsedByMaterialPaths { get; set; }

            public MaterialPropertyData(string name, string type, string value, string readableSize = null)
            {
                Name = name;
                Type = type;
                Value = value;
                ReadableSize = readableSize;
            }
        }

        private class MaterialAssetData : ItemDataBase
        {
            public MaterialAssetData(
                string path, 
                Type type,
                string typeName,
                long bytesSize, 
                string readableSize,
                AnalysisSettings analysisSettings)
            {
                Path = path;
                Type = type;
                TypeName = typeName;
                BytesSize = bytesSize;
                ReadableSize = readableSize;

                InResources = Path.Contains("/Resources/");

                IsAddressable = CommonUtilities.IsAssetAddressable(Path);
#if HUNT_LAYOUT
                Bundle = analysisSettings.GetBundleNameByAssetPath(path);
#endif
            }

            public string Path { get; }
            public string Name => System.IO.Path.GetFileName(Path);
            public Type Type { get; }
            public string TypeName { get; }
            public long BytesSize { get; }
            public string ReadableSize { get; }
            public bool Foldout { get; set; }
            public bool PropertiesFoldout { get; set; }
            public bool TextureReferencesFoldout { get; set; }
            public Dictionary<string, bool> TextureUsedByMaterialsFoldout { get; } = new Dictionary<string, bool>();

            public string Bundle { get; }
            
            public bool InResources { get; }
            public bool IsAddressable { get; }

            public string Fingerprint { get; set; }
            public List<string> DuplicatePaths { get; private set; }

            public string ShaderName { get; set; }
            public int RenderQueue { get; set; }
            public int? ShaderDefaultRenderQueue { get; set; }
            public bool HasRenderQueueOverride => ShaderDefaultRenderQueue.HasValue && RenderQueue != ShaderDefaultRenderQueue.Value;
            public List<string> EnabledKeywords { get; set; }
            public List<MaterialPropertyData> Properties { get; set; }
            public List<string> ReferencedTexturePaths { get; private set; }

            public bool IsMissingShader { get; set; }
            public bool IsBuiltinShader { get; set; }

            public List<string> ReferencedByPaths { get; private set; }

            public void AddDuplicatePath(string path)
            {
                DuplicatePaths ??= new List<string>();
                DuplicatePaths.Add(path);
            }

            public void AddReferencedByPath(string path)
            {
                ReferencedByPaths ??= new List<string>();
                ReferencedByPaths.Add(path);
            }

            public bool IsVariant { get; set; }
            public string ParentMaterialPath { get; set; }
            public int VariantChainDepth { get; set; }
            public int? VariantOverrideCount { get; set; }
            public bool ParentLinkBroken { get; set; }
            public List<string> VariantChildrenPaths { get; private set; }

            public bool? SupportsGpuInstancing { get; set; }
            public bool GpuInstancingEnabled { get; set; }
            public bool? SrpBatcherCompatible { get; set; }

            public void AddVariantChildPath(string path)
            {
                VariantChildrenPaths ??= new List<string>();
                if (!VariantChildrenPaths.Contains(path))
                    VariantChildrenPaths.Add(path);
            }

            public void SetReferencedTexturePaths(IEnumerable<string> paths)
            {
                ReferencedTexturePaths = paths?.Distinct().OrderBy(x => x).ToList();
            }
        }

        private class RendererScanCacheEntry
        {
            public Hash128 DependencyHash { get; set; }
            public List<RendererComponentData> RendererRows { get; set; }
            public Dictionary<string, List<string>> MaterialToRendererPaths { get; set; }
        }

        private class MaterialScanCacheEntry
        {
            public Hash128 DependencyHash { get; set; }
            public MaterialAssetData MaterialData { get; set; }
        }
        
        [MenuItem("Tools/Materials Hunter")]
        public static void LaunchWindow()
        {
            GetWindow<MaterialsHunterWindow>();
        }
        
        private static void Clear()
        {
            EditorUtility.UnloadUnusedAssetsImmediate();
        }
        
        private void OnDestroy()
        {
            ClearSessionCaches();
            Clear();
        }
        
        private Result _result;
        private OutputSettings _outputSettings;
        private AnalysisSettings _analysisSettings;
        private SearchPatternsSettings _searchPatternsSettings;
        
        private bool _analysisSettingsFoldout;
        private bool _batchOperationsFoldout;
        private bool _searchPatternsSettingsFoldout;
        private bool _shaderUsageFoldout;
        
        private bool _batchOperationsJustLog;
        
        private Dictionary<string, List<string>> _materialToRendererPaths;
        
        private Vector2 _rendererPagesScroll = Vector2.zero;
        private Vector2 _renderersScroll = Vector2.zero;
        
        private Vector2 _materialsPagesScroll = Vector2.zero;
        private Vector2 _materialsScroll = Vector2.zero;
        
        private bool _analysisOngoing;
        private Material _batchReplaceSourceMaterial;
        private Material _batchReplaceTargetMaterial;
        private Material _batchBuiltinFallbackMaterial;
        private Shader _batchMissingShaderFallback;
        private bool _batchTargetOnlyFilteredRenderers = true;
        private bool _hasRendererScanResults;
        private bool _hasMaterialScanResults;
        private readonly Dictionary<string, RendererScanCacheEntry> _rendererScanCache = new();
        private readonly Dictionary<string, MaterialScanCacheEntry> _materialScanCache = new();
        private string _analysisSettingsCacheSignature;
        
        private IEnumerator PopulateRenderersList()
        {
            _analysisOngoing = true;
            EnsureCacheCompatibility();
            
            _result ??= new Result();
            
            _result.Renderers.Clear();
            _result.FilteredRenderers.Clear();
            
            _outputSettings ??= new OutputSettings();

            if (_analysisSettings.GarbageCollectStep < 0)
            {
                _analysisSettings.GarbageCollectStep = AnalysisSettings.DefaultGCStep;
            }

            _materialToRendererPaths = new Dictionary<string, List<string>>();

            Clear();
            Show();
            
            EditorUtility.ClearProgressBar();

            var assetPaths = AssetDatabase.GetAllAssetPaths();
            var rendererAssetPaths = new List<string>();
            foreach (var assetPath in assetPaths)
            {
                if (AssetDatabase.GetMainAssetTypeAtPath(assetPath) == typeof(GameObject))
                {
                    rendererAssetPaths.Add(assetPath);
                }
            }

            var rendererAssetPathSet = new HashSet<string>(rendererAssetPaths);
            var reusedAssets = 0;
            var rescannedAssets = 0;

            for (var assetIndex = 0; assetIndex < rendererAssetPaths.Count; assetIndex++)
            {
                if (_analysisSettings.GarbageCollectStep != 0 && assetIndex % _analysisSettings.GarbageCollectStep == 0)
                {
                    GC.Collect();
                    yield return 0.05f;
                    GC.Collect();
                }
                
                var assetPath = rendererAssetPaths[assetIndex];
                EditorUtility.DisplayProgressBar("Materials Hunter", "Scanning for renderers",
                    (float)assetIndex / Mathf.Max(1, rendererAssetPaths.Count));

                var dependencyHash = GetAssetDependencyHashSafe(assetPath);
                if (_rendererScanCache.TryGetValue(assetPath, out var rendererCacheEntry) &&
                    rendererCacheEntry.DependencyHash == dependencyHash)
                {
                    reusedAssets++;
                    foreach (var cachedRow in rendererCacheEntry.RendererRows)
                    {
                        _result.Renderers.Add(CloneRendererComponentData(cachedRow));
                    }

                    MergeMaterialToRendererPaths(_materialToRendererPaths, rendererCacheEntry.MaterialToRendererPaths);
                }
                else
                {
                    rescannedAssets++;
                    AnalyzeRendererAsset(assetPath, out var scannedRows, out var scannedMaterialToRendererPaths);
                    foreach (var row in scannedRows)
                    {
                        _result.Renderers.Add(row);
                    }

                    MergeMaterialToRendererPaths(_materialToRendererPaths, scannedMaterialToRendererPaths);

                    _rendererScanCache[assetPath] = new RendererScanCacheEntry
                    {
                        DependencyHash = dependencyHash,
                        RendererRows = scannedRows.Select(CloneRendererComponentData).ToList(),
                        MaterialToRendererPaths = scannedMaterialToRendererPaths
                            .ToDictionary(x => x.Key, x => x.Value.ToList())
                    };
                }

                if (_analysisSettings.DebugLimit > 0 && _result.Renderers.Count > _analysisSettings.DebugLimit)
                    break;
            }

            RemoveMissingCacheEntries(_rendererScanCache, rendererAssetPathSet);

            GC.Collect();
            
            if (_analysisSettings.GarbageCollectStep != 0)
            {
                yield return 0.1f;
                GC.Collect();
            }
            
            ApplyDefaultWarningSorting();

            _result.Filter(_searchPatternsSettings);

            _result.OutputDescription = $"Renderers (Shown/Total): {_result.FilteredRenderers.Count} / {_result.Renderers.Count}. " +
                                        $"Materials (Shown/Total): {_result.FilteredMaterials.Count} / {_result.Materials.Count}";
            
            EditorUtility.ClearProgressBar();
            
            Debug.Log(_result.OutputDescription);
            Debug.Log($"Renderer scan cache: reused={reusedAssets}, rescanned={rescannedAssets}, totalAssets={rendererAssetPaths.Count}");

            _hasRendererScanResults = true;
            _analysisOngoing = false;
        }

        private void ClearSessionCaches()
        {
            _rendererScanCache.Clear();
            _materialScanCache.Clear();
            _analysisSettingsCacheSignature = null;
        }

        private void EnsureCacheCompatibility()
        {
            var currentSignature = BuildAnalysisSettingsCacheSignature();
            if (string.Equals(_analysisSettingsCacheSignature, currentSignature, StringComparison.Ordinal))
                return;

            ClearSessionCaches();
            _analysisSettingsCacheSignature = currentSignature;
        }

        private string BuildAnalysisSettingsCacheSignature()
        {
            return string.Join("|",
                _analysisSettings.DefaultMaterialsAreErrors,
                _analysisSettings.NullMaterialsAreErrors,
                _analysisSettings.DefaultTexturesAreErrors,
                _analysisSettings.NullTexturesAreErrors,
                _analysisSettings.DuplicateMaterialsAreErrors,
                _analysisSettings.UnusedMaterialsAreErrors,
                _analysisSettings.BuiltinShadersAreErrors,
                _analysisSettings.VariantChainsAreErrors,
                _analysisSettings.VariantHeavyOverridesAreErrors,
                _analysisSettings.VariantDeepChainThreshold,
                _analysisSettings.VariantHeavyOverridesThreshold,
                _analysisSettings.InstancingDisabledAreErrors,
                _analysisSettings.SrpBatcherIncompatibleAreErrors,
                _analysisSettings.DebugLimit);
        }

        private static Hash128 GetAssetDependencyHashSafe(string assetPath)
        {
            try
            {
                return AssetDatabase.GetAssetDependencyHash(assetPath);
            }
            catch
            {
                return default;
            }
        }

        private static void CopyWarnings(ItemDataBase source, ItemDataBase target)
        {
            if (source.CustomWarnings != null)
            {
                foreach (var warning in source.CustomWarnings)
                {
                    target.AddCustomWarning(warning);
                }
            }

            target.TrySetWarningLevel(source.WarningLevel);
        }

        private RendererComponentData CloneRendererComponentData(RendererComponentData source)
        {
            var clone = new RendererComponentData(source.Path, source.ChildName, _analysisSettings)
            {
                Foldout = false,
                MaterialSlotsCount = source.MaterialSlotsCount
            };
            CopyWarnings(source, clone);
            return clone;
        }

        private static MaterialPropertyData CloneMaterialPropertyData(MaterialPropertyData source)
        {
            return new MaterialPropertyData(source.Name, source.Type, source.Value, source.ReadableSize)
            {
                UsedByMaterialPaths = source.UsedByMaterialPaths?.ToList()
            };
        }

        private MaterialAssetData CloneMaterialDataBaseSlice(MaterialAssetData source)
        {
            var clone = new MaterialAssetData(
                source.Path,
                source.Type,
                source.TypeName,
                source.BytesSize,
                source.ReadableSize,
                _analysisSettings)
            {
                Foldout = false,
                Fingerprint = source.Fingerprint,
                ShaderName = source.ShaderName,
                RenderQueue = source.RenderQueue,
                ShaderDefaultRenderQueue = source.ShaderDefaultRenderQueue,
                EnabledKeywords = source.EnabledKeywords?.ToList(),
                Properties = source.Properties?.Select(CloneMaterialPropertyData).ToList(),
                IsMissingShader = source.IsMissingShader,
                IsBuiltinShader = source.IsBuiltinShader,
                IsVariant = source.IsVariant,
                ParentMaterialPath = source.ParentMaterialPath,
                VariantChainDepth = source.VariantChainDepth,
                VariantOverrideCount = source.VariantOverrideCount,
                ParentLinkBroken = source.ParentLinkBroken,
                SupportsGpuInstancing = source.SupportsGpuInstancing,
                GpuInstancingEnabled = source.GpuInstancingEnabled,
                SrpBatcherCompatible = source.SrpBatcherCompatible
            };

            if (source.DuplicatePaths != null)
            {
                foreach (var dup in source.DuplicatePaths)
                {
                    clone.AddDuplicatePath(dup);
                }
            }

            if (source.ReferencedByPaths != null)
            {
                foreach (var refPath in source.ReferencedByPaths)
                {
                    clone.AddReferencedByPath(refPath);
                }
            }

            if (source.VariantChildrenPaths != null)
            {
                foreach (var childPath in source.VariantChildrenPaths)
                {
                    clone.AddVariantChildPath(childPath);
                }
            }

            clone.SetReferencedTexturePaths(source.ReferencedTexturePaths);
            CopyWarnings(source, clone);
            return clone;
        }

        private static void RemoveMissingCacheEntries<T>(Dictionary<string, T> cache, HashSet<string> existingPaths)
        {
            var staleKeys = cache.Keys.Where(key => !existingPaths.Contains(key)).ToList();
            foreach (var staleKey in staleKeys)
            {
                cache.Remove(staleKey);
            }
        }

        private static void MergeMaterialToRendererPaths(
            Dictionary<string, List<string>> target,
            Dictionary<string, List<string>> source)
        {
            foreach (var pair in source)
            {
                if (!target.TryGetValue(pair.Key, out var list))
                {
                    list = new List<string>();
                    target[pair.Key] = list;
                }

                foreach (var rendererPath in pair.Value)
                {
                    if (!list.Contains(rendererPath))
                    {
                        list.Add(rendererPath);
                    }
                }
            }
        }

        private void AnalyzeRendererAsset(
            string assetPath,
            out List<RendererComponentData> rendererRows,
            out Dictionary<string, List<string>> materialToRendererPaths)
        {
            rendererRows = new List<RendererComponentData>();
            materialToRendererPaths = new Dictionary<string, List<string>>();

            var loadedObject = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (loadedObject == null)
            {
                Debug.LogWarning($"Unable to load {assetPath}");
                return;
            }

            var renderers = loadedObject.GetComponentsInChildren<Renderer>(true);
            if (renderers == null || renderers.Length == 0)
                return;

            foreach (var component in renderers)
            {
                var rendererComponentData = new RendererComponentData(assetPath, component.transform.name, _analysisSettings);
                rendererRows.Add(rendererComponentData);

                var sharedMaterials = component.sharedMaterials;
                rendererComponentData.MaterialSlotsCount = sharedMaterials?.Length ?? 0;

                if (sharedMaterials == null || sharedMaterials.Length == 0)
                {
                    if (_analysisSettings.NullMaterialsAreErrors)
                    {
                        rendererComponentData.AddCustomWarning(CustomWarningMessages.NullMaterial);
                        rendererComponentData.TrySetWarningLevel(1);
                    }

                    continue;
                }

                foreach (var mat in sharedMaterials)
                {
                    if (mat == null)
                    {
                        if (_analysisSettings.NullMaterialsAreErrors)
                        {
                            rendererComponentData.AddCustomWarning(CustomWarningMessages.NullMaterialSlot);
                            rendererComponentData.TrySetWarningLevel(1);
                        }

                        continue;
                    }

                    var materialPath = AssetDatabase.GetAssetPath(mat);

                    if (materialPath != null && materialPath.Contains("unity_builtin"))
                    {
                        if (_analysisSettings.DefaultMaterialsAreErrors)
                        {
                            rendererComponentData.AddCustomWarning(
                                CustomWarningMessages.UnityBuiltinMaterialAt(CommonUtilities.GetFullName(component.transform)));
                            rendererComponentData.TrySetWarningLevel(2);
                        }

                        continue;
                    }

                    if (string.IsNullOrEmpty(materialPath))
                        continue;

                    if (!materialToRendererPaths.TryGetValue(materialPath, out var paths))
                    {
                        paths = new List<string>();
                        materialToRendererPaths[materialPath] = paths;
                    }

                    if (!paths.Contains(assetPath))
                    {
                        paths.Add(assetPath);
                    }
                }
            }
        }

        private IEnumerator PopulateMaterialAssetsList()
        {
            _analysisOngoing = true;
            EnsureCacheCompatibility();
            
            _result ??= new Result();
            
            _result.Materials.Clear();
            _result.FilteredMaterials.Clear();
            
            _outputSettings ??= new OutputSettings();

            if (_analysisSettings.GarbageCollectStep < 0)
            {
                _analysisSettings.GarbageCollectStep = AnalysisSettings.DefaultGCStep;
            }

            Clear();
            Show();
            
            EditorUtility.ClearProgressBar();

            var assetPaths = AssetDatabase.GetAllAssetPaths();
            var materialPaths = new List<string>();
            foreach (var assetPath in assetPaths)
            {
                if (AssetDatabase.GetMainAssetTypeAtPath(assetPath) == typeof(Material))
                {
                    materialPaths.Add(assetPath);
                }
            }
            var materialPathSet = new HashSet<string>(materialPaths);
            var reusedMaterials = 0;
            var rescannedMaterials = 0;

            for (var assetIndex = 0; assetIndex < materialPaths.Count; assetIndex++)
            {
                if (_analysisSettings.GarbageCollectStep != 0 && assetIndex % _analysisSettings.GarbageCollectStep == 0)
                {
                    GC.Collect();
                    yield return 0.05f;
                    GC.Collect();
                }
                
                var assetPath = materialPaths[assetIndex];
                EditorUtility.DisplayProgressBar("Materials Hunter", "Scanning for materials",
                    (float)assetIndex / Mathf.Max(1, materialPaths.Count));

                var dependencyHash = GetAssetDependencyHashSafe(assetPath);
                if (_materialScanCache.TryGetValue(assetPath, out var materialCacheEntry) &&
                    materialCacheEntry.DependencyHash == dependencyHash)
                {
                    reusedMaterials++;
                    var materialData = CloneMaterialDataBaseSlice(materialCacheEntry.MaterialData);
                    _result.Materials.Add(materialData);
                }
                else
                {
                    rescannedMaterials++;
                    var materialData = CreateMaterialData(assetPath);
                    FindMaterialWarnings(materialData);
                    _result.Materials.Add(materialData);

                    _materialScanCache[assetPath] = new MaterialScanCacheEntry
                    {
                        DependencyHash = dependencyHash,
                        MaterialData = CloneMaterialDataBaseSlice(materialData)
                    };
                }

                if (_analysisSettings.DebugLimit > 0 && _result.Materials.Count > _analysisSettings.DebugLimit)
                    break;
            }
            RemoveMissingCacheEntries(_materialScanCache, materialPathSet);
            
            GC.Collect();
            
            if (_analysisSettings.GarbageCollectStep != 0)
            {
                yield return 0.1f;
                GC.Collect();
            }
            
            if (_analysisSettings.DuplicateMaterialsAreErrors)
            {
                DetectDuplicateMaterials();
            }
            
            ApplyReferencedByPaths();
            
            if (_analysisSettings.UnusedMaterialsAreErrors)
            {
                DetectUnusedMaterials();
            }
            
            BuildShaderUsageCounts();
            BuildMaterialTextureCrossReference();
            AnalyzeMaterialVariantsAndPerformance();
            
            ApplyDefaultWarningSorting();

            _result.Filter(_searchPatternsSettings);

            var duplicateCount = _result.Materials.Count(m => m.DuplicatePaths != null && m.DuplicatePaths.Count > 0);
            var unusedCount = _result.Materials.Count(m => m.CustomWarnings != null && m.CustomWarnings.Any(w => w.StartsWith(CustomWarningMessages.NotReferencedPrefix, StringComparison.Ordinal)));
            var variantCount = _result.Materials.Count(m => m.IsVariant);
            var deepChainCount = _result.Materials.Count(m => m.CustomWarnings != null && m.CustomWarnings.Any(w => w.Contains(CustomWarningMessages.TokenVariantChainDepth, StringComparison.Ordinal)));
            var heavyOverrideCount = _result.Materials.Count(m => m.CustomWarnings != null && m.CustomWarnings.Any(w => w.Contains(CustomWarningMessages.TokenHeavyVariantOverrides, StringComparison.Ordinal)));
            var instancingWarn = _result.Materials.Count(m => m.CustomWarnings != null && m.CustomWarnings.Any(w => w.Contains(CustomWarningMessages.TokenGpuInstancingDisabled, StringComparison.Ordinal)));
            var srpWarn = _result.Materials.Count(m => m.CustomWarnings != null && m.CustomWarnings.Any(w => w.Contains(CustomWarningMessages.TokenSrpBatcher, StringComparison.Ordinal)));
            var uniqueReferencedTextures = _result.Materials
                .Where(m => m.ReferencedTexturePaths != null)
                .SelectMany(m => m.ReferencedTexturePaths)
                .Distinct()
                .Count();
            var sharedTextureCount = _result.Materials
                .Where(m => m.Properties != null)
                .SelectMany(m => m.Properties.Where(p => p.Type == "Texture" && p.UsedByMaterialPaths != null && p.UsedByMaterialPaths.Count > 1))
                .Select(p => p.Value)
                .Distinct()
                .Count();
            _result.OutputDescription = $"Renderers (Shown/Total): {_result.FilteredRenderers.Count} / {_result.Renderers.Count}. " +
                                        $"Materials (Shown/Total): {_result.FilteredMaterials.Count} / {_result.Materials.Count}. " +
                                        $"Duplicates: {duplicateCount}. Unused: {unusedCount}. " +
                                        $"Variants: {variantCount}. DeepChains: {deepChainCount}. HeavyVarOverrides: {heavyOverrideCount}. " +
                                        $"InstancingOff: {instancingWarn}. SRPBatcher: {srpWarn}. " +
                                        $"TexturesReferenced: {uniqueReferencedTextures}. SharedTextures: {sharedTextureCount}";
            
            EditorUtility.ClearProgressBar();
            
            Debug.Log(_result.OutputDescription);
            Debug.Log($"Material scan cache: reused={reusedMaterials}, rescanned={rescannedMaterials}, totalAssets={materialPaths.Count}");

            _hasMaterialScanResults = true;
            _analysisOngoing = false;
        }

        private void FindMaterialWarnings(MaterialAssetData materialAssetData)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialAssetData.Path);

            if (material == null)
            {
                materialAssetData.AddCustomWarning(CustomWarningMessages.UnableToLoad);
                materialAssetData.TrySetWarningLevel(2);
            }
            else
            {
                var textureNames = material.GetTexturePropertyNames();

                foreach (var textureName in textureNames)
                {
                    var texture = material.GetTexture(textureName);
                    if (texture == null)
                    {
                        if (_analysisSettings.NullTexturesAreErrors)
                        {
                            materialAssetData.AddCustomWarning(CustomWarningMessages.TextureIsNullAt(textureName));
                            materialAssetData.TrySetWarningLevel(1);
                        }
                    }
                    else
                    {
                        if (_analysisSettings.DefaultTexturesAreErrors)
                        {
                            var texturePath = AssetDatabase.GetAssetPath(texture);
                            if (texturePath != null && texturePath.Contains("unity_builtin"))
                            {
                                materialAssetData.AddCustomWarning(CustomWarningMessages.UnityBuiltinTextureAt(textureName));
                                materialAssetData.TrySetWarningLevel(2);
                            }
                        }
                    }
                }
                
                var shader = material.shader;
                if (shader == null)
                {
                    materialAssetData.AddCustomWarning(CustomWarningMessages.ShaderIsNull);
                    materialAssetData.TrySetWarningLevel(2);
                    materialAssetData.IsMissingShader = true;
                }
                else
                {
                    var shaderName = shader.name;

                    if (shaderName == "Hidden/InternalErrorShader")
                    {
                        materialAssetData.AddCustomWarning(CustomWarningMessages.ShaderInternalErrorShader);
                        materialAssetData.TrySetWarningLevel(2);
                        materialAssetData.IsMissingShader = true;
                    }
                    else if (_analysisSettings.BuiltinShadersAreErrors && IsBuiltinShader(shaderName))
                    {
                        materialAssetData.AddCustomWarning(CustomWarningMessages.BuiltInShaderLine(shaderName));
                        materialAssetData.TrySetWarningLevel(1);
                        materialAssetData.IsBuiltinShader = true;
                    }
                }
                
                if (_analysisSettings.DuplicateMaterialsAreErrors)
                {
                    materialAssetData.Fingerprint = ComputeMaterialFingerprint(material);
                }

                PopulateMaterialProperties(materialAssetData, material);
            }
        }

        private static void PopulateMaterialProperties(MaterialAssetData materialAssetData, Material material)
        {
            var shader = material.shader;
            materialAssetData.ShaderName = shader != null ? shader.name : "Unknown";
            materialAssetData.RenderQueue = material.renderQueue;
            materialAssetData.ShaderDefaultRenderQueue = shader != null ? shader.renderQueue : null;

            if (materialAssetData.HasRenderQueueOverride)
            {
                materialAssetData.AddCustomWarning(
                    CustomWarningMessages.RenderQueueOverrideLine(
                        materialAssetData.RenderQueue,
                        materialAssetData.ShaderDefaultRenderQueue));
                materialAssetData.TrySetWarningLevel(1);
            }

            var keywords = material.shaderKeywords;
            if (keywords != null && keywords.Length > 0)
            {
                materialAssetData.EnabledKeywords = keywords.OrderBy(k => k).ToList();
            }

            var properties = new List<MaterialPropertyData>();

            if (shader != null)
            {
                var propCount = ShaderUtil.GetPropertyCount(shader);
                for (var i = 0; i < propCount; i++)
                {
                    var propName = ShaderUtil.GetPropertyName(shader, i);
                    var propType = ShaderUtil.GetPropertyType(shader, i);

                    switch (propType)
                    {
                        case ShaderUtil.ShaderPropertyType.Color:
                            var color = material.GetColor(propName);
                            properties.Add(new MaterialPropertyData(propName, "Color",
                                $"({color.r:F2}, {color.g:F2}, {color.b:F2}, {color.a:F2})"));
                            break;
                        case ShaderUtil.ShaderPropertyType.Vector:
                            var vector = material.GetVector(propName);
                            properties.Add(new MaterialPropertyData(propName, "Vector",
                                $"({vector.x:F2}, {vector.y:F2}, {vector.z:F2}, {vector.w:F2})"));
                            break;
                        case ShaderUtil.ShaderPropertyType.Float:
                            properties.Add(new MaterialPropertyData(propName, "Float",
                                material.GetFloat(propName).ToString("F4")));
                            break;
                        case ShaderUtil.ShaderPropertyType.Range:
                            properties.Add(new MaterialPropertyData(propName, "Range",
                                material.GetFloat(propName).ToString("F4")));
                            break;
                        case ShaderUtil.ShaderPropertyType.TexEnv:
                            var tex = material.GetTexture(propName);
                            var texPath = tex != null ? AssetDatabase.GetAssetPath(tex) : "None";
                            var readableSize = GetAssetReadableSizeSafe(texPath);
                            properties.Add(new MaterialPropertyData(propName, "Texture", texPath, readableSize));
                            break;
                    }
                }
            }

            materialAssetData.Properties = properties;
        }

        private static string GetAssetReadableSizeSafe(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || assetPath == "None")
                return "-";
            try
            {
                var fi = new FileInfo(assetPath);
                if (!fi.Exists)
                    return "-";
                return CommonUtilities.GetReadableSize(fi.Length);
            }
            catch
            {
                return "-";
            }
        }

        private static string ComputeMaterialFingerprint(Material material)
        {
            var sb = new StringBuilder();

            var shader = material.shader;
            sb.Append("shader:");
            sb.Append(shader != null ? shader.name : "null");
            sb.Append(';');

            sb.Append("queue:");
            sb.Append(material.renderQueue);
            sb.Append(';');

            var textureNames = material.GetTexturePropertyNames();
            foreach (var textureName in textureNames)
            {
                sb.Append("tex:");
                sb.Append(textureName);
                sb.Append('=');
                var texture = material.GetTexture(textureName);
                if (texture != null)
                {
                    var texPath = AssetDatabase.GetAssetPath(texture);
                    var guid = AssetDatabase.AssetPathToGUID(texPath);
                    sb.Append(guid);
                }
                else
                {
                    sb.Append("null");
                }

                sb.Append(';');
            }

            var keywords = material.shaderKeywords;
            if (keywords != null)
            {
                var sorted = keywords.OrderBy(k => k).ToArray();
                sb.Append("keywords:");
                sb.Append(string.Join(",", sorted));
                sb.Append(';');
            }

            if (shader != null)
            {
                var propCount = ShaderUtil.GetPropertyCount(shader);
                for (var i = 0; i < propCount; i++)
                {
                    var propName = ShaderUtil.GetPropertyName(shader, i);
                    var propType = ShaderUtil.GetPropertyType(shader, i);

                    sb.Append(propName);
                    sb.Append(':');
                    sb.Append((int)propType);
                    sb.Append('=');

                    switch (propType)
                    {
                        case ShaderUtil.ShaderPropertyType.Color:
                            var color = material.GetColor(propName);
                            sb.Append($"{color.r:F6},{color.g:F6},{color.b:F6},{color.a:F6}");
                            break;
                        case ShaderUtil.ShaderPropertyType.Vector:
                            var vector = material.GetVector(propName);
                            sb.Append($"{vector.x:F6},{vector.y:F6},{vector.z:F6},{vector.w:F6}");
                            break;
                        case ShaderUtil.ShaderPropertyType.Float:
                        case ShaderUtil.ShaderPropertyType.Range:
                            sb.Append(material.GetFloat(propName).ToString("F6", System.Globalization.CultureInfo.InvariantCulture));
                            break;
                    }

                    sb.Append(';');
                }
            }

            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        private void DetectDuplicateMaterials()
        {
            var fingerprintGroups = new Dictionary<string, List<MaterialAssetData>>();

            foreach (var materialData in _result.Materials)
            {
                if (string.IsNullOrEmpty(materialData.Fingerprint))
                    continue;

                if (!fingerprintGroups.TryGetValue(materialData.Fingerprint, out var group))
                {
                    group = new List<MaterialAssetData>();
                    fingerprintGroups[materialData.Fingerprint] = group;
                }

                group.Add(materialData);
            }

            foreach (var group in fingerprintGroups.Values)
            {
                if (group.Count <= 1)
                    continue;

                foreach (var materialData in group)
                {
                    var duplicates = group.Where(m => m != materialData).ToList();
                    var pathsStr = string.Join(", ", duplicates.Select(m => m.Name));

                    materialData.AddCustomWarning(CustomWarningMessages.DuplicateOfLine(duplicates.Count, pathsStr));
                    materialData.TrySetWarningLevel(1);

                    foreach (var duplicate in duplicates)
                    {
                        materialData.AddDuplicatePath(duplicate.Path);
                    }
                }
            }
        }

        private void ApplyReferencedByPaths()
        {
            if (_materialToRendererPaths == null)
                return;

            foreach (var materialData in _result.Materials)
            {
                if (_materialToRendererPaths.TryGetValue(materialData.Path, out var paths))
                {
                    foreach (var path in paths)
                    {
                        materialData.AddReferencedByPath(path);
                    }
                }
            }
        }

        private void DetectUnusedMaterials()
        {
            foreach (var materialData in _result.Materials)
            {
                var hasRendererRefs = materialData.ReferencedByPaths != null && materialData.ReferencedByPaths.Count > 0;

                if (!hasRendererRefs && !materialData.InResources && !materialData.IsAddressable)
                {
                    materialData.AddCustomWarning(CustomWarningMessages.NotReferencedUnused);
                    materialData.TrySetWarningLevel(1);
                }
            }
        }

        private void BuildShaderUsageCounts()
        {
            _result.ShaderUsageCounts = _result.Materials
                .GroupBy(m => m.ShaderName ?? "Unknown")
                .ToDictionary(g => g.Key, g => g.Count());
        }

        private void BuildMaterialTextureCrossReference()
        {
            var textureToMaterials = new Dictionary<string, HashSet<string>>();
            var pathToMaterial = _result.Materials.ToDictionary(m => m.Path, m => m);

            foreach (var materialData in _result.Materials)
            {
                var texPaths = materialData.Properties?
                    .Where(p => p.Type == "Texture" && !string.IsNullOrEmpty(p.Value) && p.Value != "None")
                    .Select(p => p.Value)
                    .Distinct()
                    .ToList() ?? new List<string>();

                materialData.SetReferencedTexturePaths(texPaths);
                foreach (var texPath in texPaths)
                {
                    if (!textureToMaterials.TryGetValue(texPath, out var set))
                    {
                        set = new HashSet<string>();
                        textureToMaterials[texPath] = set;
                    }

                    set.Add(materialData.Path);
                }
            }

            foreach (var materialData in _result.Materials)
            {
                if (materialData.Properties == null)
                    continue;

                foreach (var prop in materialData.Properties.Where(x => x.Type == "Texture" && !string.IsNullOrEmpty(x.Value) && x.Value != "None"))
                {
                    if (!textureToMaterials.TryGetValue(prop.Value, out var users))
                    {
                        prop.UsedByMaterialPaths = new List<string>();
                        continue;
                    }

                    prop.UsedByMaterialPaths = users
                        .Where(pathToMaterial.ContainsKey)
                        .OrderBy(x => x)
                        .ToList();
                }
            }
        }

        private void AnalyzeMaterialVariantsAndPerformance()
        {
            var pathToData = new Dictionary<string, MaterialAssetData>();
            foreach (var md in _result.Materials)
            {
                pathToData[md.Path] = md;
            }

            foreach (var materialData in _result.Materials)
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(materialData.Path);
                if (mat == null)
                    continue;

                var hasVariantApi = TryGetIsMaterialVariant(mat, out var isVariantByApi);
                if (hasVariantApi)
                    materialData.IsVariant = isVariantByApi;
                if (!TryGetParentMaterial(mat, out var parentPath, out var parentLinkBroken, out var resolvedParent))
                {
                    if (hasVariantApi && isVariantByApi)
                    {
                        materialData.ParentLinkBroken = true;
                        if (_analysisSettings.VariantChainsAreErrors)
                        {
                            materialData.AddCustomWarning(CustomWarningMessages.VariantParentInvalid);
                            materialData.TrySetWarningLevel(2);
                        }
                    }
                }
                else
                {
                    materialData.IsVariant = true;
                    materialData.ParentMaterialPath = parentPath;
                    if (parentLinkBroken)
                    {
                        materialData.ParentLinkBroken = true;
                        if (_analysisSettings.VariantChainsAreErrors)
                        {
                            materialData.AddCustomWarning(CustomWarningMessages.VariantParentInvalid);
                            materialData.TrySetWarningLevel(2);
                        }
                    }
                }

                var chainDepth = ComputeVariantChainDepth(mat);
                materialData.VariantChainDepth = chainDepth;
                if (_analysisSettings.VariantChainsAreErrors && chainDepth > _analysisSettings.VariantDeepChainThreshold)
                {
                    materialData.AddCustomWarning(
                        CustomWarningMessages.VariantChainDepthLine(chainDepth, _analysisSettings.VariantDeepChainThreshold));
                    materialData.TrySetWarningLevel(1);
                }

                if (materialData.IsVariant && !materialData.ParentLinkBroken && !string.IsNullOrEmpty(materialData.ParentMaterialPath) &&
                    resolvedParent != null)
                {
                    materialData.VariantOverrideCount = ComputeVariantOverrideCount(mat, resolvedParent);
                    if (_analysisSettings.VariantHeavyOverridesAreErrors && materialData.VariantOverrideCount.HasValue &&
                        materialData.VariantOverrideCount > _analysisSettings.VariantHeavyOverridesThreshold)
                    {
                        materialData.AddCustomWarning(
                            CustomWarningMessages.HeavyVariantOverridesLine(
                                materialData.VariantOverrideCount,
                                _analysisSettings.VariantHeavyOverridesThreshold));
                        materialData.TrySetWarningLevel(1);
                    }
                }
            }

            BuildVariantHierarchyMetadata(pathToData);

            foreach (var materialData in _result.Materials)
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(materialData.Path);
                if (mat == null)
                    continue;
                var shader = mat.shader;
                materialData.GpuInstancingEnabled = mat.enableInstancing;
                materialData.SupportsGpuInstancing = TryGetGpuInstancingSupport(shader);
                if (_analysisSettings.InstancingDisabledAreErrors && materialData.SupportsGpuInstancing == true && !mat.enableInstancing)
                {
                    materialData.AddCustomWarning(CustomWarningMessages.GpuInstancingOff);
                    materialData.TrySetWarningLevel(1);
                }

                materialData.SrpBatcherCompatible = TryGetSrpBatcherCompatibility(shader);
                var sName = shader != null ? shader.name : "Unknown";
                if (_analysisSettings.SrpBatcherIncompatibleAreErrors && materialData.SrpBatcherCompatible == false)
                {
                    materialData.AddCustomWarning(CustomWarningMessages.ShaderNotSrpBatcherLine(sName));
                    materialData.TrySetWarningLevel(1);
                }
            }
        }

        /// <summary> Maps parent path → child variant paths and fills <see cref="MaterialAssetData.VariantChildrenPaths" /> on parent assets. </summary>
        private void BuildVariantHierarchyMetadata(Dictionary<string, MaterialAssetData> pathToData)
        {
            var children = new Dictionary<string, List<string>>();
            foreach (var materialData in _result.Materials)
            {
                if (string.IsNullOrEmpty(materialData.ParentMaterialPath))
                    continue;
                if (!children.TryGetValue(materialData.ParentMaterialPath, out var list))
                {
                    list = new List<string>();
                    children[materialData.ParentMaterialPath] = list;
                }

                if (!list.Contains(materialData.Path))
                    list.Add(materialData.Path);
            }

            foreach (var pair in children)
            {
                if (!pathToData.TryGetValue(pair.Key, out var parentData))
                    continue;
                foreach (var child in pair.Value)
                {
                    parentData.AddVariantChildPath(child);
                }
            }
        }

        private static bool TryGetIsMaterialVariant(Material m, out bool isVariant)
        {
            isVariant = false;
            var p = typeof(Material).GetProperty("isVariant", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.PropertyType == typeof(bool))
            {
                isVariant = (bool)p.GetValue(m);
                return true;
            }

            return false;
        }

        /// <summary> Resolves a material’s variant parent (API or serialized m_Parent). </summary>
        private static bool TryGetParentMaterial(Material m, out string parentPath, out bool parentLinkBroken, out Material parentMaterial)
        {
            parentPath = null;
            parentLinkBroken = false;
            parentMaterial = null;

            var parProp = typeof(Material).GetProperty("parent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (parProp != null)
            {
                parentMaterial = parProp.GetValue(m) as Material;
                if (parentMaterial != null)
                {
                    parentPath = AssetDatabase.GetAssetPath(parentMaterial);
                    return !string.IsNullOrEmpty(parentPath);
                }
            }

            var so = new SerializedObject(m);
            var sp = so.FindProperty("m_Parent");
            if (sp == null)
                return false;
            if (sp.objectReferenceValue is Material pm)
            {
                parentMaterial = pm;
                parentPath = AssetDatabase.GetAssetPath(pm);
                return !string.IsNullOrEmpty(parentPath);
            }

            if (sp.objectReferenceValue == null && sp.propertyType == SerializedPropertyType.ObjectReference)
            {
                parentLinkBroken = m != null && TryGetIsMaterialVariant(m, out var isV) && isV;
                return false;
            }

            return false;
        }

        private static int ComputeVariantChainDepth(Material m)
        {
            var seen = new HashSet<int>();
            var depth = 0;
            var cur = m;
            while (cur != null && seen.Add(cur.GetInstanceID()) && depth < 64)
            {
                if (!TryGetParentMaterial(cur, out var pp, out var broken, out var parent) || parent == null)
                    break;
                if (broken)
                    break;
                depth++;
                cur = parent;
            }

            return depth;
        }

        private static int ComputeVariantOverrideCount(Material child, Material parent)
        {
            if (child == null || parent == null || child.shader == null || parent.shader == null)
                return 0;
            if (child.shader != parent.shader)
                return 999;

            var count = 0;
            if (child.renderQueue != parent.renderQueue)
                count++;
            if (child.enableInstancing != parent.enableInstancing)
                count++;

            var kc = new HashSet<string>(child.shaderKeywords ?? Array.Empty<string>());
            var kp = new HashSet<string>(parent.shaderKeywords ?? Array.Empty<string>());
            if (!kc.SetEquals(kp))
                count++;

            var shader = child.shader;
            var n = ShaderUtil.GetPropertyCount(shader);
            for (var i = 0; i < n; i++)
            {
                var name = ShaderUtil.GetPropertyName(shader, i);
                var propType = ShaderUtil.GetPropertyType(shader, i);
                if (!PropertyValuesDiffer(name, propType, child, parent))
                    continue;
                count++;
            }

            return count;
        }

        private static bool PropertyValuesDiffer(string name, ShaderUtil.ShaderPropertyType propType, Material child, Material parent)
        {
            try
            {
                switch (propType)
                {
                    case ShaderUtil.ShaderPropertyType.Color:
                        return child.GetColor(name) != parent.GetColor(name);
                    case ShaderUtil.ShaderPropertyType.Vector:
                        return child.GetVector(name) != parent.GetVector(name);
                    case ShaderUtil.ShaderPropertyType.Float:
                    case ShaderUtil.ShaderPropertyType.Range:
                        return Math.Abs(child.GetFloat(name) - parent.GetFloat(name)) > 0.0001f;
                    case ShaderUtil.ShaderPropertyType.TexEnv:
                    {
                        return TexturesDifferByAssetPath(child.GetTexture(name), parent.GetTexture(name));
                    }
                    default:
                    {
                        // Some Unity versions use a different enum name for texture slots.
                        if (string.Equals(propType.ToString(), "Texture", StringComparison.Ordinal))
                        {
                            return TexturesDifferByAssetPath(child.GetTexture(name), parent.GetTexture(name));
                        }

                        return false;
                    }
                }
            }
            catch
            {
                return true;
            }
        }

        private static bool TexturesDifferByAssetPath(Texture tc, Texture tp)
        {
            if (ReferenceEquals(tc, tp))
                return false;
            if (tc == null || tp == null)
                return true;
            return AssetDatabase.GetAssetPath(tc) != AssetDatabase.GetAssetPath(tp);
        }

        private static bool? TryGetGpuInstancingSupport(Shader shader)
        {
            if (shader == null)
                return null;
            var t = typeof(ShaderUtil);
            var m = t.GetMethod("HasInstancing", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(Shader) }, null);
            if (m == null)
            {
                foreach (var cand in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (cand.Name != "HasInstancing" || !cand.ReturnType.Equals(typeof(bool)))
                        continue;
                    var ps = cand.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(Shader))
                    {
                        m = cand;
                        break;
                    }
                }
            }
            if (m == null)
                return null;
            try
            {
                return (bool)m.Invoke(null, new object[] { shader });
            }
            catch
            {
                return null;
            }
        }

        private static bool? TryGetSrpBatcherCompatibility(Shader shader)
        {
            if (shader == null)
                return null;
            var t = typeof(ShaderUtil);
            var m = t.GetMethod("IsShaderSrpBatcherCompatible", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(Shader) }, null);
            if (m == null)
                m = t.GetMethod("IsSRPBatcherShaderCompatible", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(Shader) }, null);
            if (m == null)
            {
                foreach (var cand in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!cand.Name.Contains("SrpBatcher", StringComparison.OrdinalIgnoreCase) &&
                        !cand.Name.Contains("SRPBatch", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var ps = cand.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(Shader) && cand.ReturnType == typeof(bool))
                    {
                        m = cand;
                        break;
                    }
                }
            }
            if (m == null)
                return null;
            try
            {
                return (bool)m.Invoke(null, new object[] { shader });
            }
            catch
            {
                return null;
            }
        }

        private static readonly HashSet<string> BuiltinShaderNames = new()
        {
            "Standard",
            "Standard (Specular setup)",
            "Standard (Roughness setup)",
            "Unlit/Color",
            "Unlit/Texture",
            "Unlit/Transparent",
            "Unlit/Transparent Cutout",
            "Particles/Standard Unlit",
            "Legacy Shaders/Diffuse",
            "Legacy Shaders/Specular",
            "Legacy Shaders/Bumped Diffuse",
            "Legacy Shaders/Bumped Specular",
            "Mobile/Diffuse",
            "Mobile/Unlit (Supports Lightmap)",
            "Mobile/VertexLit",
            "Mobile/VertexLit-OnlyDirectionalLights",
            "Mobile/Particles/Alpha Blended",
            "Mobile/Particles/Additive"
        };

        private static bool IsBuiltinShader(string shaderName)
        {
            return BuiltinShaderNames.Contains(shaderName);
        }

        private MaterialAssetData CreateMaterialData(string path)
        {
            var fileInfo = new FileInfo(path);
            var bytesSize = fileInfo.Length;

            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            var typeName = CommonUtilities.GetReadableTypeName(type);

            return new MaterialAssetData(path, type, typeName, bytesSize, CommonUtilities.GetReadableSize(bytesSize), _analysisSettings);
        }

        private void OnGUI()
        {
            EditorGUILayout.BeginHorizontal();
            
            GUILayout.FlexibleSpace();
            
            var prevColor = GUI.color;
            GUI.color = Color.green;

            if (!_analysisOngoing)
            {
                var postfix = _result != null && _result.Materials != null ? " (Overrides last results)" : string.Empty;
                if (GUILayout.Button($"Scan Materials {postfix}", GUILayout.Width(300f)))
                {
                    PocketEditorCoroutine.Start(PopulateMaterialAssetsList(), this);
                }
                
                postfix = _result != null && _result.Renderers != null ? " (Overrides last results)" : string.Empty;
                if (GUILayout.Button($"Scan Renderers {postfix}", GUILayout.Width(300f)))
                {
                    PocketEditorCoroutine.Start(PopulateRenderersList(), this);
                }
            }
            else
            {
                GUILayout.Label("Analysis ongoing...");
            }

            GUI.color = prevColor;
            
            GUILayout.FlexibleSpace();
            
            EditorGUILayout.EndHorizontal();

            OnSearchPatternsSettingsGUI();
            OnAnalysisSettingsGUI();
            
            GUIUtilities.HorizontalLine();

            if (_result == null || _analysisOngoing)
            {
                return;
            }
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(_result.OutputDescription);

            EditorGUILayout.EndHorizontal();

            GUIUtilities.HorizontalLine();

            _batchOperationsFoldout = EditorGUILayout.Foldout(_batchOperationsFoldout, "Batch Operations");

            if (_batchOperationsFoldout)
            {
                GUIUtilities.HorizontalLine();
                if (!_hasRendererScanResults)
                {
                    EditorGUILayout.HelpBox("Collect renderers first to enable batch operations.", MessageType.Info);
                }
                else
                {
                    _batchOperationsJustLog = EditorGUILayout.Toggle("Just log (dry run)", _batchOperationsJustLog);
                    GUIUtilities.HorizontalLine();
                    EditorGUILayout.LabelField("Apply Batch operations to currently filtered renderers or to all:");
                    _batchTargetOnlyFilteredRenderers = EditorGUILayout.Toggle(
                        "Apply to filtered", _batchTargetOnlyFilteredRenderers);
                    var batchTargetsCount = GetBatchRendererTargets().Count();
                    EditorGUILayout.LabelField(
                        $"Batch targets: {batchTargetsCount} renderer entries from " +
                        $"{(_batchTargetOnlyFilteredRenderers ? "current renderer filters" : "all scanned renderers")}");

                    GUIUtilities.HorizontalLine();
                    EditorGUILayout.LabelField("Renderer-only operation", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("Remove null material slots");
                    if (GUILayout.Button("Apply: Remove null slots", GUILayout.Width(250f)))
                    {
                        PocketEditorCoroutine.Start(RunBatchRemoveNullMaterialSlots(), this);
                    }

                    GUIUtilities.HorizontalLine();
                    if (!_hasMaterialScanResults)
                    {
                        EditorGUILayout.HelpBox("Collect materials first to enable material-based batch operations.", MessageType.Info);
                    }
                    else
                    {
                        EditorGUILayout.LabelField("Material operations", EditorStyles.boldLabel);

                        GUIUtilities.HorizontalLine();
                        EditorGUILayout.LabelField("Replace a specific material", EditorStyles.boldLabel);
                        _batchReplaceSourceMaterial = (Material)EditorGUILayout.ObjectField(
                            "Source material", _batchReplaceSourceMaterial, typeof(Material), false);
                        _batchReplaceTargetMaterial = (Material)EditorGUILayout.ObjectField(
                            "Target material", _batchReplaceTargetMaterial, typeof(Material), false);
                        if (GUILayout.Button("Apply: Replace source -> target", GUILayout.Width(300f)))
                        {
                            PocketEditorCoroutine.Start(RunBatchReplaceMaterial(_batchReplaceSourceMaterial, _batchReplaceTargetMaterial), this);
                        }

                        GUIUtilities.HorizontalLine();
                        EditorGUILayout.LabelField("Replace unity_builtin/default material references", EditorStyles.boldLabel);
                        _batchBuiltinFallbackMaterial = (Material)EditorGUILayout.ObjectField(
                            "Fallback material", _batchBuiltinFallbackMaterial, typeof(Material), false);
                        if (GUILayout.Button("Apply: Replace unity_builtin with fallback", GUILayout.Width(320f)))
                        {
                            PocketEditorCoroutine.Start(RunBatchReplaceBuiltinMaterials(_batchBuiltinFallbackMaterial), this);
                        }

                        GUIUtilities.HorizontalLine();
                        EditorGUILayout.LabelField("Fix missing shaders", EditorStyles.boldLabel);
                        _batchMissingShaderFallback = (Shader)EditorGUILayout.ObjectField(
                            "Fallback shader", _batchMissingShaderFallback, typeof(Shader), false);
                        if (GUILayout.Button("Apply: Fix missing shaders to fallback", GUILayout.Width(320f)))
                        {
                            PocketEditorCoroutine.Start(RunBatchFixMissingShaders(_batchMissingShaderFallback), this);
                        }
                    }
                }
            }

            GUIUtilities.HorizontalLine();

            if (_result.ShaderUsageCounts != null && _result.ShaderUsageCounts.Count > 0)
            {
                _shaderUsageFoldout = EditorGUILayout.Foldout(_shaderUsageFoldout,
                    $"Shader Usage Summary [{_result.ShaderUsageCounts.Count} shaders]");

                if (_shaderUsageFoldout)
                {
                    var usageSummaryGuiColor = GUI.color;
                    var sorted = _result.ShaderUsageCounts.OrderByDescending(kvp => kvp.Value).ToList();

                    foreach (var kvp in sorted)
                    {
                        var isBuiltin = IsBuiltinShader(kvp.Key);
                        var isMissing = kvp.Key == "Unknown" || kvp.Key.Contains("InternalErrorShader");

                        if (isMissing)
                            GUI.color = Color.red;
                        else if (isBuiltin)
                            GUI.color = Color.yellow;

                        EditorGUILayout.LabelField($"  {kvp.Key}: {kvp.Value} material(s)");
                        GUI.color = usageSummaryGuiColor;
                    }
                }

                GUIUtilities.HorizontalLine();
            }

            EditorGUILayout.BeginHorizontal();

            prevColor = GUI.color;
            
            var prevAlignment = GUI.skin.button.alignment;
            GUI.skin.button.alignment = TextAnchor.MiddleLeft;
            
            GUI.color = _outputSettings.TypeFilter == OutputFilterType.MaterialAssets ? Color.yellow : Color.white;
            
            if (GUILayout.Button($"[{_result.FilteredMaterials.Count}] Materials", GUILayout.Width(200f)))
            {
                _outputSettings.TypeFilter = OutputFilterType.MaterialAssets;
            }
            
            GUI.color = _outputSettings.TypeFilter == OutputFilterType.RendererComponents ? Color.yellow : Color.white;
            
            if (GUILayout.Button($"[{_result.FilteredRenderers.Count}] Renderers", GUILayout.Width(200f)))
            {
                _outputSettings.TypeFilter = OutputFilterType.RendererComponents;
            }
            
            GUI.skin.button.alignment = prevAlignment;
            GUI.color = prevColor;

            EditorGUILayout.EndHorizontal();

            GUIUtilities.HorizontalLine();
            
            EditorGUILayout.BeginHorizontal();

            var textFieldStyle = EditorStyles.textField;
            var prevTextFieldAlignment = textFieldStyle.alignment;
            textFieldStyle.alignment = TextAnchor.MiddleCenter;
            
            _outputSettings.PathFilter = EditorGUILayout.TextField("Path Contains:", 
                _outputSettings.PathFilter, GUILayout.Width(400f));

            textFieldStyle.alignment = prevTextFieldAlignment;

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Export Materials CSV", GUILayout.Width(180f)))
            {
                var outputPath = EditorUtility.SaveFilePanel(
                    "Export Materials CSV",
                    Application.dataPath,
                    "materials_hunter_materials.csv",
                    "csv");
                if (!string.IsNullOrEmpty(outputPath))
                {
                    ExportMaterialsCsv(outputPath);
                }
            }

            if (GUILayout.Button("Export Renderers CSV", GUILayout.Width(180f)))
            {
                var outputPath = EditorUtility.SaveFilePanel(
                    "Export Renderers CSV",
                    Application.dataPath,
                    "materials_hunter_renderers.csv",
                    "csv");
                if (!string.IsNullOrEmpty(outputPath))
                {
                    ExportRenderersCsv(outputPath);
                }
            }

            EditorGUILayout.EndHorizontal();
            
            GUIUtilities.HorizontalLine();

            switch (_outputSettings.TypeFilter)
            {
                case OutputFilterType.RendererComponents:
                    OnDrawRenderers(_result.FilteredRenderers, _outputSettings.PathFilter, _outputSettings.RendererComponentsSettings);
                    break;
                case OutputFilterType.MaterialAssets:
                    OnDrawMaterials(_result.FilteredMaterials, _outputSettings.PathFilter, _outputSettings.MaterialAssetsSettings);
                    break;
            }
        }

        private void OnDrawRenderers(List<RendererComponentData> renderers, string pathFilter, RendererComponentsOutputSettings settings)
        {
            if (renderers.Count == 0)
            {
                EditorGUILayout.LabelField("No renderers found");
                return;
            }

            EditorGUILayout.BeginHorizontal();
            
            var prevColor = GUI.color;

            var sortType = settings.SortType;
            
            GUI.color = sortType == 0 || sortType == 1 ? Color.yellow : Color.white;
            var orderType = sortType == 1 ? "Z-A" : "A-Z";
            if (GUILayout.Button("Sort by warnings " + orderType, GUILayout.Width(150f)))
            {
                SortRenderersByWarnings(renderers, settings);
            }
        
            GUI.color = sortType == 2 || sortType == 3 ? Color.yellow : Color.white;
            orderType = sortType == 3 ? "Z-A" : "A-Z";
            if (GUILayout.Button("Sort by path " + orderType, GUILayout.Width(150f)))
            {
                SortRenderersByPath(renderers, settings);
            }
            
            GUI.color = sortType == 4 || sortType == 5 ? Color.yellow : Color.white;
            orderType = sortType == 5 ? "Z-A" : "A-Z";
            if (GUILayout.Button("Sort by material slots " + orderType, GUILayout.Width(180f)))
            {
                SortRenderersByMaterialCount(renderers, settings);
            }

            GUI.color = sortType == 6 || sortType == 7 ? Color.yellow : Color.white;
            orderType = sortType == 7 ? "Z-A" : "A-Z";
            if (GUILayout.Button("Sort by warning count " + orderType, GUILayout.Width(180f)))
            {
                SortRenderersByWarningCount(renderers, settings);
            }
            
            GUI.color = settings.WarningsOnly ? Color.yellow : Color.white;
            if (GUILayout.Button("Warnings Level 2+ Only", GUILayout.Width(250f)))
            {
                settings.WarningsOnly = !settings.WarningsOnly;
            }
            
            GUI.color = prevColor;
            
            EditorGUILayout.EndHorizontal();
            
            var filteredAssets = GetFilteredRenderersForDisplay(renderers, pathFilter, settings);
            
            DrawPagesWidget(filteredAssets.Count, settings, ref _rendererPagesScroll);
            
            GUIUtilities.HorizontalLine();
            
            _renderersScroll = GUILayout.BeginScrollView(_renderersScroll);

            EditorGUILayout.BeginVertical();

            for (var i = 0; i < filteredAssets.Count; i++)
            {
                if (settings.PageToShow.HasValue)
                {
                    var page = settings.PageToShow.Value;
                    if (i < page * OutputSettings.PageSize || i >= (page + 1) * OutputSettings.PageSize)
                    {
                        continue;
                    }
                }
                
                var asset = filteredAssets[i];
                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button(asset.Foldout ? ">Minimize" : ">Expand", GUILayout.Width(70)))
                {
                    asset.Foldout = !asset.Foldout;
                }
                                
                prevColor = GUI.color;
                
                if (asset.WarningLevel > 2)
                    GUI.color = Color.red;
                else if (asset.WarningLevel == 2)
                    GUI.color = Color.yellow;
                else if (asset.WarningLevel == 1)
                    GUI.color = new Color(0.44f, 0.79f, 1f);
                
                EditorGUILayout.LabelField(i.ToString(), GUILayout.Width(40f));
                
                EditorGUILayout.LabelField(asset.ChildName, GUILayout.Width(150f));    
                EditorGUILayout.LabelField(asset.GameObjectName, GUILayout.Width(150f));    
                EditorGUILayout.LabelField($"Materials: {asset.MaterialSlotsCount}", GUILayout.Width(90f));
                EditorGUILayout.LabelField($"Warnings: {asset.WarningsCount}", GUILayout.Width(90f));
                
                EditorGUILayout.LabelField($"Warning: {asset.WarningLevel}", GUILayout.Width(70f));

                GUI.color = prevColor;
                
                EditorGUILayout.EndHorizontal();

                if (asset.Foldout)
                {
                    GUILayout.Space(3);
                    EditorGUILayout.LabelField($"Renderer Path: {asset.Path}");
                    
                    GUIUtilities.HorizontalLine();
                    
                    var guiContent = EditorGUIUtility.ObjectContent(null, typeof(GameObject));
                    guiContent.text = Path.GetFileName(asset.Path);

                    var alignment = GUI.skin.button.alignment;
                    GUI.skin.button.alignment = TextAnchor.MiddleLeft;

                    if (GUILayout.Button(guiContent, GUILayout.Width(300f), GUILayout.Height(18f)))
                    {
                        Selection.objects = new[] { AssetDatabase.LoadMainAssetAtPath(asset.Path) };
                    }

                    GUI.skin.button.alignment = alignment;

#if HUNT_LAYOUT                
                    if (_analysisSettings.BuildLayout != null)
                    {
                        EditorGUILayout.LabelField($"[{asset.Bundle}]", GUILayout.Width(250));
                    }
#endif
                    GUI.color = prevColor;
                    
                    GUIUtilities.HorizontalLine();
                    
                    if (asset.CustomWarnings != null)
                    {
                        EditorGUILayout.LabelField($"Warnings [{asset.CustomWarnings.Count}]:");
                        foreach (var customWarning in asset.CustomWarnings)
                        {
                            EditorGUILayout.LabelField(new GUIContent(customWarning, CustomWarningTooltips.GetTooltipOrEmpty(customWarning)));
                        }
                        
                        GUIUtilities.HorizontalLine();
                    }
                }
            }

            GUILayout.FlexibleSpace();
            
            EditorGUILayout.EndVertical();
            GUILayout.EndScrollView();
        }

        private static List<RendererComponentData> GetFilteredRenderersForDisplay(
            IEnumerable<RendererComponentData> renderers,
            string pathFilter,
            RendererComponentsOutputSettings settings)
        {
            var filtered = renderers ?? Enumerable.Empty<RendererComponentData>();

            if (settings.WarningsOnly)
            {
                filtered = filtered.Where(x => x.WarningLevel > 1);
            }

            if (!string.IsNullOrEmpty(pathFilter))
            {
                filtered = filtered.Where(x => x.Path.Contains(pathFilter));
            }

            return filtered.ToList();
        }

        private static List<MaterialAssetData> GetFilteredMaterialsForDisplay(
            IEnumerable<MaterialAssetData> materials,
            string pathFilter,
            MaterialAssetsOutputSettings settings)
        {
            var filtered = materials ?? Enumerable.Empty<MaterialAssetData>();

            if (settings.WarningsOnly)
            {
                filtered = filtered.Where(x => x.WarningLevel > 1);
            }

            if (!string.IsNullOrEmpty(pathFilter))
            {
                filtered = filtered.Where(x => x.Path.Contains(pathFilter));
            }

            return filtered.ToList();
        }

        private void ApplyDefaultWarningSorting()
        {
            _outputSettings.RendererComponentsSettings.SortType = 1;
            _result.Renderers?.Sort((a, b) => b.WarningLevel.CompareTo(a.WarningLevel));

            _outputSettings.MaterialAssetsSettings.SortType = 1;
            _result.Materials?.Sort((a, b) => b.WarningLevel.CompareTo(a.WarningLevel));
        }

        private void ExportRenderersCsv(string filePath)
        {
            var rows = GetFilteredRenderersForDisplay(
                _result.FilteredRenderers,
                _outputSettings.PathFilter,
                _outputSettings.RendererComponentsSettings);

            var sb = new StringBuilder();
            sb.AppendLine("Path,ChildName,WarningLevel,WarningsCount,MaterialSlotsCount,Warnings");
            foreach (var row in rows)
            {
                var warnings = row.CustomWarnings == null ? string.Empty : string.Join(" | ", row.CustomWarnings);
                sb.AppendLine(string.Join(",",
                    EscapeCsv(row.Path),
                    EscapeCsv(row.ChildName),
                    row.WarningLevel.ToString(),
                    row.WarningsCount.ToString(),
                    row.MaterialSlotsCount.ToString(),
                    EscapeCsv(warnings)));
            }

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
            Debug.Log($"Exported {rows.Count} renderer rows to {filePath}");
        }

        private void ExportMaterialsCsv(string filePath)
        {
            var rows = GetFilteredMaterialsForDisplay(
                _result.FilteredMaterials,
                _outputSettings.PathFilter,
                _outputSettings.MaterialAssetsSettings);

            var sb = new StringBuilder();
            sb.AppendLine("Path,Name,WarningLevel,ShaderName,RenderQueue,ReadableSize,IsVariant,VariantChainDepth,VariantOverrideCount,GpuInstancingEnabled,SupportsGpuInstancing,SrpBatcherCompatible,Warnings,ReferencedByCount,ReferencedTexturesCount");
            foreach (var row in rows)
            {
                var warnings = row.CustomWarnings == null ? string.Empty : string.Join(" | ", row.CustomWarnings);
                sb.AppendLine(string.Join(",",
                    EscapeCsv(row.Path),
                    EscapeCsv(row.Name),
                    row.WarningLevel.ToString(),
                    EscapeCsv(row.ShaderName),
                    row.RenderQueue.ToString(),
                    EscapeCsv(row.ReadableSize),
                    row.IsVariant.ToString(),
                    row.VariantChainDepth.ToString(),
                    EscapeCsv(row.VariantOverrideCount?.ToString() ?? string.Empty),
                    row.GpuInstancingEnabled.ToString(),
                    EscapeCsv(row.SupportsGpuInstancing?.ToString() ?? "Unknown"),
                    EscapeCsv(row.SrpBatcherCompatible?.ToString() ?? "Unknown"),
                    EscapeCsv(warnings),
                    (row.ReferencedByPaths?.Count ?? 0).ToString(),
                    (row.ReferencedTexturePaths?.Count ?? 0).ToString()));
            }

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
            Debug.Log($"Exported {rows.Count} material rows to {filePath}");
        }

        private static string EscapeCsv(string value)
        {
            if (value == null)
                return "\"\"";

            var escaped = value.Replace("\"", "\"\"");
            return $"\"{escaped}\"";
        }

        private void LogBatchOperationStarted(
            string operationName,
            bool apply,
            int targetPrefabs,
            int targetRendererRows,
            bool touchMaterialAssets = false)
        {
            Debug.Log(
                $"Batch operation started: {operationName}. " +
                $"apply={apply}, targetPrefabs={targetPrefabs}, targetRendererRows={targetRendererRows}, " +
                $"touchMaterialAssets={touchMaterialAssets}");
        }

        private void LogBatchOperationFinished(
            string operationName,
            int processedAssets,
            int changedAssets,
            int changedRenderers,
            int changedEntries,
            bool apply,
            string status = "completed")
        {
            Debug.Log(
                $"Batch operation finished: {operationName}. " +
                $"status={status}, processedAssets={processedAssets}, changedAssets={changedAssets}, " +
                $"changedRenderers={changedRenderers}, changes={changedEntries}, apply={apply}");
        }

        private IEnumerable<RendererComponentData> GetBatchRendererTargets()
        {
            if (_batchTargetOnlyFilteredRenderers)
            {
                return GetFilteredRenderersForDisplay(
                    _result.FilteredRenderers ?? Enumerable.Empty<RendererComponentData>(),
                    _outputSettings?.PathFilter,
                    _outputSettings?.RendererComponentsSettings ?? new RendererComponentsOutputSettings());
            }

            return _result.Renderers ?? Enumerable.Empty<RendererComponentData>();
        }

        private IEnumerator RunBatchReplaceMaterial(Material sourceMaterial, Material targetMaterial)
        {
            if (!_hasRendererScanResults)
            {
                Debug.LogWarning("Batch replace material requires renderer scan results.");
                LogBatchOperationFinished("Replace material", 0, 0, 0, 0, !_batchOperationsJustLog, "skipped");
                yield break;
            }
            if (!_hasMaterialScanResults)
            {
                Debug.LogWarning("Batch replace material requires material scan results.");
                LogBatchOperationFinished("Replace material", 0, 0, 0, 0, !_batchOperationsJustLog, "skipped");
                yield break;
            }
            if (sourceMaterial == null || targetMaterial == null)
            {
                Debug.LogWarning("Batch replace material requires both source and target materials.");
                LogBatchOperationFinished("Replace material", 0, 0, 0, 0, !_batchOperationsJustLog, "invalid-input");
                yield break;
            }

            {
                var mutation = RunBatchMutation(
                    $"Replace material \"{sourceMaterial.name}\" -> \"{targetMaterial.name}\"",
                    (renderer, sharedMaterials, apply) =>
                    {
                        var changed = false;
                        for (var i = 0; i < sharedMaterials.Length; i++)
                        {
                            if (sharedMaterials[i] != sourceMaterial)
                                continue;
                            sharedMaterials[i] = targetMaterial;
                            changed = true;
                        }

                        if (changed)
                        {
                            if (apply)
                            {
                                renderer.sharedMaterials = sharedMaterials;
                            }

                            return 1;
                        }

                        return 0;
                    });
                while (mutation.MoveNext())
                    yield return mutation.Current;
            }
        }

        private IEnumerator RunBatchReplaceBuiltinMaterials(Material fallbackMaterial)
        {
            if (!_hasRendererScanResults)
            {
                Debug.LogWarning("Batch replace unity_builtin requires renderer scan results.");
                LogBatchOperationFinished("Replace unity_builtin", 0, 0, 0, 0, !_batchOperationsJustLog, "skipped");
                yield break;
            }
            if (!_hasMaterialScanResults)
            {
                Debug.LogWarning("Batch replace unity_builtin requires material scan results.");
                LogBatchOperationFinished("Replace unity_builtin", 0, 0, 0, 0, !_batchOperationsJustLog, "skipped");
                yield break;
            }
            if (fallbackMaterial == null)
            {
                Debug.LogWarning("Batch replace unity_builtin requires a fallback material.");
                LogBatchOperationFinished("Replace unity_builtin", 0, 0, 0, 0, !_batchOperationsJustLog, "invalid-input");
                yield break;
            }

            {
                var mutation = RunBatchMutation(
                    $"Replace unity_builtin with \"{fallbackMaterial.name}\"",
                    (renderer, sharedMaterials, apply) =>
                    {
                        var changed = false;
                        for (var i = 0; i < sharedMaterials.Length; i++)
                        {
                            var mat = sharedMaterials[i];
                            if (mat == null)
                                continue;
                            var path = AssetDatabase.GetAssetPath(mat);
                            if (string.IsNullOrEmpty(path) || !path.Contains("unity_builtin"))
                                continue;
                            sharedMaterials[i] = fallbackMaterial;
                            changed = true;
                        }

                        if (changed)
                        {
                            if (apply)
                            {
                                renderer.sharedMaterials = sharedMaterials;
                            }

                            return 1;
                        }

                        return 0;
                    });
                while (mutation.MoveNext())
                    yield return mutation.Current;
            }
        }

        private IEnumerator RunBatchRemoveNullMaterialSlots()
        {
            if (!_hasRendererScanResults)
            {
                Debug.LogWarning("Batch remove null slots requires renderer scan results.");
                LogBatchOperationFinished("Remove null material slots", 0, 0, 0, 0, !_batchOperationsJustLog, "skipped");
                yield break;
            }
            {
                var mutation = RunBatchMutation(
                    "Remove null material slots",
                    (renderer, sharedMaterials, apply) =>
                    {
                        var filtered = sharedMaterials.Where(m => m != null).ToArray();
                        if (filtered.Length == sharedMaterials.Length)
                            return 0;
                        if (apply)
                        {
                            renderer.sharedMaterials = filtered;
                        }

                        return 1;
                    });
                while (mutation.MoveNext())
                    yield return mutation.Current;
            }
        }

        private IEnumerator RunBatchFixMissingShaders(Shader fallbackShader)
        {
            if (!_hasRendererScanResults)
            {
                Debug.LogWarning("Batch fix missing shaders requires renderer scan results.");
                LogBatchOperationFinished("Fix missing shaders", 0, 0, 0, 0, !_batchOperationsJustLog, "skipped");
                yield break;
            }
            if (!_hasMaterialScanResults)
            {
                Debug.LogWarning("Batch fix missing shaders requires material scan results.");
                LogBatchOperationFinished("Fix missing shaders", 0, 0, 0, 0, !_batchOperationsJustLog, "skipped");
                yield break;
            }
            if (fallbackShader == null)
            {
                Debug.LogWarning("Batch fix missing shaders requires a fallback shader.");
                LogBatchOperationFinished("Fix missing shaders", 0, 0, 0, 0, !_batchOperationsJustLog, "invalid-input");
                yield break;
            }

            var processedMaterials = new HashSet<Material>();
            {
                var mutation = RunBatchMutation(
                    $"Fix missing shaders -> \"{fallbackShader.name}\"",
                    (renderer, sharedMaterials, apply) =>
                    {
                        var changed = 0;
                        foreach (var mat in sharedMaterials)
                        {
                            if (mat == null || !processedMaterials.Add(mat))
                                continue;
                            var shader = mat.shader;
                            var isMissing = shader == null || shader.name.Contains("InternalErrorShader");
                            if (!isMissing)
                                continue;

                            if (apply)
                            {
                                mat.shader = fallbackShader;
                            }

                            changed++;
                        }

                        return changed;
                    },
                    touchMaterialAssets: true);
                while (mutation.MoveNext())
                    yield return mutation.Current;
            }
        }

        /// <summary>
        /// Callers must drive this enumerator with
        /// <c>while (m.MoveNext()) yield return m.Current;</c> — <see cref="PocketEditorCoroutine"/>
        /// does not execute nested iterators from <c>yield return RunBatchMutation(...)</c>.
        /// </summary>
        private IEnumerator RunBatchMutation(
            string operationName,
            Func<Renderer, Material[], bool, int> mutateRenderer,
            bool touchMaterialAssets = false)
        {
            if (_analysisOngoing)
            {
                LogBatchOperationFinished(operationName, 0, 0, 0, 0, !_batchOperationsJustLog, "skipped-analysis-running");
                yield break;
            }

            _analysisOngoing = true;

            var performChanges = !_batchOperationsJustLog;
            var groupedTargets = GetBatchRendererTargets()
                .GroupBy(x => x.Path)
                .ToDictionary(g => g.Key, g => new HashSet<string>(g.Select(x => x.ChildName)));

            var targetRendererRows = groupedTargets.Values.Sum(s => s.Count);
            LogBatchOperationStarted(
                operationName,
                performChanges,
                groupedTargets.Count,
                targetRendererRows,
                touchMaterialAssets);

            var touchedAssets = 0;
            var touchedRenderers = 0;
            var touchedMaterials = 0;
            var processedAssets = 0;
            var index = 0;
            var total = groupedTargets.Count;

            foreach (var entry in groupedTargets)
            {
                index++;
                processedAssets++;
                EditorUtility.DisplayProgressBar("Materials Hunter", $"Batch op: {operationName}", (float)index / Mathf.Max(1, total));
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(entry.Key);
                if (root == null)
                    continue;

                var renderers = root.GetComponentsInChildren<Renderer>(true);
                var selectedNames = entry.Value;
                var assetChanged = false;

                foreach (var renderer in renderers)
                {
                    if (!selectedNames.Contains(renderer.transform.name))
                        continue;

                    var shared = renderer.sharedMaterials ?? Array.Empty<Material>();
                    var affected = mutateRenderer(renderer, shared, performChanges);
                    if (affected <= 0)
                        continue;

                    touchedRenderers++;
                    touchedMaterials += affected;
                    assetChanged = true;

                    if (performChanges)
                    {
                        EditorUtility.SetDirty(renderer);
                        if (touchMaterialAssets)
                        {
                            foreach (var m in shared.Where(m => m != null))
                            {
                                EditorUtility.SetDirty(m);
                            }
                        }
                    }
                }

                if (assetChanged)
                {
                    touchedAssets++;
                    Debug.Log($"{(performChanges ? "[Apply]" : "[DryRun]")} {operationName} affected {entry.Key}");
                    if (performChanges)
                    {
                        EditorUtility.SetDirty(root);
                    }
                }

                if (index % 100 == 0)
                {
                    GC.Collect();
                    yield return 0.05f;
                    GC.Collect();
                }
            }

            if (performChanges)
            {
                AssetDatabase.SaveAssets();
            }

            EditorUtility.ClearProgressBar();
            LogBatchOperationFinished(
                operationName,
                processedAssets,
                touchedAssets,
                touchedRenderers,
                touchedMaterials,
                performChanges);

            _analysisOngoing = false;

            if (performChanges)
            {
                PocketEditorCoroutine.Start(RefreshAfterBatchChanges(), this);
            }
        }

        private IEnumerator RefreshAfterBatchChanges()
        {
            var renderersScan = PopulateRenderersList();
            while (renderersScan.MoveNext())
            {
                yield return renderersScan.Current;
            }

            var materialsScan = PopulateMaterialAssetsList();
            while (materialsScan.MoveNext())
            {
                yield return materialsScan.Current;
            }
        }

        private void OnDrawMaterials(List<MaterialAssetData> materials, string pathFilter, MaterialAssetsOutputSettings settings)
        {
            if (materials.Count == 0)
            {
                EditorGUILayout.LabelField("No materials found");
                return;
            }

            EditorGUILayout.BeginHorizontal();
            
            var prevColor = GUI.color;

            var sortType = settings.SortType;
            
            GUI.color = sortType == 0 || sortType == 1 ? Color.yellow : Color.white;
            var orderType = sortType == 1 ? "Z-A" : "A-Z";
            if (GUILayout.Button("Sort by warnings " + orderType, GUILayout.Width(150f)))
            {
                SortMaterialsByWarnings(materials, settings);
            }
        
            GUI.color = sortType == 2 || sortType == 3 ? Color.yellow : Color.white;
            orderType = sortType == 3 ? "Z-A" : "A-Z";
            if (GUILayout.Button("Sort by path " + orderType, GUILayout.Width(150f)))
            {
                SortMaterialsByPath(materials, settings);
            }
            
            GUI.color = sortType == 4 || sortType == 5 ? Color.yellow : Color.white;
            orderType = sortType == 5 ? "Z-A" : "A-Z";
            if (GUILayout.Button("Sort by size " + orderType, GUILayout.Width(150f)))
            {
                SortMaterialsBySize(materials, settings);
            }
            
            GUI.color = settings.WarningsOnly ? Color.yellow : Color.white;
            if (GUILayout.Button("Warnings Level 2+ Only", GUILayout.Width(250f)))
            {
                settings.WarningsOnly = !settings.WarningsOnly;
            }
            
            GUI.color = prevColor;
            
            EditorGUILayout.EndHorizontal();
            
            var filteredAssets = GetFilteredMaterialsForDisplay(materials, pathFilter, settings);

            DrawPagesWidget(filteredAssets.Count, settings, ref _materialsPagesScroll);
            
            GUIUtilities.HorizontalLine();
            
            _materialsScroll = GUILayout.BeginScrollView(_materialsScroll);

            EditorGUILayout.BeginVertical();

            for (var i = 0; i < filteredAssets.Count; i++)
            {
                if (settings.PageToShow.HasValue)
                {
                    var page = settings.PageToShow.Value;
                    if (i < page * OutputSettings.PageSize || i >= (page + 1) * OutputSettings.PageSize)
                    {
                        continue;
                    }
                }
                
                var asset = filteredAssets[i];
                DrawMaterial(i, asset);
            }

            GUILayout.FlexibleSpace();
            
            EditorGUILayout.EndVertical();
            GUILayout.EndScrollView();
        }

        private void DrawMaterial(int i, MaterialAssetData asset)
        {
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button(asset.Foldout ? "Minimize" : "Expand", GUILayout.Width(70)))
            {
                asset.Foldout = !asset.Foldout;
            }

            var prevColor = GUI.color;

            if (asset.WarningLevel > 2)
                GUI.color = Color.red;
            else if (asset.WarningLevel == 2)
                GUI.color = Color.yellow;
            else if (asset.WarningLevel == 1)
                GUI.color = new Color(0.44f, 0.79f, 1f);

            EditorGUILayout.LabelField(i.ToString(), GUILayout.Width(40f));

            EditorGUILayout.LabelField(asset.TypeName, GUILayout.Width(70f));

            EditorGUILayout.LabelField($"Warning: {asset.WarningLevel}", GUILayout.Width(70f));

            var guiContent = EditorGUIUtility.ObjectContent(null, asset.Type);
            guiContent.text = Path.GetFileName(asset.Path);

            var alignment = GUI.skin.button.alignment;
            GUI.skin.button.alignment = TextAnchor.MiddleLeft;

            if (GUILayout.Button(guiContent, GUILayout.Width(300f), GUILayout.Height(18f)))
            {
                Selection.objects = new[] { AssetDatabase.LoadMainAssetAtPath(asset.Path) };
            }

            GUI.skin.button.alignment = alignment;

            EditorGUILayout.LabelField(asset.ReadableSize, GUILayout.Width(70f));

            GUI.color = prevColor;
            
#if HUNT_LAYOUT
            if (_analysisSettings.BuildLayout != null)
            {
                EditorGUILayout.LabelField($"[{asset.Bundle}]", GUILayout.Width(250));
            }
#endif

            GUI.color = prevColor;

            EditorGUILayout.EndHorizontal();

            if (asset.Foldout)
            {
                GUILayout.Space(3);
                EditorGUILayout.LabelField($"Path: {asset.Path}");
                GUIUtilities.HorizontalLine();

                EditorGUILayout.LabelField($"Shader: {asset.ShaderName}");
                if (asset.HasRenderQueueOverride)
                {
                    var prevQueueColor = GUI.color;
                    GUI.color = Color.yellow;
                    EditorGUILayout.LabelField(
                        $"Render Queue: {asset.RenderQueue} (override, shader default: {asset.ShaderDefaultRenderQueue})");
                    GUI.color = prevQueueColor;
                }
                else
                {
                    EditorGUILayout.LabelField($"Render Queue: {asset.RenderQueue}");
                }

                GUIUtilities.HorizontalLine();
                EditorGUILayout.LabelField("Variant", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"Is variant: {asset.IsVariant}");
                if (asset.ParentLinkBroken)
                {
                    var c = GUI.color;
                    GUI.color = Color.red;
                    EditorGUILayout.LabelField("Parent: missing or invalid");
                    GUI.color = c;
                }
                else if (!string.IsNullOrEmpty(asset.ParentMaterialPath))
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Parent", GUILayout.Width(50));
                    var parentContent = EditorGUIUtility.ObjectContent(null, typeof(Material));
                    parentContent.text = Path.GetFileName(asset.ParentMaterialPath);
                    var align = GUI.skin.button.alignment;
                    GUI.skin.button.alignment = TextAnchor.MiddleLeft;
                    if (GUILayout.Button(parentContent, GUILayout.Width(300f), GUILayout.Height(18f)))
                    {
                        Selection.objects = new[] { AssetDatabase.LoadMainAssetAtPath(asset.ParentMaterialPath) };
                    }

                    GUI.skin.button.alignment = align;
                    EditorGUILayout.EndHorizontal();
                }

                var deepChain = asset.VariantChainDepth > _analysisSettings.VariantDeepChainThreshold;
                var depthColor = (deepChain && _analysisSettings.VariantChainsAreErrors) ? Color.yellow : GUI.color;
                var dPrev = GUI.color;
                GUI.color = depthColor;
                EditorGUILayout.LabelField($"Chain depth: {asset.VariantChainDepth} (warn if > {_analysisSettings.VariantDeepChainThreshold})");
                GUI.color = dPrev;
                if (asset.VariantOverrideCount.HasValue)
                {
                    var heavy = asset.VariantOverrideCount > _analysisSettings.VariantHeavyOverridesThreshold;
                    var oCol = (heavy && _analysisSettings.VariantHeavyOverridesAreErrors) ? Color.yellow : GUI.color;
                    var oPrev = GUI.color;
                    GUI.color = oCol;
                    EditorGUILayout.LabelField(
                        $"Override count vs parent: {asset.VariantOverrideCount} (warn if > {_analysisSettings.VariantHeavyOverridesThreshold})");
                    GUI.color = oPrev;
                }
                else
                {
                    EditorGUILayout.LabelField("Override count vs parent: —");
                }

                if (asset.VariantChildrenPaths != null && asset.VariantChildrenPaths.Count > 0)
                {
                    EditorGUILayout.LabelField($"Child variants [{asset.VariantChildrenPaths.Count}]:");
                    var pa = GUI.skin.button.alignment;
                    GUI.skin.button.alignment = TextAnchor.MiddleLeft;
                    foreach (var ch in asset.VariantChildrenPaths)
                    {
                        var chContent = EditorGUIUtility.ObjectContent(null, typeof(Material));
                        chContent.text = Path.GetFileName(ch);
                        if (GUILayout.Button(chContent, GUILayout.Width(300f), GUILayout.Height(18f)))
                        {
                            Selection.objects = new[] { AssetDatabase.LoadMainAssetAtPath(ch) };
                        }
                    }

                    GUI.skin.button.alignment = pa;
                }

                GUIUtilities.HorizontalLine();
                EditorGUILayout.LabelField("Performance", EditorStyles.boldLabel);
                var supInst = !asset.SupportsGpuInstancing.HasValue
                    ? "Unknown"
                    : (asset.SupportsGpuInstancing.Value ? "Yes" : "No");
                var instLine = asset.GpuInstancingEnabled ? "Enabled" : "Disabled";
                var instColor = GUI.color;
                if (_analysisSettings.InstancingDisabledAreErrors && asset.SupportsGpuInstancing == true && !asset.GpuInstancingEnabled)
                {
                    instColor = new Color(1f, 0.85f, 0.3f);
                }

                var iPrev2 = GUI.color;
                GUI.color = instColor;
                EditorGUILayout.LabelField(
                    $"GPU instancing: {instLine} (shader support: {supInst}, material: {(asset.GpuInstancingEnabled ? "on" : "off")})");
                GUI.color = iPrev2;
                var srpLine = !asset.SrpBatcherCompatible.HasValue
                    ? "Unknown"
                    : (asset.SrpBatcherCompatible.Value ? "Compatible" : "Incompatible");
                var srpColor = GUI.color;
                if (_analysisSettings.SrpBatcherIncompatibleAreErrors && asset.SrpBatcherCompatible == false)
                {
                    srpColor = new Color(1f, 0.85f, 0.3f);
                }

                var sPrev2 = GUI.color;
                GUI.color = srpColor;
                EditorGUILayout.LabelField($"SRP Batcher: {srpLine}");
                GUI.color = sPrev2;

                if (asset.EnabledKeywords != null && asset.EnabledKeywords.Count > 0)
                {
                    EditorGUILayout.LabelField($"Keywords: {string.Join(", ", asset.EnabledKeywords)}");
                }

                if (asset.Properties != null && asset.Properties.Count > 0)
                {
                    asset.PropertiesFoldout = EditorGUILayout.Foldout(
                        asset.PropertiesFoldout,
                        $"Properties [{asset.Properties.Count}]");

                    if (asset.PropertiesFoldout)
                    {
                        var prevAlignment = GUI.skin.button.alignment;
                        GUI.skin.button.alignment = TextAnchor.MiddleLeft;

                        foreach (var prop in asset.Properties)
                        {
                            if (prop.Type == "Texture")
                            {
                                EditorGUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField($"  {prop.Type} {prop.Name} =", GUILayout.Width(200f));

                                if (prop.Value != "None")
                                {
                                    var texContent = EditorGUIUtility.ObjectContent(null, typeof(Texture2D));
                                    texContent.text = $"{Path.GetFileName(prop.Value)} ({prop.ReadableSize})";

                                    if (GUILayout.Button(texContent, GUILayout.Width(300f), GUILayout.Height(18f)))
                                    {
                                        Selection.objects = new[] { AssetDatabase.LoadMainAssetAtPath(prop.Value) };
                                    }

                                    if (prop.UsedByMaterialPaths != null && prop.UsedByMaterialPaths.Count > 0)
                                    {
                                        EditorGUILayout.LabelField($"used by {prop.UsedByMaterialPaths.Count} material(s)", GUILayout.Width(180f));
                                    }
                                }
                                else
                                {
                                    EditorGUILayout.LabelField("None", GUILayout.Width(100f));
                                }

                                EditorGUILayout.EndHorizontal();
                            }
                            else
                            {
                                EditorGUILayout.LabelField($"  {prop.Type} {prop.Name} = {prop.Value}");
                            }
                        }

                        GUI.skin.button.alignment = prevAlignment;
                    }
                }

                if (asset.ReferencedTexturePaths != null && asset.ReferencedTexturePaths.Count > 0)
                {
                    GUIUtilities.HorizontalLine();
                    asset.TextureReferencesFoldout = EditorGUILayout.Foldout(
                        asset.TextureReferencesFoldout,
                        $"Texture References [{asset.ReferencedTexturePaths.Count}]:");

                    if (asset.TextureReferencesFoldout)
                    {
                        var prevAlignment = GUI.skin.button.alignment;
                        GUI.skin.button.alignment = TextAnchor.MiddleLeft;
                        for (var ti = 0; ti < asset.ReferencedTexturePaths.Count; ti++)
                        {
                            var texPath = asset.ReferencedTexturePaths[ti];
                            var prop = asset.Properties?.FirstOrDefault(p => p.Type == "Texture" && p.Value == texPath);
                            var texContent = EditorGUIUtility.ObjectContent(null, typeof(Texture2D));
                            var texSize = prop?.ReadableSize ?? GetAssetReadableSizeSafe(texPath);
                            texContent.text = $"{Path.GetFileName(texPath)} ({texSize})";
                            if (GUILayout.Button(texContent, GUILayout.Width(300f), GUILayout.Height(18f)))
                            {
                                Selection.objects = new[] { AssetDatabase.LoadMainAssetAtPath(texPath) };
                            }

                            if (prop?.UsedByMaterialPaths != null && prop.UsedByMaterialPaths.Count > 0)
                            {
                                if (!asset.TextureUsedByMaterialsFoldout.TryGetValue(texPath, out var usedByFoldout))
                                    usedByFoldout = false;
                                usedByFoldout = EditorGUILayout.Foldout(
                                    usedByFoldout,
                                    $"Used by [{prop.UsedByMaterialPaths.Count}] materials:");
                                asset.TextureUsedByMaterialsFoldout[texPath] = usedByFoldout;
                                if (usedByFoldout)
                                {
                                    foreach (var matPath in prop.UsedByMaterialPaths)
                                    {
                                        var matContent = EditorGUIUtility.ObjectContent(null, typeof(Material));
                                        matContent.text = Path.GetFileName(matPath);
                                        if (GUILayout.Button(matContent, GUILayout.Width(300f), GUILayout.Height(18f)))
                                        {
                                            Selection.objects = new[] { AssetDatabase.LoadMainAssetAtPath(matPath) };
                                        }
                                    }
                                }
                            }

                            if (ti < asset.ReferencedTexturePaths.Count - 1)
                                EditorGUILayout.Space(6f);
                        }

                        GUI.skin.button.alignment = prevAlignment;
                    }
                }

                GUIUtilities.HorizontalLine();

                if (asset.ReferencedByPaths != null && asset.ReferencedByPaths.Count > 0)
                {
                    EditorGUILayout.LabelField($"Referenced By [{asset.ReferencedByPaths.Count}]:");

                    var prevAlignment = GUI.skin.button.alignment;
                    GUI.skin.button.alignment = TextAnchor.MiddleLeft;

                    foreach (var refPath in asset.ReferencedByPaths)
                    {
                        var refContent = EditorGUIUtility.ObjectContent(null, typeof(GameObject));
                        refContent.text = Path.GetFileName(refPath);

                        if (GUILayout.Button(refContent, GUILayout.Width(300f), GUILayout.Height(18f)))
                        {
                            Selection.objects = new[] { AssetDatabase.LoadMainAssetAtPath(refPath) };
                        }
                    }

                    GUI.skin.button.alignment = prevAlignment;

                    GUIUtilities.HorizontalLine();
                }

                if (asset.CustomWarnings != null)
                {
                    EditorGUILayout.LabelField($"Warnings [{asset.CustomWarnings.Count}]:");
                    foreach (var customWarning in asset.CustomWarnings)
                    {
                        EditorGUILayout.LabelField(new GUIContent(customWarning, CustomWarningTooltips.GetTooltipOrEmpty(customWarning)));
                    }
                    
                    GUIUtilities.HorizontalLine();
                }

                if (asset.DuplicatePaths != null && asset.DuplicatePaths.Count > 0)
                {
                    EditorGUILayout.LabelField($"Duplicates [{asset.DuplicatePaths.Count}]:");

                    var prevAlignment = GUI.skin.button.alignment;
                    GUI.skin.button.alignment = TextAnchor.MiddleLeft;

                    foreach (var dupPath in asset.DuplicatePaths)
                    {
                        var dupContent = EditorGUIUtility.ObjectContent(null, typeof(Material));
                        dupContent.text = Path.GetFileName(dupPath);

                        if (GUILayout.Button(dupContent, GUILayout.Width(300f), GUILayout.Height(18f)))
                        {
                            Selection.objects = new[] { AssetDatabase.LoadMainAssetAtPath(dupPath) };
                        }
                    }

                    GUI.skin.button.alignment = prevAlignment;

                    GUIUtilities.HorizontalLine();
                }
            }
        }

        private void DrawPagesWidget(int assetsCount, IPaginationSettings settings, ref Vector2 scroll)
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            EditorGUILayout.BeginHorizontal();
            
            var prevColor = GUI.color;
            GUI.color = !settings.PageToShow.HasValue ? Color.yellow : Color.white;

            if (GUILayout.Button("All", GUILayout.Width(30f)))
            {
                settings.PageToShow = null;
            }

            GUI.color = prevColor;
            
            var totalCount = assetsCount;
            var pagesCount = totalCount / OutputSettings.PageSize + (totalCount % OutputSettings.PageSize > 0 ? 1 : 0);

            for (var i = 0; i < pagesCount; i++)
            {
                prevColor = GUI.color;
                GUI.color = settings.PageToShow == i ? Color.yellow : Color.white;

                if (GUILayout.Button((i + 1).ToString(), GUILayout.Width(30f)))
                {
                    settings.PageToShow = i;
                }

                GUI.color = prevColor;
            }

            if (settings.PageToShow.HasValue && settings.PageToShow > pagesCount - 1)
            {
                settings.PageToShow = pagesCount - 1;
            }

            if (settings.PageToShow.HasValue && pagesCount == 0)
            {
                settings.PageToShow = null;
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndScrollView();
        }

        private void OnAnalysisSettingsGUI()
        {
            EnsureAnalysisSettingsLoaded();
            
            _analysisSettingsFoldout = EditorGUILayout.Foldout(_analysisSettingsFoldout,
                "Analysis Settings.");

            if (!_analysisSettingsFoldout) 
                return;

            GUIUtilities.HorizontalLine();
            
#if HUNT_LAYOUT
            if (_analysisSettings.BuildLayout != null)
            {
                GUIUtilities.DrawColoredLabel("BuildLayout has been loaded", Color.green);
            }

            GUIUtilities.DrawAtCenterHorizontally(() =>
            {
                if (GUILayout.Button("Load BuildLayout.txt"))
                {
                    var path = EditorUtility.OpenFilePanelWithFilters("Open BuildLayout.txt", "Library", new[] { "Text Files (*.txt)", "txt" });
                    if (string.IsNullOrEmpty(path))
                        return;
                    _analysisSettings.BuildLayout = new RichBuildLayout(BuildLayout.Load(path));
                }
            }, Color.white);
#endif

            GUIUtilities.HorizontalLine();

            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            _analysisSettings.DefaultMaterialsAreErrors = EditorGUILayout.ToggleLeft("Default Materials are errors", _analysisSettings.DefaultMaterialsAreErrors);
            _analysisSettings.NullMaterialsAreErrors = EditorGUILayout.ToggleLeft("Null Materials are errors", _analysisSettings.NullMaterialsAreErrors);
            _analysisSettings.DefaultTexturesAreErrors = EditorGUILayout.ToggleLeft("Default Textures are errors", _analysisSettings.DefaultTexturesAreErrors);
            _analysisSettings.NullTexturesAreErrors = EditorGUILayout.ToggleLeft("Null Textures are errors", _analysisSettings.NullTexturesAreErrors);
            _analysisSettings.DuplicateMaterialsAreErrors = EditorGUILayout.ToggleLeft("Duplicate Materials are errors", _analysisSettings.DuplicateMaterialsAreErrors);
            _analysisSettings.UnusedMaterialsAreErrors = EditorGUILayout.ToggleLeft("Unused Materials are errors", _analysisSettings.UnusedMaterialsAreErrors);
            _analysisSettings.BuiltinShadersAreErrors = EditorGUILayout.ToggleLeft("Builtin Shaders are errors", _analysisSettings.BuiltinShadersAreErrors);
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            _analysisSettings.VariantChainsAreErrors = EditorGUILayout.ToggleLeft("Variant deep chains are errors", _analysisSettings.VariantChainsAreErrors);
            _analysisSettings.VariantHeavyOverridesAreErrors = EditorGUILayout.ToggleLeft("Heavy variant overrides are errors", _analysisSettings.VariantHeavyOverridesAreErrors);
            _analysisSettings.InstancingDisabledAreErrors = EditorGUILayout.ToggleLeft("Instancing disabled is error", _analysisSettings.InstancingDisabledAreErrors);
            _analysisSettings.SrpBatcherIncompatibleAreErrors = EditorGUILayout.ToggleLeft("SRP Batcher incompatible is error", _analysisSettings.SrpBatcherIncompatibleAreErrors);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Deep chain threshold", GUILayout.Width(150f));
            _analysisSettings.VariantDeepChainThreshold = EditorGUILayout.IntField(_analysisSettings.VariantDeepChainThreshold, GUILayout.Width(70f));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Heavy override threshold", GUILayout.Width(150f));
            _analysisSettings.VariantHeavyOverridesThreshold = EditorGUILayout.IntField(_analysisSettings.VariantHeavyOverridesThreshold, GUILayout.Width(70f));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
            
            GUIUtilities.HorizontalLine();
            
            GUILayout.Label(
                "*If you face OOM during analysis then try to lower GC parameter below");
            _analysisSettings.GarbageCollectStep = EditorGUILayout.IntField("GC once in (iterations):", _analysisSettings.GarbageCollectStep);
            
            GUILayout.Label(
                "*Below is a debug option to limit number of assets in the analysis");
            _analysisSettings.DebugLimit = EditorGUILayout.IntField("Assets Debug Limit:", _analysisSettings.DebugLimit);
        }
        
        private void OnSearchPatternsSettingsGUI()
        {
            EnsureSearchPatternsLoaded();
                
            _searchPatternsSettingsFoldout = EditorGUILayout.Foldout(_searchPatternsSettingsFoldout,
                $"Search Patterns Settings. Total Patterns Used: {_searchPatternsSettings.IgnoredPatterns.Count}.");

            if (!_searchPatternsSettingsFoldout) 
                return;
            
            EditorGUILayout.LabelField("Here you can setup a list of RegExp TO IGNORE parts of project", GUILayout.Width(370f));
            
            GUIUtilities.HorizontalLine();
            
            if (!_searchPatternsSettings.IsIgnoredPatternsAssetUsed)
            {
                EditorGUILayout.LabelField("By default we ignore following folders:", GUILayout.Width(350f));
                
                for (var i = 0; i < _searchPatternsSettings.IgnoredPatterns.Count; i++)
                {
                    EditorGUILayout.LabelField($"{i + 1}. {_searchPatternsSettings.IgnoredPatterns[i]}");
                }
                
                GUIUtilities.HorizontalLine();
                
                EditorGUILayout.LabelField("However you may override it by setting you own RegExp list in a file", GUILayout.Width(450f));

                if (GUILayout.Button("Create Settings File for Custom RegExp Patterns"))
                {
                    _searchPatternsSettings.CreateIgnoredPatternsAsset();
                }
            }
            else
            {
                if (GUILayout.Button("Open Settings File"))
                {
                    var settings = _searchPatternsSettings.IgnoredPatternsAsset;
                    Selection.activeObject = settings;
                    EditorGUIUtility.PingObject(settings);
                }
                
                if (GUILayout.Button("Delete Settings File and Reset to Defaults"))
                {
                    _searchPatternsSettings.DeleteIgnoredPatternsAsset();
                }
            }
            
            GUIUtilities.HorizontalLine();
            
            EditorGUILayout.LabelField("Please also note that any changes in this settings will be applied in the next launch", GUILayout.Width(650f));
            
            GUIUtilities.HorizontalLine();
        }

        private void EnsureAnalysisSettingsLoaded()
        {
            // ReSharper disable once ConvertIfStatementToNullCoalescingAssignment
            if (_analysisSettings == null)
            {
                _analysisSettings = new AnalysisSettings();
            }
        }

        private void EnsureSearchPatternsLoaded()
        {
            // ReSharper disable once ConvertIfStatementToNullCoalescingAssignment
            if (_searchPatternsSettings == null)
            {
                _searchPatternsSettings = new SearchPatternsSettings();
            }
            
            if (_searchPatternsSettings.TriedLoadingIgnoredPatterns)
                return;

            _searchPatternsSettings.TryLoadIgnoredPatternsAsset();
        }
        
        private static void SortRenderersByWarnings(List<RendererComponentData> atlases, RendererComponentsOutputSettings settings)
        {
            if (settings.SortType == 0)
            {
                settings.SortType = 1;
                atlases?.Sort((a, b) =>
                    b.WarningLevel.CompareTo(a.WarningLevel));
            }
            else
            {
                settings.SortType = 0;
                atlases?.Sort((a, b) =>
                    a.WarningLevel.CompareTo(b.WarningLevel));
            }
        }
        
        private static void SortRenderersByPath(List<RendererComponentData> renderers, RendererComponentsOutputSettings settings)
        {
            if (settings.SortType == 2)
            {
                settings.SortType = 3;
                renderers?.Sort((a, b) =>
                    string.Compare(b.Path, a.Path, StringComparison.Ordinal));
            }
            else
            {
                settings.SortType = 2;
                renderers?.Sort((a, b) =>
                    string.Compare(a.Path, b.Path, StringComparison.Ordinal));
            }
        }

        private static void SortRenderersByMaterialCount(List<RendererComponentData> renderers, RendererComponentsOutputSettings settings)
        {
            if (settings.SortType == 4)
            {
                settings.SortType = 5;
                renderers?.Sort((a, b) =>
                {
                    var byCount = b.MaterialSlotsCount.CompareTo(a.MaterialSlotsCount);
                    if (byCount != 0)
                        return byCount;
                    return string.Compare(a.Path, b.Path, StringComparison.Ordinal);
                });
            }
            else
            {
                settings.SortType = 4;
                renderers?.Sort((a, b) =>
                {
                    var byCount = a.MaterialSlotsCount.CompareTo(b.MaterialSlotsCount);
                    if (byCount != 0)
                        return byCount;
                    return string.Compare(a.Path, b.Path, StringComparison.Ordinal);
                });
            }
        }

        private static void SortRenderersByWarningCount(List<RendererComponentData> renderers, RendererComponentsOutputSettings settings)
        {
            if (settings.SortType == 6)
            {
                settings.SortType = 7;
                renderers?.Sort((a, b) =>
                {
                    var byCount = b.WarningsCount.CompareTo(a.WarningsCount);
                    if (byCount != 0)
                        return byCount;
                    return string.Compare(a.Path, b.Path, StringComparison.Ordinal);
                });
            }
            else
            {
                settings.SortType = 6;
                renderers?.Sort((a, b) =>
                {
                    var byCount = a.WarningsCount.CompareTo(b.WarningsCount);
                    if (byCount != 0)
                        return byCount;
                    return string.Compare(a.Path, b.Path, StringComparison.Ordinal);
                });
            }
        }
        
        private static void SortMaterialsByWarnings(List<MaterialAssetData> textures, MaterialAssetsOutputSettings settings)
        {
            if (settings.SortType == 0)
            {
                settings.SortType = 1;
                textures?.Sort((a, b) =>
                    b.WarningLevel.CompareTo(a.WarningLevel));
            }
            else
            {
                settings.SortType = 0;
                textures?.Sort((a, b) =>
                    a.WarningLevel.CompareTo(b.WarningLevel));
            }
        }
        
        private static void SortMaterialsByPath(List<MaterialAssetData> textures, MaterialAssetsOutputSettings settings)
        {
            if (settings.SortType == 2)
            {
                settings.SortType = 3;
                textures?.Sort((a, b) =>
                    string.Compare(b.Path, a.Path, StringComparison.Ordinal));
            }
            else
            {
                settings.SortType = 2;
                textures?.Sort((a, b) =>
                    string.Compare(a.Path, b.Path, StringComparison.Ordinal));
            }
        }

        private static void SortMaterialsBySize(List<MaterialAssetData> textures, MaterialAssetsOutputSettings settings)
        {
            if (settings.SortType == 4)
            {
                settings.SortType = 5;
                textures?.Sort((b, a) => a.BytesSize.CompareTo(b.BytesSize));
            }
            else
            {
                settings.SortType = 4;
                textures?.Sort((a, b) => a.BytesSize.CompareTo(b.BytesSize));
            }
        }
    }
    
    public static class GUIUtilities
    {
        private static void HorizontalLine(
            int marginTop,
            int marginBottom,
            int height,
            Color color
        )
        {
            EditorGUILayout.BeginHorizontal();
            var rect = EditorGUILayout.GetControlRect(
                false,
                height,
                new GUIStyle { margin = new RectOffset(0, 0, marginTop, marginBottom) }
            );

            EditorGUI.DrawRect(rect, color);
            EditorGUILayout.EndHorizontal();
        }

        public static void HorizontalLine(
            int marginTop = 5,
            int marginBottom = 5,
            int height = 2
        )
        {
            HorizontalLine(marginTop, marginBottom, height, new Color(0.5f, 0.5f, 0.5f, 1));
        }
        
        public static void DrawColoredLabel(string text, Color color)
        {
            var prevColor = GUI.color;
            GUI.color = color;
            GUILayout.Label(text);
            GUI.color = prevColor;
        }
        
        public static void DrawAtCenterHorizontally(Action toDraw, Color color)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            var prevColor = GUI.color;
            GUI.color = color;
            toDraw();
            GUI.color = prevColor;
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }
    }

    public static class CommonUtilities
    {
        public static string GetFullName(Transform transform)
        {
            if (transform == null)
                return string.Empty;

            var name = string.Empty;

            var target = transform;

            while (target != null)
            {
                name += target.name + ">";
                target = target.parent;
            }
            
            return name;
        }
        
        public static string GetReadableTypeName(Type type)
        {
            string typeName;

            if (type != null)
            {
                typeName = type.ToString();
                typeName = typeName.Replace("UnityEngine.", string.Empty);
                typeName = typeName.Replace("UnityEditor.", string.Empty);
            }
            else
            {
                typeName = "Unknown Type";
            }

            return typeName;
        }

        public static string GetReadableSize(long bytesSize)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytesSize;
            var order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }

            return $"{len:0.##} {sizes[order]}";
        }
        
        public static bool IsAssetAddressable(string assetPath)
        {
#if HUNT_ADDRESSABLES
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                return false;
            var entry = settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(assetPath));
            return entry != null;
#else
            return false;
#endif
        }
    }
    
    internal class PocketEditorCoroutine
    {
        private readonly bool _hasOwner;
        private readonly WeakReference _ownerReference;
        private IEnumerator _routine;
        private double? _lastTimeWaitStarted;

        public static PocketEditorCoroutine Start(IEnumerator routine, EditorWindow owner = null)
        {
            return new PocketEditorCoroutine(routine, owner);
        }
        
        private PocketEditorCoroutine(IEnumerator routine, EditorWindow owner = null)
        {
            _routine = routine ?? throw new ArgumentNullException(nameof(routine));
            EditorApplication.update += OnUpdate;
            if (owner == null) return;
            _ownerReference = new WeakReference(owner);
            _hasOwner = true;
        }

        private void Stop()
        {
            EditorApplication.update -= OnUpdate;
            _routine = null;
        }
        
        private void OnUpdate()
        {
            if (_hasOwner && _ownerReference is null or { IsAlive: false })
            {
                Stop();
                return;
            }
            
            var result = MoveNext(_routine);
            if (!result.HasValue || result.Value) return;
            Stop();
        }

        private bool? MoveNext(IEnumerator enumerator)
        {
            if (enumerator.Current is not float current) 
                return enumerator.MoveNext();
            
            _lastTimeWaitStarted ??= EditorApplication.timeSinceStartup;
            
            if (!(_lastTimeWaitStarted.Value + current
                  <= EditorApplication.timeSinceStartup))
                return null;

            _lastTimeWaitStarted = null;
            return enumerator.MoveNext();
        }
    }
}