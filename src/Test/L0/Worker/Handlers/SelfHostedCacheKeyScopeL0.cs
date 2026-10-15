using System;
using System.Collections.Generic;
using GitHub.Runner.Worker.Handlers;
using Xunit;

namespace GitHub.Runner.Common.Tests.Worker.Handlers
{
    public sealed class SelfHostedCacheKeyScopeL0
    {
        [Theory]
        [InlineData("actions/cache")]
        [InlineData("actions/cache/restore")]
        [InlineData("actions/cache/save")]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void ScopesCacheAndRestoreKeysToSelfHostedWorkspace(string actionName)
        {
            var environment = CreateEnvironment("/Users/elfaria/.homerun/runners/runner-a/_work/NotProton");

            Assert.True(SelfHostedCacheKeyScope.TryApply(environment, actionName));

            var scopedKey = environment["INPUT_KEY"];
            Assert.StartsWith("self-hosted-", scopedKey);
            var prefix = scopedKey.Substring(0, scopedKey.Length - "wine-key".Length);
            Assert.EndsWith("wine-key", scopedKey);
            Assert.Equal($"{prefix}wine-restore-\n{prefix}source-", environment["INPUT_RESTORE-KEYS"]);
            Assert.True(scopedKey.Length <= 512);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void ACachePrimaryKeyCanBePassedToTheSaveActionWithoutDoubleScoping()
        {
            var environment = CreateEnvironment("/Users/elfaria/.homerun/runners/runner-a/_work/NotProton");

            Assert.True(SelfHostedCacheKeyScope.TryApply(environment, "actions/cache/restore"));
            var scopedKey = environment["INPUT_KEY"];
            var scopedRestoreKeys = environment["INPUT_RESTORE-KEYS"];
            environment["INPUT_KEY"] = scopedKey;
            environment["INPUT_RESTORE-KEYS"] = scopedRestoreKeys;

            Assert.True(SelfHostedCacheKeyScope.TryApply(environment, "actions/cache/save"));

            Assert.Equal(scopedKey, environment["INPUT_KEY"]);
            Assert.Equal(scopedRestoreKeys, environment["INPUT_RESTORE-KEYS"]);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void DifferentWorkspacesCannotReuseTheSameCacheKey()
        {
            var first = CreateEnvironment("/Users/elfaria/.homerun/runners/runner-a/_work/NotProton");
            var second = CreateEnvironment("/Users/elfaria/.homerun/runners/runner-b/_work/NotProton");

            Assert.True(SelfHostedCacheKeyScope.TryApply(first, "actions/cache/restore"));
            Assert.True(SelfHostedCacheKeyScope.TryApply(second, "actions/cache/save"));

            Assert.NotEqual(first["INPUT_KEY"], second["INPUT_KEY"]);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void GitHubHostedCacheKeysRemainUnchanged()
        {
            var environment = CreateEnvironment("/Users/runner/work/NotProton/NotProton");
            environment["RUNNER_ENVIRONMENT"] = "github-hosted";
            var originalKey = environment["INPUT_KEY"];
            var originalRestoreKeys = environment["INPUT_RESTORE-KEYS"];

            Assert.False(SelfHostedCacheKeyScope.TryApply(environment, "actions/cache/restore"));

            Assert.Equal(originalKey, environment["INPUT_KEY"]);
            Assert.Equal(originalRestoreKeys, environment["INPUT_RESTORE-KEYS"]);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void DoesNotChangeNonCacheActions()
        {
            var environment = CreateEnvironment("/Users/elfaria/.homerun/runners/runner-a/_work/NotProton");
            var originalKey = environment["INPUT_KEY"];
            var originalRestoreKeys = environment["INPUT_RESTORE-KEYS"];

            Assert.False(SelfHostedCacheKeyScope.TryApply(environment, "actions/upload-artifact"));

            Assert.Equal(originalKey, environment["INPUT_KEY"]);
            Assert.Equal(originalRestoreKeys, environment["INPUT_RESTORE-KEYS"]);
        }

        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Worker")]
        public void LongCacheKeysStayWithinTheServiceLimit()
        {
            var prefix = "self-hosted-0123456789abcdef-";
            var scopedKey = SelfHostedCacheKeyScope.ScopeKey(new string('k', 512), prefix);

            Assert.Equal(512, scopedKey.Length);
            Assert.StartsWith(prefix, scopedKey);
        }

        private static Dictionary<string, string> CreateEnvironment(string workspace)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["RUNNER_ENVIRONMENT"] = "self-hosted",
                ["RUNNER_OS"] = "macOS",
                ["RUNNER_ARCH"] = "ARM64",
                ["RUNNER_WORKSPACE"] = workspace,
                ["GITHUB_WORKSPACE"] = System.IO.Path.Combine(workspace, "NotProton"),
                ["INPUT_KEY"] = "wine-key",
                ["INPUT_RESTORE-KEYS"] = "wine-restore-\nsource-"
            };
        }
    }
}
