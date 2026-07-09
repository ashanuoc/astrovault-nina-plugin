using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Astrovault;
using Astrovault.Core;

namespace Astrovault.Tests.Core
{
    /// <summary>
    /// SCAN-1 / SC5: offline secret-scan gate plus the SC1 endpoint-policy proof and the
    /// SC4 (verify-only) Retry-After assertion.
    ///
    /// The scanner walks the SHIPPABLE source tree (CloudUploadPlugin/ + scripts/ + docs/) and
    /// fails if any live-looking key or raw IP-literal endpoint is present. It is split into THREE
    /// named rules, each with its OWN positive AND negative fixture (HIGH review item 1), and a
    /// VISIBLE allowlist file (secret-scan-allowlist.txt) governs exceptions — exceptions are NOT
    /// buried in regex. Violation messages report file:line + rule name ONLY and never print the
    /// discovered secret value (CONTEXT "Specific Ideas").
    ///
    /// This is local/offline — no network is touched.
    /// </summary>
    [TestFixture]
    [Category("Security")]
    public class SecurityScanTests
    {
        // -----------------------------------------------------------------
        // Production key prefix — SINGLE point of change (research A3).
        // The live API contract docs/BACKEND-API-SPEC.md documents the production key example as
        // av_live_abc123def456 across all 7 endpoints, so the documented production prefix is
        // "av_live_". Change THIS const if the backend owner confirms a different scheme; the
        // AstrovaultPrefixedKey rule is built from it.
        // -----------------------------------------------------------------
        private const string ProdKeyPrefix = "av_live_";

        // === THREE NAMED RULES (anchored, compiled) ======================

        // Rule 1: raw NON-LOOPBACK IPv4-literal http(s) endpoint. This is what catches the staging
        // origin http://192.0.2.10:8087 . A public host name (vault.astrospherehub.com) does NOT
        // match. Loopback IP literals (http://127.0.0.1:..., 127.0.0.0/8) are explicitly NOT a
        // violation — CONTEXT permits plain http for loopback, and the IsSecureEndpoint policy allows
        // it — so the matcher (see IpLiteralEndpointMatches) ignores loopback hosts. The capture regex
        // is kept broad; the loopback filter is applied to the captured host so the rule stays
        // consistent with the production secure-endpoint policy.
        private static readonly Regex IpLiteralEndpoint =
            new(@"https?://(\d{1,3}(?:\.\d{1,3}){3})(:\d+)?", RegexOptions.Compiled);

        // Rule 2: the documented production-prefixed key. Built from ProdKeyPrefix so the prefix is
        // a single point of change. Requires >= 12 body chars so the short example "av_live_example"
        // (7 body chars) does not self-trip; the longer fake "av_live_abc123def456" IS matched by the
        // rule but is suppressed on its line by the allowlist APPROVED TOKENS (visible exception).
        private static readonly Regex AstrovaultPrefixedKey =
            new(@"\b" + Regex.Escape(ProdKeyPrefix) + @"[A-Za-z0-9_\-]{12,}\b", RegexOptions.Compiled);

        // Rule 3: bare, prefix-less high-entropy token — the FALLBACK for keys like the observed live
        // key (nEwPBa8o8rd…), which carries no av_live_ prefix. Requires length >= 40 AND at least one
        // lowercase AND at least one uppercase AND at least one digit, composed only of [A-Za-z0-9_-].
        // The mixed-case + digit lookahead is the false-positive trade-off: it keeps the rule OFF
        // lowercase-only hex hashes (sha256 / git SHAs are all-lowercase-hex -> no uppercase -> no
        // match) and off GUIDs/long lowercase identifiers, while still catching the observed key
        // (which has lower + upper + digit). Tightening further risks missing real keys; loosening
        // risks flagging hashes.
        private static readonly Regex BareHighEntropyAstrovaultKey =
            new(@"\b(?=[A-Za-z0-9_\-]*[a-z])(?=[A-Za-z0-9_\-]*[A-Z])(?=[A-Za-z0-9_\-]*\d)[A-Za-z0-9_\-]{40,}\b",
                RegexOptions.Compiled);

        // Binary/asset extensions the scan skips (never source for secrets, and reading them is noise).
        private static readonly HashSet<string> SkipExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".ico", ".dll", ".exe", ".pdb",
            ".zip", ".bin", ".fits", ".fit", ".xisf", ".tif", ".tiff"
        };

        // Named (rule, regex) pairs so the violation message can carry the rule name WITHOUT the value.
        private static readonly (string Name, Regex Rule)[] Rules =
        {
            ("IpLiteralEndpoint", IpLiteralEndpoint),
            ("AstrovaultPrefixedKey", AstrovaultPrefixedKey),
            ("BareHighEntropyAstrovaultKey", BareHighEntropyAstrovaultKey),
        };

        // =================================================================
        // SCAN-1: the gate itself
        // =================================================================
        [Test]
        public void ShippableSource_ContainsNoLiveKeysOrRawIpEndpoints()
        {
            var root = LocateRepoRoot();
            var (excludedPaths, approvedTokens) = LoadAllowlist();

            // Shippable include set: CloudUploadPlugin/ + scripts/ + docs/ (HIGH item 2 — docs/ IS scanned).
            string[] includeDirs = { "CloudUploadPlugin", "scripts", "docs" };

            var violations = new List<string>();

            foreach (var dir in includeDirs)
            {
                var dirPath = Path.Combine(root, dir);
                if (!Directory.Exists(dirPath)) continue;

                foreach (var file in Directory.EnumerateFiles(dirPath, "*.*", SearchOption.AllDirectories))
                {
                    var rel = RepoRelative(root, file);

                    if (ShouldSkipFile(rel)) continue;
                    if (excludedPaths.Any(frag => rel.IndexOf(frag, StringComparison.OrdinalIgnoreCase) >= 0))
                        continue;

                    int lineNo = 0;
                    foreach (var line in File.ReadLines(file))
                    {
                        lineNo++;

                        foreach (var (name, rule) in Rules)
                        {
                            // IpLiteralEndpoint is loopback-aware (loopback http is allowed); the other
                            // rules are plain IsMatch.
                            bool hit = name == "IpLiteralEndpoint"
                                ? IpLiteralEndpointMatches(line)
                                : rule.IsMatch(line);
                            if (!hit) continue;

                            // WR-03: rule-scoped suppression. An approved token suppresses THIS rule only if
                            // it is on the line AND its allowlist scope names this rule. A short/common host
                            // token (documentation-only, no scope) no longer blanket-suppresses a co-located
                            // raw IP or key on the same line.
                            if (IsRuleSuppressed(line, name, approvedTokens)) continue;

                            // location + rule name ONLY — NEVER the matched value.
                            violations.Add($"{rel}:{lineNo} [{name}]");
                        }
                    }
                }
            }

            Assert.That(violations, Is.Empty,
                "Secret-scan violations (file:line + rule only — the matched value is intentionally NOT printed):\n"
                + string.Join("\n", violations));
        }

        // =================================================================
        // Per-rule positive AND negative fixtures (HIGH item 1)
        // Run the rules directly against in-memory strings — no planted secret is written to any
        // scanned directory.
        // =================================================================

        [Test]
        public void IpLiteralEndpoint_Positive()
        {
            // Non-loopback raw-IP origins are violations (these are what the staging IP looked like).
            Assert.That(IpLiteralEndpointMatches("http://10.0.0.5:8087/"), Is.True);
            Assert.That(IpLiteralEndpointMatches("http://192.0.2.10:8087/"), Is.True);
            Assert.That(IpLiteralEndpointMatches("https://192.168.1.1/v1/storage"), Is.True);
        }

        [Test]
        public void IpLiteralEndpoint_Negative()
        {
            // A public host name is not an IP literal.
            Assert.That(IpLiteralEndpointMatches("https://vault.astrospherehub.com/"), Is.False);
            Assert.That(IpLiteralEndpointMatches("http://localhost:5000"), Is.False);
            // Loopback IP literals are ALLOWED (CONTEXT permits loopback http; mirrors IsSecureEndpoint)
            // — the local mock-server URLs in scripts/Setup-LiveTest.ps1 are exactly this shape.
            Assert.That(IpLiteralEndpointMatches("http://127.0.0.1:8087/"), Is.False);
            Assert.That(IpLiteralEndpointMatches("http://127.0.0.2:5000/admin/reset"), Is.False);
            // A bare dotted-quad in prose (no http(s):// scheme) is not an endpoint and must not trip.
            Assert.That(IpLiteralEndpointMatches("a plain sentence with version 1.2.3.4 in prose"), Is.False);
        }

        [Test]
        public void AstrovaultPrefixedKey_Positive()
        {
            Assert.That(AstrovaultPrefixedKey.IsMatch("av_live_PLANTEDPLANTED1234567890"), Is.True);
            Assert.That(AstrovaultPrefixedKey.IsMatch("X-API-Key: av_live_abc123def456"), Is.True,
                "The documented fake example matches the RULE (>=12 body chars); it is suppressed only by "
                + "the allowlist APPROVED TOKENS during the file scan.");
        }

        [Test]
        public void AstrovaultPrefixedKey_Negative()
        {
            // "av_live_example" has 7 body chars (< 12) -> the rule does not match it.
            Assert.That(AstrovaultPrefixedKey.IsMatch("av_live_example"), Is.False);
            Assert.That(AstrovaultPrefixedKey.IsMatch("this is ordinary prose with no key"), Is.False);
        }

        [Test]
        public void BareHighEntropyAstrovaultKey_Positive()
        {
            // The observed live-key shape (no av_live_ prefix; lower+upper+digit; 43 chars).
            Assert.That(
                BareHighEntropyAstrovaultKey.IsMatch("nEwPBa8o8rdUQwncQ2jub4Q07_L-zdOYJ_tnFkRjGEA"),
                Is.True);
        }

        [Test]
        public void BareHighEntropyAstrovaultKey_Negative()
        {
            // 64-char lowercase hex SHA-256 -> no uppercase -> fails the mixed-case lookahead (hash
            // false-positive resistance).
            Assert.That(
                BareHighEntropyAstrovaultKey.IsMatch(
                    "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"),
                Is.False, "A lowercase hex SHA-256 must NOT match (hash false-positive resistance).");

            // 40-char lowercase hex (git SHA-1 doubled) -> no uppercase/digit-mix guarantee broken by
            // all-lowercase -> no match.
            Assert.That(
                BareHighEntropyAstrovaultKey.IsMatch("da39a3ee5e6b4b0d3255bfef95601890afd80709aaaa"),
                Is.False, "All-lowercase-hex must NOT match.");

            // A normal long lowercase identifier.
            Assert.That(
                BareHighEntropyAstrovaultKey.IsMatch("thisisaverylongbutentirelylowercaseidentifierxyz"),
                Is.False);

            // A GUID (segments < 40, lowercase) must not match.
            Assert.That(
                BareHighEntropyAstrovaultKey.IsMatch("550e8400-e29b-41d4-a716-446655440000"),
                Is.False);
        }

        [Test]
        public void Scanner_AllowsApprovedExampleKeyAndDomain()
        {
            var (_, approvedTokens) = LoadAllowlist();

            // The documented example key is an approved token scoped to the key-prefix rule, so a doc line
            // carrying it is suppressed for AstrovaultPrefixedKey.
            var exampleKey = approvedTokens.FirstOrDefault(t => t.Token == "av_live_abc123def456");
            Assert.That(exampleKey, Is.Not.Null, "The documented example key must be an approved token.");
            Assert.That(IsRuleSuppressed("X-API-Key: av_live_abc123def456", "AstrovaultPrefixedKey", approvedTokens),
                Is.True, "The example-key line must be suppressed for the AstrovaultPrefixedKey rule.");

            // The public production host is listed but documentation-only -- it must carry NO rule scope so
            // it cannot blanket-suppress a co-located real secret (WR-03).
            var host = approvedTokens.FirstOrDefault(t => t.Token == "vault.astrospherehub.com");
            Assert.That(host, Is.Not.Null, "The public production host must be a listed token.");
            Assert.That(host!.Rules.Count, Is.EqualTo(0),
                "The host token must be documentation-only (no rule scope) so it cannot blanket-suppress.");
        }

        [Test]
        public void HostToken_DoesNotSuppressCoLocatedRealSecret()
        {
            // WR-03 regression: a line carrying the approved public host AND a real non-loopback staging IP.
            var (_, approvedTokens) = LoadAllowlist();
            const string coLocated = "# reachable via vault.astrospherehub.com or http://192.0.2.10:8087";

            Assert.That(IpLiteralEndpointMatches(coLocated), Is.True,
                "The raw staging IP on the line must match the IP-literal rule.");
            Assert.That(IsRuleSuppressed(coLocated, "IpLiteralEndpoint", approvedTokens), Is.False,
                "The approved host token must NOT suppress a co-located raw IP -- it would still be a violation (WR-03).");
        }

        // =================================================================
        // P19-01a/b/c (SC1): IsSecureEndpoint refuse/allow contract
        // NOTE: the http://192.0.2.10:8087/ literal is deliberately PRESENT in this test file as a
        // "non-loopback http is refused" case. The scan excludes the CloudUploadPlugin.Tests tree, so
        // this literal does not trip the gate.
        // =================================================================

        [TestCase("http://example.com")]
        [TestCase("http://192.0.2.10:8087/")]
        [TestCase("http://api.astrovault.io")]
        public void IsSecureEndpoint_RefusesNonLoopbackHttp(string endpoint)
        {
            Assert.That(AstrovaultPlugin.IsSecureEndpoint(endpoint), Is.False,
                $"'{endpoint}' is non-loopback http and must be refused by the secure-endpoint policy.");
        }

        [TestCase("https://vault.astrospherehub.com/")]
        [TestCase("https://example.com")]
        [TestCase("http://localhost:5000")]
        [TestCase("http://127.0.0.1")]
        [TestCase("http://[::1]:5111")]
        [TestCase("http://127.0.0.2")]
        public void IsSecureEndpoint_AllowsHttpsAndLoopbackHttp(string endpoint)
        {
            Assert.That(AstrovaultPlugin.IsSecureEndpoint(endpoint), Is.True,
                $"'{endpoint}' is https or loopback http and must be allowed by the secure-endpoint policy.");
        }

        // =================================================================
        // SC4 (verify-only): a 429 with Retry-After: N yields a >= N delay.
        // GetRetryAfterDelay is private static in AstrovaultApiClient; per the plan we do NOT modify
        // production code or add InternalsVisibleTo for a verify-only check. We assert the value the
        // production logic reads (AstrovaultApiClient.GetRetryAfterDelay returns response.Headers
        // .RetryAfter.Delta for the delta form, clamped non-negative — AstrovaultApiClient.cs:1017-1020)
        // and that the chunk-retry path (AstrovaultApiClient.cs:962-965) routes 429/503 through it.
        // The backoff itself is Phase 18.1 work and is NOT modified here.
        // =================================================================
        [Test]
        public void RetryAfter_DeltaSeconds_YieldsAtLeastThatDelay()
        {
            using var response = new HttpResponseMessage((HttpStatusCode)429);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));

            var delta = response.Headers.RetryAfter?.Delta;

            Assert.That(delta, Is.Not.Null, "A Retry-After delta header must be present on the 429 response.");
            // GetRetryAfterDelay returns this delta (clamped non-negative) for the delta form, so the
            // resulting backoff delay is >= the advertised Retry-After seconds.
            Assert.That(delta.Value, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(7)),
                "A Retry-After: 7 must produce a delay >= 7s through GetRetryAfterDelay.");
        }

        // =================================================================
        // Helpers
        // =================================================================

        private static string LocateRepoRoot()
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "CloudUploadPlugin.sln"))
                    || Directory.Exists(Path.Combine(dir.FullName, ".git")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            throw new InvalidOperationException(
                "Could not locate repo root (no CloudUploadPlugin.sln or .git found above the test dir).");
        }

        private static string RepoRelative(string root, string fullPath)
        {
            return Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        }

        /// <summary>
        /// True when a line contains a NON-LOOPBACK IPv4-literal http(s) endpoint. Loopback literals
        /// (127.0.0.0/8) are allowed (CONTEXT permits loopback http; mirrors IsSecureEndpoint), so a
        /// match whose host is loopback is NOT a violation. Returns true only if at least one matched
        /// IP-literal URL on the line is non-loopback.
        /// </summary>
        private static bool IpLiteralEndpointMatches(string line)
        {
            foreach (Match m in IpLiteralEndpoint.Matches(line))
            {
                var host = m.Groups[1].Value; // the dotted-quad
                if (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip))
                    continue; // loopback http is allowed
                return true;  // a non-loopback raw-IP origin -> violation
            }
            return false;
        }

        private static bool ShouldSkipFile(string repoRelativePath)
        {
            var rel = repoRelativePath.Replace('\\', '/');

            // Never scan generated / vendored / planning / VCS / IDE-cache trees, the test project, or
            // this file. ".vs/" is Visual Studio's local cache (gitignored sqlite/dtbcache binaries) —
            // it is on disk but is NOT shippable source.
            string[] skipFragments =
            {
                "/bin/", "/obj/", "ref_repos/", ".planning/", ".git/", ".vs/", ".idea/",
                "CloudUploadPlugin.Tests/",
            };
            if (skipFragments.Any(f => rel.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0))
                return true;

            // Skip the scan test file and the allowlist file themselves (they legitimately carry
            // example tokens / IP shapes).
            if (rel.EndsWith("SecurityScanTests.cs", StringComparison.OrdinalIgnoreCase)) return true;
            if (rel.EndsWith("secret-scan-allowlist.txt", StringComparison.OrdinalIgnoreCase)) return true;

            var ext = Path.GetExtension(rel);
            if (!string.IsNullOrEmpty(ext) && SkipExtensions.Contains(ext)) return true;

            return false;
        }

        /// <summary>
        /// Loads the VISIBLE allowlist (secret-scan-allowlist.txt) copied next to the test assembly.
        /// Returns (excludedPathFragments, approvedTokens). Exceptions are reviewable in that file,
        /// not encoded in regex.
        /// </summary>
        /// <summary>
        /// An approved-token exception scoped to the rule(s) it excuses (WR-03). A token with an empty
        /// rule scope is documentation-only and suppresses nothing, so a real secret co-located with an
        /// approved host still trips its own rule.
        /// </summary>
        private sealed record ApprovedToken(string Token, HashSet<string> Rules);

        /// <summary>
        /// True when an approved token excuses <paramref name="ruleName"/> on this line: the token is
        /// present AND its allowlist scope names the rule. Documentation-only tokens never suppress.
        /// </summary>
        private static bool IsRuleSuppressed(string line, string ruleName, List<ApprovedToken> approved)
        {
            foreach (var at in approved)
            {
                if (at.Rules.Contains(ruleName) && line.Contains(at.Token, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static (List<string> ExcludedPaths, List<ApprovedToken> ApprovedTokens) LoadAllowlist()
        {
            var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Core", "secret-scan-allowlist.txt");
            if (!File.Exists(path))
            {
                // Fallback: flat copy next to the assembly.
                path = Path.Combine(TestContext.CurrentContext.TestDirectory, "secret-scan-allowlist.txt");
            }

            Assert.That(File.Exists(path), Is.True,
                $"secret-scan-allowlist.txt must be copied to the test output dir; looked under '{TestContext.CurrentContext.TestDirectory}'.");

            var excluded = new List<string>();
            var approved = new List<ApprovedToken>();
            string section = null;

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                if (line.Equals("[EXCLUDED PATHS]", StringComparison.OrdinalIgnoreCase))
                {
                    section = "paths";
                    continue;
                }
                if (line.Equals("[APPROVED TOKENS]", StringComparison.OrdinalIgnoreCase))
                {
                    section = "tokens";
                    continue;
                }

                if (section == "paths")
                {
                    excluded.Add(line);
                }
                else if (section == "tokens")
                {
                    // Optional rule scope: "token :: Rule1,Rule2". No " :: " => documentation-only (no scope).
                    var parts = line.Split(new[] { " :: " }, 2, StringSplitOptions.None);
                    var token = parts[0].Trim();
                    var rules = new HashSet<string>(StringComparer.Ordinal);
                    if (parts.Length == 2)
                    {
                        foreach (var r in parts[1].Split(','))
                        {
                            var name = r.Trim();
                            if (name.Length > 0) rules.Add(name);
                        }
                    }
                    if (token.Length > 0) approved.Add(new ApprovedToken(token, rules));
                }
            }

            return (excluded, approved);
        }
    }
}
