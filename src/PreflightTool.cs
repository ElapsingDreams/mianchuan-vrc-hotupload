using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

static class PreflightTool
{
    const long MaxInputBytes = 600L * 1024 * 1024;
    const long MaxDownloadBytes = 200L * 1024 * 1024;
    const long MaxUncompressedBytes = 500L * 1024 * 1024;
    const long WarnUncompressedBytes = 450L * 1024 * 1024;
    const string ExpectedUnityVersion = "2022.3.22f1";
    const uint ExpectedPlatform = 19; // StandaloneWindows64 in Unity asset metadata.

    static int _errors;
    static int _repairable;
    static int _buildRequired;
    static int _warnings;
    static int _infos;

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length < 1)
        {
            Emit("ERROR", "ARGS", "用法: PreflightTool <avatar.vrca> [--input] [--expected-avatar avtr_xxx] [--quick]");
            EmitSummary();
            return 2;
        }

        var path = args[0];
        var expectedAvatar = "";
        var quick = false;
        var inputMode = false;
        for (var i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--quick", StringComparison.OrdinalIgnoreCase))
                quick = true;
            else if (string.Equals(args[i], "--input", StringComparison.OrdinalIgnoreCase))
                inputMode = true;
            else if (string.Equals(args[i], "--expected-avatar", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                expectedAvatar = args[++i];
            else
            {
                Emit("ERROR", "ARGS", "未知或缺少值的参数: " + args[i]);
                EmitSummary();
                return 2;
            }
        }

        try
        {
            if (inputMode && !string.IsNullOrEmpty(expectedAvatar))
                throw new ArgumentException("目标 Avatar ID 校验必须使用最终产物模式，不能使用 --input。");
            Run(path, expectedAvatar, quick, inputMode);
        }
        catch (Exception ex)
        {
            Emit("ERROR", "FATAL", ex.GetType().Name + ": " + ex.Message);
        }

        EmitSummary();
        if (_errors > 0 || _buildRequired > 0) return 2;
        if (_repairable > 0) return 3;
        return 0;
    }

    static void Run(string path, string expectedAvatar, bool quick, bool inputMode)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Emit("ERROR", "FILE_MISSING", "找不到 VRCA 文件: " + path);
            return;
        }

        var file = new FileInfo(path);
        var fileLimit = inputMode ? MaxInputBytes : MaxDownloadBytes;
        Emit("INFO", "SIZE_STAGE", inputMode ? "本地输入准入；最终包仍需满足 PC 200MB 下载 / 500MB 解压限制。" : "最终上传产物；PC 200MB 下载 / 500MB 解压限制。");
        Emit("INFO", "FILE_SIZE", (inputMode ? "输入文件大小: " : "下载大小: ") + FormatBytes(file.Length));
        if (file.Length > fileLimit)
        {
            Emit("ERROR", inputMode ? "INPUT_SIZE_LIMIT" : "DOWNLOAD_SIZE_LIMIT",
                inputMode ? "输入超过本地 600MB 处理限额: " + FormatBytes(file.Length) : "最终包超过 PC 200MB 下载限制，需要进一步压缩或优化资源: " + FormatBytes(file.Length));
            return;
        }
        if (file.Length > fileLimit * 9 / 10)
            Emit("WARN", inputMode ? "INPUT_SIZE_NEAR_LIMIT" : "DOWNLOAD_SIZE_NEAR_LIMIT", "文件接近当前阶段限额: " + FormatBytes(file.Length));

        if (!string.Equals(file.Extension, ".vrca", StringComparison.OrdinalIgnoreCase))
            Emit("WARN", "EXTENSION", "文件扩展名不是 .vrca: " + file.Extension);

        var tpk = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "class.tpk");
        if (!File.Exists(tpk) && !quick)
            Emit("ERROR", "CLASSPACKAGE_MISSING", "缺少 class.tpk，无法完整解析 AssetBundle。");

        var probeManager = new AssetsManager();
        var probeBundle = probeManager.LoadBundleFile(path, false);
        try
        {
            var bundle = probeBundle.file;
            var flags = bundle.Header.FileStreamHeader.Flags;
            var compression = probeBundle.originalCompression;
            var blockDirAtEnd = HasFlag(flags, AssetBundleFSHeaderFlags.BlockAndDirAtEnd);
            var engineVersion = bundle.Header.EngineVersion ?? "";
            var uncompressed = EstimateUncompressedSize(bundle);
            ValidateBundleRanges(bundle, file.Length);

            Emit("INFO", "BUNDLE_HEADER", "HeaderVersion=" + bundle.Header.Version + ", EngineVersion=" + engineVersion);
            Emit("INFO", "COMPRESSION", "Compression=" + compression + ", BlockDirAtEnd=" + blockDirAtEnd + ", Flags=" + (int)flags);
            Emit("INFO", "UNCOMPRESSED_SIZE", "估算解压后大小: " + FormatBytes(uncompressed));

            if (uncompressed > MaxUncompressedBytes)
            {
                Emit("ERROR", "UNCOMPRESSED_SIZE_LIMIT", "解压后超过 PC 500MB 限制；LZMA 外层重压不会减少解压大小，需要优化实际资源: " + FormatBytes(uncompressed));
                return;
            }
            else if (uncompressed > WarnUncompressedBytes)
                Emit("WARN", "UNCOMPRESSED_SIZE_NEAR_LIMIT", "解压后大小接近 PC 500MB 限制: " + FormatBytes(uncompressed));

            if (!inputMode)
                foreach (var block in bundle.BlockAndDirInfo.BlockInfos)
                    if (block.GetCompressionType() != 1)
                    {
                        Emit("ERROR", "OUTPUT_COMPRESSION", "本工具最终上传产物必须全部使用 LZMA 数据块，不能包含 LZ4 或未压缩块。");
                        return;
                    }

            if (!string.IsNullOrEmpty(engineVersion) && !string.Equals(engineVersion, ExpectedUnityVersion, StringComparison.OrdinalIgnoreCase))
                Emit("WARN", "UNITY_HEADER_VERSION", "Bundle EngineVersion 与当前 SDK 目标不一致: " + engineVersion + " != " + ExpectedUnityVersion);

            if (compression == AssetBundleCompressionType.None)
                Emit("WARN", "COMPRESSION_NONE", "Bundle 未压缩，可能导致下载大小过大。");
            else if (compression == AssetBundleCompressionType.LZ4 || compression == AssetBundleCompressionType.LZ4Fast)
                Emit("WARN", "COMPRESSION_LZ4_EXPERIMENT", "Bundle 使用 LZ4/LZ4Fast，速度更快但需要实机验证安全校验。");

            HashSet<string> ids = null;
            if (quick && string.IsNullOrEmpty(expectedAvatar))
            {
                Emit("INFO", "AVATAR_ID_SCAN_SKIPPED", "快速源文件预检已跳过整文件 ID 扫描；结构化 Hotswap 会定位 PipelineManager。");
            }
            else
            {
                ids = ScanAvatarIds(path);
                if (ids.Count == 0)
                    Emit("INFO", "AVATAR_ID_SCAN_EMPTY", "压缩 bundle 字节中未直接扫到 avtr_ 标识，完整解析会继续检查 PipelineManager。");
                else
                    Emit("INFO", "AVATAR_IDS", "扫描到 Avatar ID: " + JoinIds(ids));

                if (!string.IsNullOrEmpty(expectedAvatar))
                {
                    if (!ids.Contains(expectedAvatar))
                    {
                        if (ids.Count == 0)
                        {
                            if (quick)
                                Emit("WARN", "EXPECTED_AVATAR_NOT_VERIFIED_QUICK", "快速模式无法在压缩 bundle 字节中确认新 Avatar ID，完整校验可检查 PipelineManager。");
                            else
                                Emit("INFO", "EXPECTED_AVATAR_NOT_VERIFIED_BYTES", "压缩 bundle 字节中无法直接确认新 Avatar ID，将以完整资产树扫描结果为准。");
                        }
                        else
                            Emit("REPAIRABLE", "EXPECTED_AVATAR_MISSING", "bundle 内未确认目标 Avatar ID，可通过 blueprintId/字符串改写后重新校验: " + expectedAvatar);
                    }
                    foreach (var id in ids)
                        if (!string.Equals(id, expectedAvatar, StringComparison.OrdinalIgnoreCase))
                            Emit("REPAIRABLE", "OLD_AVATAR_ID_REMAINS", "bundle 内仍残留其他 Avatar ID，可通过 hotswap 字符串清扫修复: " + id);
                }
            }
        }
        finally
        {
            try { probeBundle.file.Close(); } catch { }
        }

        if (!quick)
        {
            Emit("INFO", "FULL_SCAN_START", "开始完整解析资产树，LZMA 大包可能需要 1-3 分钟。");
            RunFullAssetChecks(path, tpk, expectedAvatar);
        }
        else
            Emit("INFO", "QUICK_MODE", "快速模式已跳过完整资产树扫描。");
    }

    static void RunFullAssetChecks(string path, string tpk, string expectedAvatar)
    {
        var am = new AssetsManager();
        am.LoadClassPackage(tpk);
        var bun = am.LoadBundleFile(path);
        try
        {
            var afi = am.LoadAssetsFileFromBundle(bun, 0, true);
            am.LoadClassDatabaseFromPackage(bun.file.Header.EngineVersion);

            var unityVersion = afi.file.Metadata.UnityVersion ?? "";
            var platform = afi.file.Metadata.TargetPlatform;
            Emit("INFO", "ASSETS_METADATA", "UnityVersion=" + unityVersion + ", TargetPlatform=" + platform);

            if (!string.Equals(unityVersion, ExpectedUnityVersion, StringComparison.OrdinalIgnoreCase))
                Emit("WARN", "UNITY_METADATA_VERSION", "Assets UnityVersion 与当前 SDK 目标不一致: " + unityVersion + " != " + ExpectedUnityVersion);
            if (platform != ExpectedPlatform)
                Emit("ERROR", "TARGET_PLATFORM", "目标平台不是 StandaloneWindows: " + platform);

            CheckPipelineManagers(am, afi, expectedAvatar);
            CheckAssetStringAvatarIds(am, afi, expectedAvatar);
            EmitAssetCounts(afi);
        }
        finally
        {
            try { bun.file.Close(); } catch { }
        }
    }

    static void CheckPipelineManagers(AssetsManager am, AssetsFileInstance afi, string expectedAvatar)
    {
        var blueprintIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pipelineCount = 0;
        var completedCount = 0;
        var completedZero = 0;

        foreach (var inf in afi.file.GetAssetsOfType((AssetClassID)114))
        {
            AssetTypeValueField bf;
            try { bf = am.GetBaseField(afi, inf); } catch { continue; }

            var bp = bf["blueprintId"];
            if (bp == null || bp.FieldName == "DUMMY") continue;

            pipelineCount++;
            if (!string.IsNullOrEmpty(bp.AsString))
                blueprintIds.Add(bp.AsString);

            var cp = bf["completedSDKPipeline"];
            if (cp != null && cp.FieldName != "DUMMY")
            {
                if (cp.AsInt == 1) completedCount++;
                else completedZero++;
            }
        }

        if (pipelineCount == 0)
            Emit("ERROR", "PIPELINE_MANAGER_MISSING", "未找到 VRCSDKPipelineManager / blueprintId 字段。");
        else
            Emit("INFO", "PIPELINE_MANAGER", "PipelineManager 数量=" + pipelineCount + ", blueprintId=" + JoinIds(blueprintIds));

        if (blueprintIds.Count == 0)
            Emit("ERROR", "BLUEPRINT_ID_EMPTY", "PipelineManager 的 blueprintId 为空。");
        else if (blueprintIds.Count > 1)
            Emit("WARN", "MULTIPLE_BLUEPRINT_IDS", "存在多个不同 blueprintId，上传后可能与目标记录不一致: " + JoinIds(blueprintIds));

        if (!string.IsNullOrEmpty(expectedAvatar) && blueprintIds.Count == 1 && !blueprintIds.Contains(expectedAvatar))
            Emit("ERROR", "PIPELINE_BLUEPRINT_MISMATCH", "PipelineManager blueprintId 与目标 Avatar ID 不一致: " + JoinIds(blueprintIds) + " != " + expectedAvatar);

        if (completedZero > 0)
            Emit("BUILD_REQUIRED", "SDK_PIPELINE_NOT_COMPLETED", "原始包不是官方 SDK 完整构建完成状态；这不是安全的字段修复项，需要官方 SDK 构建或继续做构建流程复现实验。");
        else if (completedCount > 0)
            Emit("INFO", "SDK_PIPELINE_COMPLETED", "completedSDKPipeline=1 数量=" + completedCount);
    }

    static void EmitAssetCounts(AssetsFileInstance afi)
    {
        Emit("INFO", "COUNT_GAMEOBJECT", "GameObject=" + CountType(afi, 1));
        Emit("INFO", "COUNT_MATERIAL", "Material=" + CountType(afi, 21));
        Emit("INFO", "COUNT_TEXTURE2D", "Texture2D=" + CountType(afi, 28));
        Emit("INFO", "COUNT_MESH", "Mesh=" + CountType(afi, 43));
        Emit("INFO", "COUNT_ANIMATIONCLIP", "AnimationClip=" + CountType(afi, 74));
        Emit("INFO", "COUNT_ANIMATORCONTROLLER", "AnimatorController=" + CountType(afi, 91));
        Emit("INFO", "COUNT_MONOBEHAVIOUR", "MonoBehaviour=" + CountType(afi, 114));
        Emit("INFO", "COUNT_ASSETBUNDLE", "AssetBundle=" + CountType(afi, 142));
    }

    static void CheckAssetStringAvatarIds(AssetsManager am, AssetsFileInstance afi, string expectedAvatar)
    {
        var idCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var scannedAssets = 0;
        var hitAssets = 0;
        var failedAssets = 0;

        foreach (var inf in afi.file.AssetInfos)
        {
            scannedAssets++;
            AssetTypeValueField bf;
            try { bf = am.GetBaseField(afi, inf); }
            catch { failedAssets++; continue; }

            var before = TotalCounts(idCounts);
            ScanFieldForAvatarIds(bf, idCounts);
            if (TotalCounts(idCounts) > before) hitAssets++;
        }

        if (idCounts.Count == 0)
        {
            Emit("INFO", "ASSET_STRING_AVATAR_IDS_EMPTY", "完整资产树字符串中未发现额外 avtr_ 引用。scanned=" + scannedAssets + ", failed=" + failedAssets);
            return;
        }

        Emit("INFO", "ASSET_STRING_AVATAR_IDS", "完整资产树字符串引用: " + JoinIdCounts(idCounts) + ", scanned=" + scannedAssets + ", hitAssets=" + hitAssets + ", failed=" + failedAssets);

        if (!string.IsNullOrEmpty(expectedAvatar))
        {
            if (!idCounts.ContainsKey(expectedAvatar))
                Emit("WARN", "EXPECTED_AVATAR_NOT_IN_STRINGS", "完整字符串扫描未发现目标 Avatar ID；如果 PipelineManager 已匹配，通常不是致命问题: " + expectedAvatar);

            foreach (var kv in idCounts)
                if (!string.Equals(kv.Key, expectedAvatar, StringComparison.OrdinalIgnoreCase))
                    Emit("REPAIRABLE", "OLD_AVATAR_ID_STRING_REMAINS", "完整资产树仍残留其他 Avatar ID，可通过字符串清扫修复: " + kv.Key + " count=" + kv.Value);
        }
    }

    static void ScanFieldForAvatarIds(AssetTypeValueField field, Dictionary<string, int> idCounts)
    {
        if (field == null) return;

        if (string.Equals(field.TypeName, "string", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var text = field.AsString;
                if (!string.IsNullOrEmpty(text))
                {
                    foreach (Match match in NewAvatarIdRegex().Matches(text))
                    {
                        var id = match.Value;
                        idCounts[id] = idCounts.ContainsKey(id) ? idCounts[id] + 1 : 1;
                    }
                }
            }
            catch { }
        }

        if (field.Children == null) return;
        foreach (var child in field.Children)
            ScanFieldForAvatarIds(child, idCounts);
    }

    static int CountType(AssetsFileInstance afi, int classId)
    {
        var count = 0;
        foreach (var ignored in afi.file.GetAssetsOfType((AssetClassID)classId))
            count++;
        return count;
    }

    static long EstimateUncompressedSize(AssetBundleFile bundle)
    {
        long directoryTotal = 0;
        if (bundle.BlockAndDirInfo != null && bundle.BlockAndDirInfo.DirectoryInfos != null)
        {
            foreach (var dir in bundle.BlockAndDirInfo.DirectoryInfos)
                directoryTotal = checked(directoryTotal + dir.DecompressedSize);
        }

        long blockTotal = 0;
        if (bundle.BlockAndDirInfo != null && bundle.BlockAndDirInfo.BlockInfos != null)
        {
            foreach (var block in bundle.BlockAndDirInfo.BlockInfos)
                blockTotal = checked(blockTotal + block.DecompressedSize);
        }

        return Math.Max(directoryTotal, blockTotal);
    }

    static void ValidateBundleRanges(AssetBundleFile bundle, long physicalLength)
    {
        var h = bundle.Header;
        var info = bundle.BlockAndDirInfo;
        if (h.Signature != "UnityFS" || h.Version < 6 || h.Version > 8 || info == null ||
            info.BlockInfos == null || info.BlockInfos.Length == 0 ||
            info.DirectoryInfos == null || info.DirectoryInfos.Count == 0)
            throw new InvalidDataException("UnityFS 容器版本、数据块或资源目录无效。");
        var flags = (uint)h.FileStreamHeader.Flags;
        if ((flags & ~0x2ffu) != 0 || (flags & 0x40) == 0)
            throw new InvalidDataException("UnityFS 容器含未支持的标志。");
        long total = h.FileStreamHeader.TotalFileSize;
        if (total <= 0 || total > physicalLength)
            throw new InvalidDataException("UnityFS 声明长度超过实际文件，文件可能被截断。");
        long compressed = 0, expanded = 0;
        foreach (var block in info.BlockInfos)
        {
            if (block.GetCompressionType() > 3 || block.CompressedSize == 0 || block.DecompressedSize == 0)
                throw new InvalidDataException("UnityFS 数据块大小或压缩类型无效。");
            if (block.GetCompressionType() == 0 && block.CompressedSize != block.DecompressedSize)
                throw new InvalidDataException("未压缩数据块长度不一致。");
            compressed = checked(compressed + block.CompressedSize);
            expanded = checked(expanded + block.DecompressedSize);
        }
        long dataOffset = h.GetFileDataOffset();
        if ((flags & 0x200) != 0) dataOffset = checked(dataOffset + 15) & ~15L;
        long infoOffset = h.GetBundleInfoOffset();
        long dataEnd = checked(dataOffset + compressed);
        long infoEnd = checked(infoOffset + h.FileStreamHeader.CompressedSize);
        if (dataEnd > total || infoEnd > total || (dataOffset < infoEnd && infoOffset < dataEnd))
            throw new InvalidDataException("UnityFS 数据块和目录越界或重叠。");
        foreach (var dir in info.DirectoryInfos)
            if (dir.Offset < 0 || dir.DecompressedSize < 0 || dir.Offset > expanded || dir.DecompressedSize > expanded - dir.Offset)
                throw new InvalidDataException("UnityFS 内部资源范围越界: " + dir.Name);
    }

    static HashSet<string> ScanAvatarIds(string path)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var regex = NewAvatarIdRegex();
        var overlap = "";
        var buffer = new byte[1024 * 1024];
        using (var stream = File.OpenRead(path))
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var text = overlap + Encoding.ASCII.GetString(buffer, 0, read);
                foreach (Match match in regex.Matches(text))
                    ids.Add(match.Value);
                overlap = text.Length > 64 ? text.Substring(text.Length - 64) : text;
            }
        }
        return ids;
    }

    static Regex NewAvatarIdRegex()
    {
        return new Regex("avtr_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);
    }

    static int TotalCounts(Dictionary<string, int> counts)
    {
        var total = 0;
        foreach (var kv in counts) total += kv.Value;
        return total;
    }

    static bool HasFlag(AssetBundleFSHeaderFlags flags, AssetBundleFSHeaderFlags flag)
    {
        return (((int)flags) & (int)flag) != 0;
    }

    static string FormatBytes(long bytes)
    {
        return (bytes / 1048576.0).ToString("F1") + " MB";
    }

    static string JoinIds(IEnumerable<string> ids)
    {
        var sb = new StringBuilder();
        foreach (var id in ids)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(id);
        }
        return sb.Length == 0 ? "(none)" : sb.ToString();
    }

    static string JoinIdCounts(Dictionary<string, int> ids)
    {
        var sb = new StringBuilder();
        foreach (var kv in ids)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(kv.Key).Append(" x").Append(kv.Value);
        }
        return sb.Length == 0 ? "(none)" : sb.ToString();
    }

    static void Emit(string level, string code, string message)
    {
        if (level == "ERROR") _errors++;
        else if (level == "REPAIRABLE") _repairable++;
        else if (level == "BUILD_REQUIRED") _buildRequired++;
        else if (level == "WARN") _warnings++;
        else _infos++;
        Console.WriteLine(level + "|" + code + "|" + message);
    }

    static void EmitSummary()
    {
        Console.WriteLine("SUMMARY|errors=" + _errors +
                          "|repairable=" + _repairable +
                          "|buildRequired=" + _buildRequired +
                          "|warnings=" + _warnings +
                          "|infos=" + _infos);
    }
}
