using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AssetsTools.NET;
using LZ4ps;

public static class NativeAvatarLzma
{
    public static bool Enabled {
        get {
            string setting = (Environment.GetEnvironmentVariable("HOTUPLOAD_NATIVE_AVATAR_LZMA") ?? "").Trim();
            return !setting.Equals("0", StringComparison.OrdinalIgnoreCase) &&
                   !setting.Equals("false", StringComparison.OrdinalIgnoreCase) &&
                   !setting.Equals("off", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void PackWithFallback(AssetBundleFile raw, string rawPath, string output, bool atEnd)
    {
        if (File.Exists(output) || Path.GetFullPath(rawPath).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Refusing to overwrite an input or existing output.");
        int threads = Environment.ProcessorCount < 2 || Environment.GetEnvironmentVariable("HOTUPLOAD_NATIVE_LZMA_THREADS") == "1" ? 1 : 2;
        try {
            Console.WriteLine("[FAST] Full single-stream native LZMA: dict=2MiB fb=32 threads=" + threads + " EOS=false");
            Pack(raw, rawPath, output, atEnd, threads, 2097152, 32, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) {
            if (File.Exists(output)) throw new IOException("Output appeared during native encoding; refusing fallback overwrite.", ex);
            Console.WriteLine("[FAST] Native LZMA unavailable; using canonical managed packer: " + ex.GetType().Name + ": " + ex.Message);
            using (var writer = new AssetsFileWriter(output)) raw.Pack(writer, AssetBundleCompressionType.LZMA, atEnd);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int ProgressCallback(ulong input, ulong output);

    [DllImport("NativeLzma.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int nlc_encode(string source, ulong offset, ulong length,
        string destination, int threads, uint dictionary, int fastBytes, ProgressCallback callback);

    public static AssetBundleFile OpenRaw(string path)
    {
        var file = new AssetBundleFile();
        var reader = new AssetsFileReader(path);
        try
        {
            file.Read(reader);
            if (file.Header.Signature != "UnityFS" || file.Header.Version < 6 || file.Header.Version > 8)
                throw new InvalidDataException("Unsupported UnityFS header.");
            if (file.BlockAndDirInfo == null || file.BlockAndDirInfo.BlockInfos.Length == 0)
                throw new InvalidDataException("Missing blocks.");
            long size = 0;
            foreach (var block in file.BlockAndDirInfo.BlockInfos)
            {
                if (block.GetCompressionType() != 0 || block.CompressedSize != block.DecompressedSize)
                    throw new InvalidDataException("Input must be the fully rewritten raw bundle.");
                size = checked(size + block.DecompressedSize);
            }
            if (size <= 0 || size > int.MaxValue || file.DataIsCompressed ||
                file.DataReader.BaseStream.Length != size || file.Header.FileStreamHeader.TotalFileSize != reader.BaseStream.Length)
                throw new InvalidDataException("Invalid raw payload length or trailing data.");
            foreach (var entry in file.BlockAndDirInfo.DirectoryInfos)
                if (entry.Offset < 0 || entry.DecompressedSize < 0 || entry.Offset > size || entry.DecompressedSize > size - entry.Offset)
                    throw new InvalidDataException("Directory outside payload.");
            return file;
        }
        catch { reader.Close(); throw; }
    }

    public static void Pack(AssetBundleFile raw, string rawPath, string output, bool atEnd,
        int threads, uint dictionary, int fastBytes, Func<bool> cancelled)
    {
        rawPath = Path.GetFullPath(rawPath);
        output = Path.GetFullPath(output);
        if (string.Equals(rawPath, output, StringComparison.OrdinalIgnoreCase) || File.Exists(output))
            throw new IOException("Refusing to overwrite an input or existing output.");
        long size = raw.DataReader.BaseStream.Length;
        if (size <= 0 || size > int.MaxValue || raw.DataIsCompressed)
            throw new InvalidDataException("Invalid raw bundle.");
        var rawStream = raw.Reader.BaseStream as FileStream;
        if (rawStream == null || !Path.GetFullPath(rawStream.Name).Equals(rawPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Metadata and raw input must come from the same file.");
        long blockLength = 0;
        foreach (var b in raw.BlockAndDirInfo.BlockInfos)
        {
            if (b.GetCompressionType() != 0 || b.CompressedSize != b.DecompressedSize)
                throw new InvalidDataException("Raw blocks required.");
            blockLength = checked(blockLength + b.DecompressedSize);
        }
        if (size != blockLength || raw.Header.FileStreamHeader.TotalFileSize != rawStream.Length)
            throw new InvalidDataException("Raw payload size mismatch.");
        foreach (var entry in raw.BlockAndDirInfo.DirectoryInfos)
            if (entry.Offset < 0 || entry.DecompressedSize < 0 || entry.Offset > size || entry.DecompressedSize > size - entry.Offset)
                throw new InvalidDataException("Raw directory outside payload.");
        string payload = output + ".lzma-" + Guid.NewGuid().ToString("N") + ".tmp";
        string pending = output + ".packing-" + Guid.NewGuid().ToString("N") + ".tmp";
        long offset = raw.Header.GetFileDataOffset();
        if ((raw.Header.FileStreamHeader.Flags & AssetBundleFSHeaderFlags.BlockInfoNeedPaddingAtStart) != 0)
            offset = (offset + 15) & ~15L;
        var heartbeat = Stopwatch.StartNew();
        ProgressCallback callback = delegate(ulong input, ulong compressed) {
            try
            {
                if (cancelled != null && cancelled()) return 1;
                if (heartbeat.ElapsedMilliseconds >= 10000) {
                    Console.WriteLine("[NATIVE_LZMA] processed=" + input + "/" + size + " compressed=" + compressed);
                    heartbeat.Restart();
                }
                return 0;
            }
            catch { return 1; }
        };
        try
        {
            if (cancelled != null && cancelled()) throw new OperationCanceledException();
            int code = nlc_encode(rawPath, (ulong)offset, (ulong)size, payload, threads, dictionary, fastBytes, callback);
            GC.KeepAlive(callback);
            if (code == 10) throw new OperationCanceledException("Native LZMA cancelled.");
            if (code != 0) throw new IOException("Native LZMA failed, SDK code=" + code);
            if (cancelled != null && cancelled()) throw new OperationCanceledException();
            long packedSize = new FileInfo(payload).Length;
            if (packedSize < 10 || packedSize > uint.MaxValue) throw new IOException("Invalid encoded size.");
            var directory = new AssetBundleBlockAndDirInfo {
                Hash = default(Hash128), DirectoryInfos = raw.BlockAndDirInfo.DirectoryInfos,
                BlockInfos = new[] { new AssetBundleBlockInfo {
                    DecompressedSize = (uint)size, CompressedSize = (uint)packedSize, Flags = 65 } }
            };
            byte[] info;
            using (var ms = new MemoryStream()) {
                var iw = new AssetsFileWriter(ms); iw.BigEndian = true;
                directory.Write(iw); info = ms.ToArray();
            }
            byte[] packedInfo = LZ4Codec.Encode32HC(info, 0, info.Length);
            var header = new AssetBundleHeader {
                Signature = raw.Header.Signature, Version = raw.Header.Version,
                GenerationVersion = raw.Header.GenerationVersion, EngineVersion = raw.Header.EngineVersion,
                FileStreamHeader = new AssetBundleFSHeader {
                    Flags = (AssetBundleFSHeaderFlags)(0x43 | (atEnd ? 0x80 : 0)),
                    CompressedSize = (uint)packedInfo.Length, DecompressedSize = (uint)info.Length }
            };
            using (var fs = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new AssetsFileWriter(fs)) {
                header.Write(writer);
                if (header.Version >= 7) writer.Align16();
                if (!atEnd) writer.Write(packedInfo);
                using (var stream = File.OpenRead(payload)) stream.CopyTo(writer.BaseStream, 1024 * 1024);
                if (atEnd) writer.Write(packedInfo);
                header.FileStreamHeader.TotalFileSize = writer.Position;
                writer.Position = 0; header.Write(writer);
                writer.Flush(); fs.Flush(true);
            }
            if (cancelled != null && cancelled()) throw new OperationCanceledException();
            File.Move(pending, output);
        }
        finally {
            if (File.Exists(payload)) File.Delete(payload);
            if (File.Exists(pending)) File.Delete(pending);
        }
    }
}
