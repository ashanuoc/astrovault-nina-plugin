using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Astrovault;
using Astrovault.Core;
using Astrovault.Interfaces;
using Astrovault.Models;
using CloudUploadPlugin.Tests.Core; // MockHttpMessageHandler (real HttpMessageHandler seam)
using CloudUploadPlugin.Tests.Helpers; // TestDataFactory
using Moq;

namespace Astrovault.Tests.Core
{
    /// <summary>
    /// Redaction + send-boundary security tests for AstrovaultApiClient (Phase 19-05). Covers:
    ///   REDACT-2 (behavioral): a 500 whose server-set ReasonPhrase = "INTERNAL-SECRET-REASON" yields
    ///            an UploadResult.ErrorMessage containing "500" but NOT the reason text — proving the
    ///            server-controllable ReasonPhrase cannot flow into UploadResult. This is a REAL handler
    ///            -seam proof (MockHttpMessageHandler returns the 500), NOT a source-text assertion.
    ///   P19-01d (SendBoundary, no network): a non-loopback http base makes UploadFileAsync return
    ///            FailPermanent("HTTPS required outside localhost") WITHOUT the injected handler's
    ///            SendAsync ever being invoked (no socket opened). A throw-if-invoked handler proves it.
    ///   LEAK-SCAN (MED item 3): a grep-driven source-scan enumerates every leak-prone interpolation
    ///            term (ReasonPhrase, raw {body}, remote {url}, LocalPath in Logger/Fail strings,
    ///            AccountName/accountName, raw BaseAddress) across AstrovaultApiClient.cs, UploadManager.cs,
    ///            AuthManager.cs, and AstrovaultPlugin.cs and asserts each remaining occurrence is redacted
    ///            or a proven non-leaking use. Reports file:lineNo [term] ONLY — never the surrounding value.
    ///
    /// The source-scan is the MED-3 acceptance GUARD; the behavioral ReasonPhrase test above is the
    /// PRIMARY proof for that specific leak. Where a handler-driven behavioral test for a given site is
    /// impractical, the source-scan still enforces redaction.
    /// </summary>
    [TestFixture]
    [Category("Security")]
    public class ApiClientRedactionTests
    {
        private string? tempFilePath;

        [TearDown]
        public void TearDown()
        {
            if (tempFilePath != null && File.Exists(tempFilePath))
            {
                try { File.Delete(tempFilePath); } catch { /* best-effort */ }
            }
        }

        /// <summary>Creates a small (&lt; 5 MB) temp file so UploadFileAsync's pre-branch re-stat routes
        /// to the single-file path (where the ReasonPhrase error string is built).</summary>
        private string CreateSmallTempFile()
        {
            tempFilePath = Path.GetTempFileName();
            File.WriteAllBytes(tempFilePath, new byte[1024]); // 1 KB -> single-file path
            return tempFilePath;
        }

        private static Mock<IAuthManager> AuthReturningKey()
        {
            var mock = new Mock<IAuthManager>();
            mock.Setup(a => a.GetApiKey()).Returns("test-api-key"); // non-empty -> auth guard passes
            return mock;
        }

        // ===== REDACT-2: ReasonPhrase cannot reach UploadResult (BEHAVIORAL, real handler seam) =====

        [Test]
        public async Task SingleUpload_ServerReasonPhrase_DoesNotLeakIntoUploadResultErrorMessage()
        {
            // Arrange: a real handler seam returns a 500 whose ReasonPhrase carries a distinctive secret.
            // If the production code echoed ReasonPhrase, that secret would surface in ErrorMessage.
            var handler = new MockHttpMessageHandler();
            handler.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                ReasonPhrase = "INTERNAL-SECRET-REASON",
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });

            var auth = AuthReturningKey();
            // https base passes the send-boundary gate so we exercise the actual upload path.
            using var client = new AstrovaultApiClient(auth.Object, "https://api.test.io", handler);

            var filePath = CreateSmallTempFile();
            var job = TestDataFactory.CreateUploadJob(localPath: filePath);

            // Act
            var result = await client.UploadFileAsync(job, new Progress<double>(), CancellationToken.None);

            // Assert: the error carries the NUMERIC status, never the server-set ReasonPhrase.
            Assert.That(result.Success, Is.False, "A 500 response must produce a failed result.");
            Assert.That(result.ErrorMessage, Does.Contain("500"),
                "The upload-failure error string must carry the numeric status.");
            Assert.That(result.ErrorMessage, Does.Not.Contain("INTERNAL-SECRET-REASON"),
                "Server-controllable ReasonPhrase must NOT flow into UploadResult.ErrorMessage (SC6).");
        }

        // ===== P19-01d: upload-path send-boundary gate (no socket opened) =====

        [Test]
        public async Task UploadFileAsync_NonLoopbackHttpBase_SendBoundary_RefusesWithoutInvokingHandler()
        {
            // Arrange: a throw-if-invoked handler proves the gate returns BEFORE any request is created.
            // A non-empty key means the Not-authenticated guard passes, so it is the HTTPS gate that fires.
            var handler = new ThrowingHandler();
            var auth = AuthReturningKey();
            using var client = new AstrovaultApiClient(auth.Object, "http://example.com/", handler);

            var filePath = CreateSmallTempFile();
            var job = TestDataFactory.CreateUploadJob(localPath: filePath);

            // Act
            var result = await client.UploadFileAsync(job, new Progress<double>(), CancellationToken.None);

            // Assert: permanent refusal with the exact message, and SendAsync was NEVER reached (no socket).
            Assert.That(result.Success, Is.False, "Non-loopback http must be refused.");
            Assert.That(result.IsPermanent, Is.True,
                "The refusal must be permanent so the job does not burn transient retries.");
            Assert.That(result.ErrorMessage, Is.EqualTo("HTTPS required outside localhost"),
                "The send-boundary refusal must use the exact user-facing message.");
            Assert.That(handler.SendInvoked, Is.False,
                "The gate must return before any request is created -- the handler's SendAsync must never run.");
        }

        // ===== WR-02: TestConnectionAsync also gates the key behind the HTTPS send-boundary =====

        [Test]
        public async Task TestConnectionAsync_NonLoopbackHttpBase_RefusesWithoutInvokingHandler()
        {
            // A record-if-invoked handler proves the gate returns BEFORE the X-API-Key request is sent.
            var handler = new ThrowingHandler();
            var auth = AuthReturningKey();
            using var client = new AstrovaultApiClient(auth.Object, "http://example.com/", handler);

            var ok = await client.TestConnectionAsync();

            Assert.That(ok, Is.False, "TestConnectionAsync must refuse a non-loopback http endpoint.");
            Assert.That(handler.SendInvoked, Is.False,
                "The send-boundary gate must return before the key request is created -- SendAsync must never run (WR-02).");
        }

        // ===== LEAK-SCAN (MED item 3): grep-driven source-scan across the four files =====

        [Test]
        public void UploadLifecycleSources_HaveNoUnredactedLeakSites()
        {
            var root = SourceRoot.Locate();

            // The four files whose upload-lifecycle logging/error construction must not leak.
            var apiClient = SourceFile.Load(root, "CloudUploadPlugin/Core/AstrovaultApiClient.cs");
            var uploadManager = SourceFile.Load(root, "CloudUploadPlugin/Core/UploadManager.cs");
            var authManager = SourceFile.Load(root, "CloudUploadPlugin/Core/AuthManager.cs");
            var plugin = SourceFile.Load(root, "CloudUploadPlugin/AstrovaultPlugin.cs");
            // WR-01: these two also log job.LocalPath on the enqueue path -- the LocalPath scan must cover them.
            var queueRepo = SourceFile.Load(root, "CloudUploadPlugin/Data/UploadQueueRepository.cs");
            var imageSaveListener = SourceFile.Load(root, "CloudUploadPlugin/Integration/ImageSaveListener.cs");

            var allFour = new[] { apiClient, uploadManager, authManager, plugin };
            var violations = new List<string>();

            // --- ReasonPhrase: zero `response.ReasonPhrase` in AstrovaultApiClient.cs ---
            foreach (var line in apiClient.CodeLines)
            {
                if (line.Text.Contains("response.ReasonPhrase", StringComparison.Ordinal))
                    violations.Add($"{apiClient.RelPath}:{line.Number} [ReasonPhrase]");
            }

            // --- Raw body: no Logger line in AstrovaultApiClient.cs interpolates a raw body variable.
            // The responseBody READ (ReadAsStringAsync) and JSON parse are NOT logs and are allowed; we
            // flag only a Logger.* line that interpolates {body} or {responseBody}. ---
            foreach (var line in apiClient.CodeLines)
            {
                if (IsLoggerLine(line.Text) &&
                    (InterpolatesToken(line.Text, "body") || InterpolatesToken(line.Text, "responseBody")))
                {
                    violations.Add($"{apiClient.RelPath}:{line.Number} [body]");
                }
            }

            // --- Remote URL: no Logger line in AstrovaultApiClient.cs interpolates a {url} that derives
            // from RemoteUrl/RemotePath. The RemoteUrl/RemotePath PROPERTY declarations are allowed (they
            // are not Logger lines). We flag any Logger.* line that interpolates {url}. ---
            foreach (var line in apiClient.CodeLines)
            {
                if (IsLoggerLine(line.Text) && InterpolatesToken(line.Text, "url"))
                    violations.Add($"{apiClient.RelPath}:{line.Number} [url]");
            }

            // --- LocalPath: every job.LocalPath inside a Logger.* call OR a FailPermanent(/Fail( string in
            // AstrovaultApiClient.cs and UploadManager.cs must be REDACTED to filename-only. The two
            // blessed redaction idioms are LogSanitizer.MaskPath( and the in-file precedent
            // Path.GetFileName( (which the plan explicitly lists as an allowed non-leaking use and which
            // Task 2 uses at the single-file success log). Non-log uses (File.Exists, FileInfo,
            // OpenFileWithRetryAsync, bare GetFileName) are allowed. ---
            foreach (var src in new[] { apiClient, uploadManager, queueRepo, imageSaveListener })
            {
                foreach (var line in src.CodeLines)
                {
                    bool isLogOrFail = IsLoggerLine(line.Text)
                        || line.Text.Contains("FailPermanent(", StringComparison.Ordinal)
                        || line.Text.Contains(".Fail(", StringComparison.Ordinal);

                    bool redacted = line.Text.Contains("MaskPath(", StringComparison.Ordinal)
                        || line.Text.Contains("GetFileName(", StringComparison.Ordinal);

                    if (isLogOrFail
                        && line.Text.Contains("job.LocalPath", StringComparison.Ordinal)
                        && !redacted)
                    {
                        violations.Add($"{src.RelPath}:{line.Number} [LocalPath]");
                    }
                }
            }

            // --- AccountName/accountName: no Logger line in ANY of the four files interpolates
            // AccountName/accountName. The MaskAccountName( redactor call is allowed; a raw {...AccountName}
            // on a Logger line is a violation. ---
            foreach (var src in allFour)
            {
                foreach (var line in src.CodeLines)
                {
                    if (!IsLoggerLine(line.Text)) continue;
                    if (line.Text.Contains("MaskAccountName(", StringComparison.Ordinal)) continue;
                    if (InterpolatesToken(line.Text, "AccountName") || InterpolatesToken(line.Text, "accountName"))
                        violations.Add($"{src.RelPath}:{line.Number} [AccountName]");
                }
            }

            // --- Raw BaseAddress: no Logger line interpolates BaseAddress. The ctor
            // `BaseAddress = new Uri(...)` assignments are NOT Logger lines and are allowed. ---
            foreach (var src in allFour)
            {
                foreach (var line in src.CodeLines)
                {
                    if (IsLoggerLine(line.Text) && InterpolatesToken(line.Text, "BaseAddress"))
                        violations.Add($"{src.RelPath}:{line.Number} [BaseAddress]");
                }
            }

            Assert.That(violations, Is.Empty,
                "Unredacted leak sites (file:line + term only -- the surrounding value is intentionally NOT printed):\n"
                + string.Join("\n", violations));
        }

        // =================================================================
        // Helpers
        // =================================================================

        /// <summary>True when a line is a NINA Logger.* call (Logger.Info/Warning/Error/Debug/Trace).</summary>
        private static bool IsLoggerLine(string line) => line.Contains("Logger.", StringComparison.Ordinal);

        /// <summary>
        /// True when an interpolated-string placeholder references <paramref name="token"/> as an
        /// identifier — i.e. a "{...token...}" placeholder where token appears on a word boundary. This
        /// catches `{url}`, `{body}`, `{response.ReasonPhrase}`, `{job.AccountName}`,
        /// `{httpClient.BaseAddress}` while ignoring unrelated substrings. Comments are excluded so the
        /// SC6 explanatory comment that names ReasonPhrase does not self-trip.
        /// </summary>
        private static bool InterpolatesToken(string line, string token)
        {
            var code = StripLineComment(line);
            // Match a {...} interpolation hole that contains `token` on a word boundary.
            var pattern = @"\{[^{}]*\b" + Regex.Escape(token) + @"\b[^{}]*\}";
            return Regex.IsMatch(code, pattern);
        }

        /// <summary>Drops a trailing // line comment so commentary that names a leak term is not flagged.</summary>
        private static string StripLineComment(string line)
        {
            var idx = line.IndexOf("//", StringComparison.Ordinal);
            return idx >= 0 ? line.Substring(0, idx) : line;
        }

        /// <summary>Throw-if-invoked handler: records whether SendAsync was reached and throws if it is.</summary>
        private sealed class ThrowingHandler : HttpMessageHandler
        {
            public bool SendInvoked { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                SendInvoked = true;
                throw new InvalidOperationException("network must not be reached");
            }
        }

        /// <summary>Locates the repo root (mirrors SecurityScanTests) so source files can be read as text.</summary>
        private static class SourceRoot
        {
            public static string Locate()
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
                    "Could not locate repo root (no CloudUploadPlugin.sln or .git above the test dir).");
            }
        }

        private sealed record CodeLine(int Number, string Text);

        /// <summary>A source file loaded as numbered lines for the leak-site scan.</summary>
        private sealed class SourceFile
        {
            public string RelPath { get; }
            public IReadOnlyList<CodeLine> CodeLines { get; }

            private SourceFile(string relPath, IReadOnlyList<CodeLine> lines)
            {
                RelPath = relPath;
                CodeLines = lines;
            }

            public static SourceFile Load(string root, string repoRelativePath)
            {
                var full = Path.Combine(root, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));
                Assert.That(File.Exists(full), Is.True,
                    $"Leak-scan target source file must exist: {repoRelativePath}");

                var lines = new List<CodeLine>();
                int n = 0;
                foreach (var text in File.ReadLines(full))
                {
                    n++;
                    lines.Add(new CodeLine(n, text));
                }
                return new SourceFile(repoRelativePath, lines);
            }
        }
    }
}
