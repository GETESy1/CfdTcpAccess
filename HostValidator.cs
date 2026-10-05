using System;
using System.Text.RegularExpressions;

namespace CfdTcpAccess
{
    public static class HostValidator
    {
        private static readonly Regex HostnameRegex = new(
            @"^(?=.{1,253}$)([a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,63}$",
            RegexOptions.Compiled);

        /// <summary>校验隧道域名（不接受协议前缀与端口）。</summary>
        public static bool IsValidHostname(string? hostname) =>
            !string.IsNullOrWhiteSpace(hostname) && HostnameRegex.IsMatch(hostname.Trim());

        /// <summary>把用户可能粘贴进去的 https://x/ 、带端口的写法清洗成纯主机名。</summary>
        public static string Normalize(string? input)
        {
            var text = (input ?? "").Trim().Trim('"', '\'');
            if (text.Length == 0)
            {
                return "";
            }

            if (text.Contains("://", StringComparison.Ordinal))
            {
                if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
                {
                    text = uri.Host;
                }
            }

            var slash = text.IndexOf('/');
            if (slash >= 0)
            {
                text = text[..slash];
            }

            var colon = text.LastIndexOf(':');
            if (colon > 0 && !text.Contains(']'))
            {
                text = text[..colon];
            }

            return text.Trim();
        }
    }
}
