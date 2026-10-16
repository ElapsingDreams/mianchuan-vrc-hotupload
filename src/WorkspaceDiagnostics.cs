using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace VrcaUploader
{
    public partial class VrcaClient
    {
        const string SecretKeys = "password|passwd|auth|twoFactorAuth|access_token|refresh_token|accessToken|refreshToken|token|ticket|authorization|cookie|set-cookie|privateKey|secretKey|operationKey|apiKey";
        static readonly Regex DiagnosticUrls = new Regex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex DiagnosticJsonSecrets = new Regex("(\"(?:" + SecretKeys + ")\"\\s*:\\s*)(\"(?:\\\\.|[^\"\\\\])*\"|[^,}\\r\\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex DiagnosticHeaders = new Regex(@"(?im)(\b(?:Authorization|Cookie|Set-Cookie)\s*:\s*)[^\r\n]+", RegexOptions.Compiled);
        static readonly Regex DiagnosticValues = new Regex("(\\b(?:" + SecretKeys + ")\\s*=\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s;&,}]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex DiagnosticBearer = new Regex(@"\b(Bearer|Basic)\s+[A-Za-z0-9_+/=.\-]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string ProtectDiagnosticText(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            var text = DiagnosticUrls.Replace(value, m => ProtectDiagnosticUrl(m.Value));
            text = DiagnosticJsonSecrets.Replace(text, "$1\"<redacted>\"");
            text = DiagnosticHeaders.Replace(text, "$1<redacted>");
            text = DiagnosticValues.Replace(text, "$1<redacted>");
            return DiagnosticBearer.Replace(text, "$1 <redacted>");
        }

        static string ProtectDiagnosticUrl(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri)) return value;
            var builder = new UriBuilder(uri);
            if (!string.IsNullOrEmpty(uri.UserInfo)) { builder.UserName = "redacted"; builder.Password = "redacted"; }
            var query = uri.Query.TrimStart('?').Split('&');
            for (int i = 0; i < query.Length; i++)
            {
                int split = query[i].IndexOf('=');
                var key = split < 0 ? query[i] : query[i].Substring(0, split);
                var decoded = Uri.UnescapeDataString(key);
                if (Regex.IsMatch(decoded, "^(?:" + SecretKeys + "|signature|sig|credential|AWSAccessKeyId|X-Amz-.+|X-Goog-.+)$", RegexOptions.IgnoreCase))
                    query[i] = key + "=redacted";
            }
            builder.Query = string.Join("&", query);
            if (!string.IsNullOrEmpty(builder.Fragment)) builder.Fragment = "redacted";
            return builder.Uri.AbsoluteUri;
        }

        static string LimitDiagnosticBody(string text, int maximum)
        {
            return text.Length <= maximum ? text : text.Substring(0, maximum) +
                "\r\n[TRUNCATED] response body chars=" + text.Length + " shown=" + maximum;
        }

        public static string FormatDiagnosticEntry(string text)
        {
            var prefix = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz") +
                "][T" + Thread.CurrentThread.ManagedThreadId + "] ";
            return prefix + text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine + prefix) + Environment.NewLine;
        }

        public void LogWorkspaceException(string context, Exception error)
        {
            Log("[TASK_ERROR] " + context + Environment.NewLine + error.ToString());
            for (Exception item = error; item != null; item = item.InnerException)
            {
                var api = item as ApiException;
                if (api != null && !string.IsNullOrEmpty(api.Body))
                    Log("[TASK_API_ERROR] status=" + api.StatusCode + " body=" + LimitDiagnosticBody(ProtectDiagnosticText(api.Body),32768));
            }
        }
    }
}
