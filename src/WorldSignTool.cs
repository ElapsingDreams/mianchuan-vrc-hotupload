using System;
using System.IO;
using System.Text;

// Reads the VRChat AssetBundle footer appended after the Unity bundle body
// and extracts the worldSignature string for upload.
// The footer format (reverse-engineered from VRCCore-Editor.dll):
//   [bundle body] [sections...] [magic 4 bytes] [total footer length 4 bytes LE]
// Each section: [type string (LEN-prefixed UTF8)] [data length 4 bytes LE] [data bytes]
// Magic = 0x56 0x52 0x43 0x46 ("VRCF")
// worldSignature = base64 of the "publicKey" footer section data.
static class WorldSignTool
{
    static readonly byte[] FooterMagic = { 0x56, 0x52, 0x43, 0x46 }; // "VRCF"

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length < 1)
        {
            Console.WriteLine("Usage: WorldSignTool <input.vrcw>");
            Console.WriteLine("Output: SIGNATURE|<base64-worldSignature>");
            Console.WriteLine("        NO_FOOTER|bundle has no VRChat footer");
            Console.WriteLine("        ERROR|<message>");
            return 2;
        }

        string path = args[0];
        if (!File.Exists(path))
        {
            Console.WriteLine("ERROR|File not found: " + path);
            return 2;
        }

        try
        {
            string sig = ExtractWorldSignature(path);
            if (sig == null)
            {
                Console.WriteLine("NO_FOOTER|Bundle does not have a VRChat footer with a signature section. This bundle was likely not built with the official SDK.");
                return 1;
            }
            Console.WriteLine("SIGNATURE|" + sig);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR|" + ex.GetType().Name + ": " + ex.Message);
            return 2;
        }
    }

    // Returns null if no footer, throws on IO error.
    public static string ExtractWorldSignature(string vrcwPath)
    {
        using (var fs = new FileStream(vrcwPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            return ExtractWorldSignature(fs);
        }
    }

    public static string ExtractWorldSignature(Stream stream)
    {
        long fileSize = stream.Length;

        // Footer layout (from end of file):
        //   [4 bytes LE] total footer byte count (not including magic+length trailer)
        //   [4 bytes]    magic "VRCF"
        // So last 8 bytes = footer length + magic
        if (fileSize < 48) // minimum unity bundle header + footer trailer
            return null;

        stream.Seek(-8, SeekOrigin.End);
        byte[] trailer = new byte[8];
        if (stream.Read(trailer, 0, 8) != 8)
            return null;

        // Check magic at bytes [4..7]
        if (trailer[4] != FooterMagic[0] || trailer[5] != FooterMagic[1] ||
            trailer[6] != FooterMagic[2] || trailer[7] != FooterMagic[3])
            return null;

        int footerDataLen = trailer[0] | (trailer[1] << 8) | (trailer[2] << 16) | (trailer[3] << 24);
        if (footerDataLen <= 0 || footerDataLen > 4 * 1024 * 1024)
            return null;

        long footerStart = fileSize - 8 - footerDataLen;
        if (footerStart < 0)
            return null;

        // Read the bundle header to find declared bundle body size
        // and validate footer starts after it
        stream.Seek(0, SeekOrigin.Begin);
        long bundleBodySize = ReadUnityBundleBodySize(stream);
        if (bundleBodySize > 0 && footerStart < bundleBodySize)
            return null;

        // Read all footer sections
        stream.Seek(footerStart, SeekOrigin.Begin);
        byte[] footerBytes = new byte[footerDataLen];
        int totalRead = 0;
        while (totalRead < footerDataLen)
        {
            int r = stream.Read(footerBytes, totalRead, footerDataLen - totalRead);
            if (r <= 0) return null;
            totalRead += r;
        }

        return ParseSignatureFromFooter(footerBytes);
    }

    static string ParseSignatureFromFooter(byte[] data)
    {
        int pos = 0;
        while (pos < data.Length - 4)
        {
            // Read section type: 2-byte LE length prefix + UTF8 bytes
            if (pos + 2 > data.Length) break;
            int typeLen = data[pos] | (data[pos + 1] << 8);
            pos += 2;
            if (typeLen <= 0 || typeLen > 256 || pos + typeLen > data.Length) break;
            string sectionType = Encoding.UTF8.GetString(data, pos, typeLen);
            pos += typeLen;

            // Read data length: 4-byte LE
            if (pos + 4 > data.Length) break;
            int dataLen = data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24);
            pos += 4;
            if (dataLen < 0 || pos + dataLen > data.Length) break;

            byte[] sectionData = new byte[dataLen];
            Array.Copy(data, pos, sectionData, 0, dataLen);
            pos += dataLen;

            // "publicKey" section → base64 is the worldSignature
            if (string.Equals(sectionType, "publicKey", StringComparison.OrdinalIgnoreCase))
                return Convert.ToBase64String(sectionData);

            // "worldSignature" section → raw string value
            if (string.Equals(sectionType, "worldSignature", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sectionType, "signature", StringComparison.OrdinalIgnoreCase))
                return Encoding.UTF8.GetString(sectionData);
        }
        return null;
    }

    static long ReadUnityBundleBodySize(Stream stream)
    {
        // UnityFS header: magic(8) version(4) unityVersion(var) engineVersion(var)
        // then at fixed offset for version 8: totalFileSize as int64 BE at offset 16
        try
        {
            byte[] magic = new byte[8];
            if (stream.Read(magic, 0, 8) != 8) return -1;
            string magicStr = Encoding.ASCII.GetString(magic, 0, 7);
            if (magicStr != "UnityFS") return -1;

            // version: 4 bytes BE
            byte[] verBytes = new byte[4];
            if (stream.Read(verBytes, 0, 4) != 4) return -1;
            int version = (verBytes[0] << 24) | (verBytes[1] << 16) | (verBytes[2] << 8) | verBytes[3];
            if (version < 6 || version > 9) return -1;

            // skip unityVersion and engineVersion (null-terminated strings)
            SkipNullTerminated(stream);
            SkipNullTerminated(stream);

            // totalFileSize: 8 bytes BE
            byte[] sizeBytes = new byte[8];
            if (stream.Read(sizeBytes, 0, 8) != 8) return -1;
            long totalSize = 0;
            for (int i = 0; i < 8; i++)
                totalSize = (totalSize << 8) | sizeBytes[i];
            return totalSize;
        }
        catch
        {
            return -1;
        }
    }

    static void SkipNullTerminated(Stream stream)
    {
        for (int i = 0; i < 512; i++)
        {
            int b = stream.ReadByte();
            if (b <= 0) return;
        }
    }
}
