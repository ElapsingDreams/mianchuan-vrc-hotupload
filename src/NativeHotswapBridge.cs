using System;
using System.Runtime.InteropServices;
using System.Text;

static class NativeHotswapBridge
{
    public sealed class CabRewriteResult
    {
        public bool Success;
        public string Error;
        public string Summary;
        public int CabReplacements;
    }

    [DllImport("HotUploadNative.dll", ExactSpelling = true, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    static extern int hu_rewrite_cab_names_file(
        string rawPath, string cabOverride, int randomizeCab, string preferredAvatarId,
        [Out] StringBuilder summary, int summaryCapacity, out int cabReplacements);

    public static CabRewriteResult RewriteCabNamesInFile(
        string rawPath, string cabOverride, bool randomizeCab, string preferredAvatarId)
    {
        var summary = new StringBuilder(2048);
        int count;
        int result = hu_rewrite_cab_names_file(rawPath,
            string.IsNullOrWhiteSpace(cabOverride) ? null : cabOverride,
            randomizeCab ? 1 : 0,
            string.IsNullOrWhiteSpace(preferredAvatarId) ? null : preferredAvatarId,
            summary, summary.Capacity, out count);
        return new CabRewriteResult {
            Success = result == 0,
            Error = result == 0 ? "" : MapError(result, summary.ToString()),
            Summary = summary.ToString(),
            CabReplacements = count
        };
    }

    static string MapError(int code, string detail)
    {
        if (!string.IsNullOrEmpty(detail)) return detail;
        if (code == 5) return "Failed to read raw bundle.";
        if (code == 6) return "Failed to write raw bundle.";
        if (code == 7) return "Invalid CAB override.";
        if (code == 9) return "AssetBundle/CAB association is ambiguous.";
        return "Native patch failed (" + code + ").";
    }
}
