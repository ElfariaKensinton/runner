using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace GitHub.Runner.Worker.Handlers
{
    internal static class SelfHostedCacheKeyScope
    {
        private const int MaximumCacheKeyLength = 512;
        private static readonly HashSet<string> CacheActionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "actions/cache",
            "actions/cache/restore",
            "actions/cache/save"
        };

        internal static bool TryApply(IDictionary<string, string> environment, string actionName)
        {
            if (environment == null || !CacheActionNames.Contains(actionName ?? string.Empty))
            {
                return false;
            }

            if (!environment.TryGetValue("RUNNER_ENVIRONMENT", out var runnerEnvironment)
                || !string.Equals(runnerEnvironment, "self-hosted", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            environment.TryGetValue("RUNNER_WORKSPACE", out var runnerWorkspace);
            environment.TryGetValue("GITHUB_WORKSPACE", out var githubWorkspace);
            if (string.IsNullOrWhiteSpace(runnerWorkspace) && string.IsNullOrWhiteSpace(githubWorkspace))
            {
                return false;
            }

            var prefix = CreatePrefix(environment, runnerWorkspace, githubWorkspace);
            var changed = false;

            if (environment.TryGetValue("INPUT_KEY", out var key) && !string.IsNullOrEmpty(key))
            {
                environment["INPUT_KEY"] = ScopeKey(key, prefix);
                changed = true;
            }

            if (environment.TryGetValue("INPUT_RESTORE-KEYS", out var restoreKeys)
                && !string.IsNullOrWhiteSpace(restoreKeys))
            {
                environment["INPUT_RESTORE-KEYS"] = ScopeRestoreKeys(restoreKeys, prefix);
                changed = true;
            }

            return changed;
        }

        internal static bool IsCacheAction(string actionName)
        {
            return CacheActionNames.Contains(actionName ?? string.Empty);
        }

        internal static string CreatePrefix(IDictionary<string, string> environment, string runnerWorkspace, string githubWorkspace)
        {
            var runnerOs = environment.TryGetValue("RUNNER_OS", out var os) ? os ?? string.Empty : string.Empty;
            var runnerArch = environment.TryGetValue("RUNNER_ARCH", out var arch) ? arch ?? string.Empty : string.Empty;
            var normalizedRunnerWorkspace = NormalizePath(runnerWorkspace);
            var normalizedGithubWorkspace = NormalizePath(githubWorkspace);
            var fingerprint = $"{runnerOs}\n{runnerArch}\n{normalizedRunnerWorkspace}\n{normalizedGithubWorkspace}";
            var digest = ComputeDigest(fingerprint).Substring(0, 16);
            return $"self-hosted-{digest}-";
        }

        internal static string ScopeKey(string key, string prefix)
        {
            if (string.IsNullOrEmpty(key))
            {
                return key;
            }

            var availableLength = MaximumCacheKeyLength - prefix.Length;
            if (key.Length <= availableLength)
            {
                return prefix + key;
            }

            // Preserve uniqueness for keys near the cache service's 512-character limit.
            var suffix = "-" + ComputeDigest(key).Substring(0, 16);
            var keyLength = MaximumCacheKeyLength - prefix.Length - suffix.Length;
            return prefix + key.Substring(0, keyLength) + suffix;
        }

        internal static string ScopeRestoreKeys(string restoreKeys, string prefix)
        {
            if (string.IsNullOrWhiteSpace(restoreKeys))
            {
                return restoreKeys;
            }

            var normalizedLineEndings = restoreKeys.Replace("\r\n", "\n").Replace('\r', '\n');
            return string.Join("\n", normalizedLineEndings.Split('\n').Select(key =>
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    return key;
                }

                var availableLength = MaximumCacheKeyLength - prefix.Length;
                return prefix + (key.Length <= availableLength ? key : key.Substring(0, availableLength));
            }));
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                path = Path.GetFullPath(path.Trim());
            }
            catch
            {
                path = path.Trim();
            }

            var root = Path.GetPathRoot(path) ?? string.Empty;
            while (path.Length > root.Length
                && (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)))
            {
                path = path.Substring(0, path.Length - 1);
            }

            return Path.DirectorySeparatorChar == '\\' ? path.ToUpperInvariant() : path;
        }

        private static string ComputeDigest(string value)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        }
    }
}
