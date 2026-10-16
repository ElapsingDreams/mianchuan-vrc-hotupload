using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

// Read-only inspection for an already built VRChat World bundle.
// This deliberately does not mutate the bundle or manufacture a world signature.
static class WorldPreflightTool
{
    const string WorldIdPattern = "wrld_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";
    const uint StandaloneWindows64 = 19;

    static int errors;
    static int warnings;
    static int buildRequired;
    static int infos;

    sealed class SerializedEntryResult
    {
        public string Name;
        public string UnityVersion;
        public uint Platform;
        public int GameObjects;
        public int MonoBehaviours;
        public int AssetBundles;
        public int SceneDescriptors;
        public int PipelineManagers;
        public int UdonSignatureCandidates;
        public int UdonSignaturesPresent;
        public int PipelineCompletionFields;
        public int CompletedPipelines;
        public readonly HashSet<string> WorldIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> BundleNames = new List<string>();
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length < 1)
        {
            Emit("ERROR", "ARGS", "Usage: WorldPreflightTool <input.vrcw> [--world-id wrld_xxx]");
            EmitSummary();
            return 2;
        }

        string path = args[0];
        string expectedWorldId = "";
        bool quick = false;
        for (int i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--quick", StringComparison.OrdinalIgnoreCase))
                quick = true;
            else if (string.Equals(args[i], "--world-id", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                expectedWorldId = args[++i];
        }

        try
        {
            Run(path, expectedWorldId, quick);
        }
        catch (Exception ex)
        {
            Emit("ERROR", "FATAL", ex.GetType().Name + ": " + ex.Message);
        }

        EmitSummary();
        return errors > 0 || buildRequired > 0 ? 2 : 0;
    }

    static void Run(string path, string expectedWorldId, bool quick)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Emit("ERROR", "FILE_MISSING", "World bundle was not found: " + path);
            return;
        }

        string extension = Path.GetExtension(path);
        if (!string.Equals(extension, ".vrcw", StringComparison.OrdinalIgnoreCase))
            Emit("WARN", "EXTENSION", "Expected .vrcw, received " + extension);

        FileInfo fileInfo = new FileInfo(path);
        Emit("INFO", "FILE_SIZE", "Download size=" + FormatBytes(fileInfo.Length));
        Emit("INFO", "SIGNATURE_SOURCE", "worldSignature is not read from this bundle; official SDK supplies it from the build callback/session state.");
        Emit("WARN", "WORLD_SIGNATURE_EXTERNAL", "No external worldSignature was supplied; the upload pipeline must validate signature requirements before creating or updating the world record.");

        string tpk = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "class.tpk");
        if (!File.Exists(tpk))
        {
            Emit("ERROR", "CLASSPACKAGE_MISSING", "Missing class.tpk; serialized Unity objects cannot be inspected.");
            return;
        }

        AssetsManager manager = new AssetsManager();
        BundleFileInstance bundle = quick ? manager.LoadBundleFile(path, false) : manager.LoadBundleFile(path);
        try
        {
            AssetBundleFile assetBundle = bundle.file;
            AssetBundleFSHeaderFlags flags = assetBundle.Header.FileStreamHeader.Flags;
            Emit("INFO", "BUNDLE_HEADER", "HeaderVersion=" + assetBundle.Header.Version + ", EngineVersion=" + (assetBundle.Header.EngineVersion ?? ""));
            Emit("INFO", "COMPRESSION", "Compression=" + bundle.originalCompression + ", BlockDirAtEnd=" + HasFlag(flags, AssetBundleFSHeaderFlags.BlockAndDirAtEnd) + ", Flags=" + (int)flags);
            Emit("INFO", "UNCOMPRESSED_SIZE", "Estimated uncompressed size=" + FormatBytes(EstimateUncompressedSize(assetBundle)));

            manager.LoadClassPackage(tpk);
            manager.LoadClassDatabaseFromPackage(assetBundle.Header.EngineVersion);

            List<SerializedEntryResult> parsed = new List<SerializedEntryResult>();
            int directoryCount = assetBundle.BlockAndDirInfo == null || assetBundle.BlockAndDirInfo.DirectoryInfos == null
                ? 0 : assetBundle.BlockAndDirInfo.DirectoryInfos.Count;
            Emit("INFO", "DIRECTORY_COUNT", "Bundle directory entries=" + directoryCount);

            if (directoryCount == 0)
            {
                Emit("ERROR", "DIRECTORY_EMPTY", "The world bundle has no directory entries.");
                return;
            }

            for (int index = 0; index < directoryCount; index++)
            {
                var directory = assetBundle.BlockAndDirInfo.DirectoryInfos[index];
                string name = directory.Name ?? "";
                Emit("INFO", "DIRECTORY", "index=" + index + ", name=" + name + ", decompressed=" + FormatBytes(directory.DecompressedSize));

                if (quick || IsResourceEntry(name))
                    continue;

                try
                {
                    AssetsFileInstance assets = manager.LoadAssetsFileFromBundle(bundle, index, true);
                    SerializedEntryResult result = InspectSerializedEntry(manager, assets, name);
                    parsed.Add(result);
                    EmitEntrySummary(result);
                }
                catch (Exception ex)
                {
                    Emit("WARN", "SERIALIZED_ENTRY_SKIPPED", "index=" + index + ", name=" + name + ", reason=" + ex.GetType().Name + ": " + ex.Message);
                }
            }

            if (quick)
            {
                Emit("INFO", "QUICK_MODE", "Quick mode skipped serialized object inspection.");
                return;
            }

            EvaluateWorldStructure(parsed, expectedWorldId);
        }
        finally
        {
            try { bundle.file.Close(); } catch { }
        }
    }

    static SerializedEntryResult InspectSerializedEntry(AssetsManager manager, AssetsFileInstance assets, string name)
    {
        SerializedEntryResult result = new SerializedEntryResult();
        result.Name = name;
        result.UnityVersion = assets.file.Metadata.UnityVersion ?? "";
        result.Platform = assets.file.Metadata.TargetPlatform;

        result.GameObjects = CountType(assets, 1);
        result.MonoBehaviours = CountType(assets, 114);
        result.AssetBundles = CountType(assets, 142);

        foreach (var info in assets.file.GetAssetsOfType((AssetClassID)142))
        {
            try
            {
                AssetTypeValueField field = manager.GetBaseField(assets, info);
                string bundleName = ReadString(field["m_AssetBundleName"]);
                if (string.IsNullOrEmpty(bundleName)) bundleName = ReadString(field["m_Name"]);
                if (!string.IsNullOrEmpty(bundleName)) result.BundleNames.Add(bundleName);
            }
            catch { }
        }

        foreach (var info in assets.file.GetAssetsOfType((AssetClassID)114))
        {
            AssetTypeValueField field;
            try { field = manager.GetBaseField(assets, info); }
            catch { continue; }

            ScanFieldForWorldIds(field, result.WorldIds);
            if (LooksLikeUdonSignatureHolder(field))
            {
                result.UdonSignatureCandidates++;
                if (HasField(field, "serializedSignature")) result.UdonSignaturesPresent++;
            }
            if (HasField(field, "blueprintId"))
            {
                string blueprint = ReadString(field["blueprintId"]);
                if (IsWorldId(blueprint))
                {
                    result.PipelineManagers++;
                    AssetTypeValueField completed = field["completedSDKPipeline"];
                    if (completed != null && completed.FieldName != "DUMMY")
                    {
                        result.PipelineCompletionFields++;
                        if (completed.AsInt == 1) result.CompletedPipelines++;
                    }
                    result.WorldIds.Add(blueprint);
                }
            }

            if (LooksLikeSceneDescriptor(field))
                result.SceneDescriptors++;
        }

        return result;
    }

    static void EvaluateWorldStructure(List<SerializedEntryResult> entries, string expectedWorldId)
    {
        if (entries.Count == 0)
        {
            Emit("ERROR", "NO_SERIALIZED_ASSETS", "No serialized Unity asset file could be parsed from the world bundle.");
            return;
        }

        HashSet<string> worldIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int pipelineManagers = 0;
        int descriptors = 0;
        int udonCandidates = 0;
        int udonSignatures = 0;
        int completedPipelines = 0;
        int completionFields = 0;
        int totalAssetBundles = 0;
        string unityVersion = "";
        uint platform = 0;

        foreach (SerializedEntryResult entry in entries)
        {
            foreach (string id in entry.WorldIds) worldIds.Add(id);
            pipelineManagers += entry.PipelineManagers;
            descriptors += entry.SceneDescriptors;
            udonCandidates += entry.UdonSignatureCandidates;
            udonSignatures += entry.UdonSignaturesPresent;
            completionFields += entry.PipelineCompletionFields;
            completedPipelines += entry.CompletedPipelines;
            totalAssetBundles += entry.AssetBundles;
            if (string.IsNullOrEmpty(unityVersion)) unityVersion = entry.UnityVersion;
            if (platform == 0) platform = entry.Platform;
        }

        Emit("INFO", "ASSETS_SUMMARY", "serializedFiles=" + entries.Count + ", GameObject=" + Sum(entries, "go") + ", MonoBehaviour=" + Sum(entries, "mb") + ", AssetBundle=" + totalAssetBundles);
        Emit("INFO", "WORLD_IDS", worldIds.Count == 0 ? "(none)" : Join(worldIds));
        Emit("INFO", "WORLD_COMPONENTS", "PipelineManager=" + pipelineManagers + ", SceneDescriptor=" + descriptors +
             ", completedSDKPipelineFields=" + completionFields + ", completedSDKPipeline=" + completedPipelines);
        Emit("INFO", "UDON_SIGNATURES", "signatureCandidates=" + udonCandidates + ", signaturesPresent=" + udonSignatures);
        Emit("INFO", "WORLD_METADATA", "UnityVersion=" + unityVersion + ", TargetPlatform=" + platform);

        if (platform != 0 && platform != StandaloneWindows64)
            Emit("WARN", "TARGET_PLATFORM", "Target platform is not StandaloneWindows64: " + platform);
        if (pipelineManagers == 0)
            Emit("ERROR", "PIPELINE_MANAGER_MISSING", "No unique world PipelineManager with a wrld_ blueprintId was found.");
        else if (pipelineManagers > 1)
            Emit("ERROR", "MULTIPLE_PIPELINE_MANAGERS", "Official Worlds SDK rejects multiple PipelineManager components: " + pipelineManagers);
        if (descriptors == 0)
            Emit("ERROR", "SCENE_DESCRIPTOR_MISSING", "No VRCSceneDescriptor-like component was found.");
        else if (descriptors > 1)
            Emit("WARN", "MULTIPLE_SCENE_DESCRIPTORS", "More than one SceneDescriptor-like component was found: " + descriptors);
        if (worldIds.Count > 1)
            Emit("ERROR", "MULTIPLE_WORLD_IDS", "Multiple distinct wrld_ IDs were found: " + Join(worldIds));
        if (!string.IsNullOrEmpty(expectedWorldId) && !worldIds.Contains(expectedWorldId))
            Emit("ERROR", "WORLD_ID_MISMATCH", "Expected world ID was not found: " + expectedWorldId);
        if (completionFields == 0)
            Emit("INFO", "SDK_PIPELINE_MARKER_NOT_APPLICABLE", "World PipelineManager does not expose the Avatar-only completedSDKPipeline marker.");
        else if (completedPipelines == 0)
            Emit("INFO", "SDK_PIPELINE_MARKER_ZERO", "World build records may legitimately keep completedSDKPipeline=0 before the remote upload completes; this is not a World build failure by itself.");
        else
            Emit("INFO", "SDK_PIPELINE_COMPLETED", "completedSDKPipeline=1 count=" + completedPipelines);
        if (totalAssetBundles == 0)
            Emit("ERROR", "ASSETBUNDLE_MISSING", "No Unity AssetBundle metadata object was found.");
        if (udonCandidates > 0 && udonSignatures < udonCandidates)
            Emit("WARN", "UDON_SIGNATURES_INCOMPLETE", "Some Udon signature holder candidates do not expose a serialized signature; inspect before any rebuild.");
    }

    static int Sum(List<SerializedEntryResult> entries, string kind)
    {
        int total = 0;
        foreach (SerializedEntryResult entry in entries)
        {
            if (kind == "go") total += entry.GameObjects;
            else if (kind == "mb") total += entry.MonoBehaviours;
        }
        return total;
    }

    static bool LooksLikeSceneDescriptor(AssetTypeValueField field)
    {
        int score = 0;
        string[] names = new[]
        {
            "spawns", "spawnRadius", "spawnOrder", "spawnOrientation", "ReferenceCamera",
            "RespawnHeightY", "ObjectBehaviourAtRespawnHeight", "DynamicPrefabs", "DynamicMaterials",
            "NetworkIDs", "interactThruLayers"
        };
        foreach (string name in names)
            if (HasField(field, name)) score++;
        return score >= 3;
    }

    static bool LooksLikeUdonSignatureHolder(AssetTypeValueField field)
    {
        return HasField(field, "serializedProgramBytesString") ||
               HasField(field, "serializedProgramCompressedBytes") ||
               HasField(field, "serializedProgramAsset");
    }

    static void ScanFieldForWorldIds(AssetTypeValueField field, HashSet<string> ids)
    {
        if (field == null) return;
        if (string.Equals(field.TypeName, "string", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string value = field.AsString;
                if (!string.IsNullOrEmpty(value))
                {
                    foreach (Match match in Regex.Matches(value, WorldIdPattern))
                        ids.Add(match.Value);
                }
            }
            catch { }
        }
        if (field.Children == null) return;
        foreach (AssetTypeValueField child in field.Children)
            ScanFieldForWorldIds(child, ids);
    }

    static bool IsResourceEntry(string name)
    {
        string lower = (name ?? "").ToLowerInvariant();
        return lower.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) ||
               lower.EndsWith(".resource", StringComparison.OrdinalIgnoreCase) ||
               lower.EndsWith(".ress", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsWorldId(string value)
    {
        return !string.IsNullOrEmpty(value) && Regex.IsMatch(value, "^" + WorldIdPattern + "$", RegexOptions.IgnoreCase);
    }

    static bool HasField(AssetTypeValueField field, string name)
    {
        if (field == null) return false;
        AssetTypeValueField child = field[name];
        return child != null && child.FieldName != "DUMMY";
    }

    static string ReadString(AssetTypeValueField field)
    {
        if (field == null || field.FieldName == "DUMMY") return "";
        try { return field.AsString ?? ""; } catch { return ""; }
    }

    static int CountType(AssetsFileInstance assets, int classId)
    {
        int count = 0;
        foreach (var ignored in assets.file.GetAssetsOfType((AssetClassID)classId)) count++;
        return count;
    }

    static long EstimateUncompressedSize(AssetBundleFile bundle)
    {
        long directoryTotal = 0;
        long blockTotal = 0;
        if (bundle.BlockAndDirInfo != null && bundle.BlockAndDirInfo.DirectoryInfos != null)
            foreach (var dir in bundle.BlockAndDirInfo.DirectoryInfos) directoryTotal += dir.DecompressedSize;
        if (bundle.BlockAndDirInfo != null && bundle.BlockAndDirInfo.BlockInfos != null)
            foreach (var block in bundle.BlockAndDirInfo.BlockInfos) blockTotal += block.DecompressedSize;
        return Math.Max(directoryTotal, blockTotal);
    }

    static bool HasFlag(AssetBundleFSHeaderFlags flags, AssetBundleFSHeaderFlags flag)
    {
        return (((int)flags) & (int)flag) != 0;
    }

    static void EmitEntrySummary(SerializedEntryResult result)
    {
        Emit("INFO", "SERIALIZED_ENTRY", "name=" + result.Name + ", UnityVersion=" + result.UnityVersion + ", Platform=" + result.Platform +
             ", GameObject=" + result.GameObjects + ", MonoBehaviour=" + result.MonoBehaviours + ", AssetBundle=" + result.AssetBundles +
             ", SceneDescriptor=" + result.SceneDescriptors + ", PipelineManager=" + result.PipelineManagers +
             ", UdonSignatureCandidates=" + result.UdonSignatureCandidates + ", UdonSignatures=" + result.UdonSignaturesPresent +
             ", worldIds=" + (result.WorldIds.Count == 0 ? "(none)" : Join(result.WorldIds)));
        foreach (string bundleName in result.BundleNames)
            Emit("INFO", "ASSETBUNDLE_NAME", "name=" + bundleName);
    }

    static string Join(IEnumerable<string> values)
    {
        StringBuilder builder = new StringBuilder();
        foreach (string value in values)
        {
            if (builder.Length > 0) builder.Append(", ");
            builder.Append(value);
        }
        return builder.Length == 0 ? "(none)" : builder.ToString();
    }

    static string FormatBytes(long bytes)
    {
        return (bytes / 1048576.0).ToString("F1") + " MB";
    }

    static void Emit(string level, string code, string message)
    {
        if (level == "ERROR") errors++;
        else if (level == "WARN") warnings++;
        else if (level == "BUILD_REQUIRED") buildRequired++;
        else infos++;
        Console.WriteLine(level + "|" + code + "|" + message);
    }

    static void EmitSummary()
    {
        Console.WriteLine("SUMMARY|errors=" + errors + "|buildRequired=" + buildRequired + "|warnings=" + warnings + "|infos=" + infos);
    }
}
