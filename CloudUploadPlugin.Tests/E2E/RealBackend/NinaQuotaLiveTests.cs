using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Astrovault.Core;
using Astrovault.Data;
using Astrovault.Models;
using CloudUploadPlugin.Tests.E2E.Fixtures;
using CloudUploadPlugin.Tests.E2E.Helpers;
using Newtonsoft.Json;
using NUnit.Framework;

namespace CloudUploadPlugin.Tests.E2E.RealBackend
{
    /// <summary>
    /// One-shot, strictly-ordered live verification of the storage-quota plugin against the REAL dev
    /// backend (via a local loopback TCP relay -- see the ASTROVAULT_BACKEND_URL env var read by
    /// <see cref="RealBackendFixture"/>). This suite is DELIBERATELY destructive to the dev account's
    /// remaining storage budget: it spends it down on purpose so it ends with the account nearly
    /// exhausted, mirroring the state a human tester wants for a manual "already over limit" NINA
    /// session. It must NEVER run as part of the normal non-E2E suite -- it shares
    /// <see cref="RealBackendFixture"/>'s SetUpFixture (namespace-scoped) and the "RealBackend"
    /// category, so ASTROVAULT_API_KEY unset (the default everywhere except this deliberate live run)
    /// makes every test here Assert.Ignore, exactly like <see cref="RealBackendTests"/>.
    /// <para/>
    /// Every scenario below exercises the REAL <see cref="AstrovaultApiClient"/> production code path
    /// (never a hand-rolled parallel HTTP call) via <see cref="CapturingHandler"/>, a transparent
    /// delegating handler that records the exact wire status + raw body of every request the client
    /// sends without altering what the client itself observes. This gives every assertion below BOTH
    /// the client's typed result (FailureKind/QuotaInfo/etc.) AND the literal wire evidence the live
    /// verification report requires, from a single real network call -- never two.
    /// <para/>
    /// Budget ledger: the account's remaining ~73.5 MB is tracked by a static, run-scoped ledger seeded
    /// from the REAL quota fetched in Scenario 2 and decremented by each upload's exact byte count as
    /// it succeeds. Sizing later scenarios (in particular Scenario 6's exhaust step) off this ledger --
    /// rather than off a live re-fetch -- is deliberate: the backend caches quota numbers for 60s
    /// server-side, so a live re-fetch immediately after a successful upload could under-report true
    /// usage and risk sizing an upload that overspends the budget.
    /// </summary>
    [TestFixture]
    [Category("RealBackend")]
    public class NinaQuotaLiveTests
    {
        private RealBackendFixture Fixture => RealBackendFixture.Instance;
        private string TempDir;

        // ================================================================
        // Run-scoped ledger (static: NUnit creates a fresh instance per [Test], so cross-scenario
        // state that must survive the ordered sequence lives here instead of on instance fields).
        // ================================================================
        private static string ExpectedAccountId;
        private static long LedgerLimitBytes = -1;
        private static long LedgerUsedBytesBaseline = -1;
        private static long RemainingLedgerBytes = -1;
        private static long LastObservedUsedBytes = -1;
        private static long TotalSuccessfulUploadBytes = 0;

        [OneTimeSetUp]
        public void ClassOneTimeSetUp()
        {
            // Reset to sentinel "unset" values at the start of THIS run, independent of any prior
            // process state (relevant only for defense-in-depth; a fresh `dotnet test` process already
            // starts these at their declared defaults).
            ExpectedAccountId = null;
            LedgerLimitBytes = -1;
            LedgerUsedBytesBaseline = -1;
            RemainingLedgerBytes = -1;
            LastObservedUsedBytes = -1;
            TotalSuccessfulUploadBytes = 0;
        }

        [OneTimeTearDown]
        public void ClassOneTimeTearDown()
        {
            TestContext.WriteLine("=== NinaQuotaLiveTests run summary ===");
            TestContext.WriteLine($"Expected accountId: {ExpectedAccountId}");
            TestContext.WriteLine($"Total successful upload bytes this run: {TotalSuccessfulUploadBytes}");
            TestContext.WriteLine($"Ledger remaining bytes (computed, not re-fetched): {RemainingLedgerBytes}");
        }

        [SetUp]
        public void SetUp()
        {
            Assert.That(Fixture, Is.Not.Null,
                "RealBackendFixture.Instance is null -- ASTROVAULT_API_KEY likely not set");
            TempDir = Path.Combine(Path.GetTempPath(), $"astrovault-live-quota-{Guid.NewGuid():N}");
            Directory.CreateDirectory(TempDir);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(TempDir))
                    Directory.Delete(TempDir, recursive: true);
            }
            catch (Exception ex)
            {
                TestContext.WriteLine($"TearDown cleanup warning: {ex.Message}");
            }
        }

        // ================================================================
        // Scenario 1: validate -> 200, valid=true, accountId is a parseable UUID
        // ================================================================

        [Test]
        [Order(1)]
        public async Task Scenario01_Validate_ReturnsValidTrue_WithParseableAccountId()
        {
            var handler = new CapturingHandler();
            var authStub = new TestAuthStub { ApiKey = Fixture.ApiKey };
            var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl, handler);
            try
            {
                var connected = await client.TestConnectionAsync();
                var capture = LastCapture(handler);
                LogCapture("Scenario 1: auth/validate", capture);

                Assert.That(connected, Is.True,
                    "TestConnectionAsync should return true with the real dev API key.");
                Assert.That(capture.StatusCode, Is.EqualTo(200));

                var parsed = JsonConvert.DeserializeObject<ValidateBody>(capture.Body);
                Assert.That(parsed, Is.Not.Null, "validate body must be parseable JSON");
                Assert.That(parsed.Valid, Is.True);
                Assert.That(parsed.AccountId, Is.Not.Null.And.Not.Empty, "accountId must be present on a valid key");
                Assert.That(Guid.TryParse(parsed.AccountId, out _), Is.True,
                    $"accountId '{parsed.AccountId}' must be a parseable UUID");

                ExpectedAccountId = parsed.AccountId;
            }
            finally
            {
                client.Dispose();
            }
        }

        // ================================================================
        // Scenario 2: GetQuotaAsync -> parses the real body; plausible numbers; seeds the ledger
        // ================================================================

        [Test]
        [Order(2)]
        public async Task Scenario02_GetQuotaAsync_ParsesPlausibleRealNumbers_AndSeedsLedger()
        {
            Assert.That(ExpectedAccountId, Is.Not.Null, "Scenario 1 must run first and establish accountId");

            var handler = new CapturingHandler();
            var authStub = new TestAuthStub { ApiKey = Fixture.ApiKey };
            var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl, handler);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var quota = await client.GetQuotaAsync(cts.Token);
                var capture = LastCapture(handler);
                LogCapture("Scenario 2: GET quota", capture);

                Assert.That(quota, Is.Not.Null, "GetQuotaAsync should parse a real quota response");
                Assert.That(capture.StatusCode, Is.EqualTo(200));

                Assert.That(quota.AccountId, Is.EqualTo(ExpectedAccountId),
                    "quota's accountId should match validate's accountId");

                Assert.That(quota.LimitBytes, Is.Not.Null);
                Assert.That(quota.LimitBytes.Value, Is.GreaterThanOrEqualTo(9L * 1024 * 1024 * 1024),
                    "limit should be plausibly ~10 GiB");
                Assert.That(quota.LimitBytes.Value, Is.LessThanOrEqualTo(11L * 1024 * 1024 * 1024),
                    "limit should be plausibly ~10 GiB");

                Assert.That(quota.UsedBytes, Is.Not.Null);
                Assert.That(quota.RemainingBytes, Is.Not.Null);
                Assert.That(quota.RemainingBytes.Value, Is.LessThan(80L * 1024 * 1024),
                    "remaining should be under ~80 MB per the known account state");

                TestContext.WriteLine($"[LIVE QUOTA] accountId={quota.AccountId} plan={quota.PlanName} "
                    + $"used={quota.UsedBytes} limit={quota.LimitBytes} remaining={quota.RemainingBytes}");

                // Seed the run-scoped ledger from THIS live fetch. Every later scenario decrements
                // this ledger by exact successful-upload byte counts rather than re-fetching quota --
                // see the class doc comment for why (60s server-side cache).
                LedgerLimitBytes = quota.LimitBytes.Value;
                LedgerUsedBytesBaseline = quota.UsedBytes.Value;
                RemainingLedgerBytes = quota.RemainingBytes.Value;
                LastObservedUsedBytes = quota.UsedBytes.Value;
            }
            finally
            {
                client.Dispose();
            }
        }

        // ================================================================
        // Scenario 3: single-shot SUCCESS (~1 MB) -- we're under limit
        // ================================================================

        [Test]
        [Order(3)]
        public async Task Scenario03_SingleShotUpload_Succeeds_AndQuotaReflectsGrowth()
        {
            Assert.That(RemainingLedgerBytes, Is.GreaterThan(0),
                "Scenario 2 must have established the ledger baseline");

            const long fileSize = 1L * 1024 * 1024; // ~1 MB
            var filePath = E2ETestFileHelper.CreateTestFile(TempDir, fileSize, "live-single-1mb.fits");

            var handler = new CapturingHandler();
            var authStub = new TestAuthStub { ApiKey = Fixture.ApiKey };
            var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl, handler);
            try
            {
                var job = CreateTestJob(filePath, fileSize, "live-single-1mb.fits", "scenario-03");
                var progress = new Progress<double>();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

                var result = await client.UploadFileAsync(job, progress, cts.Token);
                var capture = LastCapture(handler);
                LogCapture("Scenario 3: single-shot upload", capture);

                Assert.That(result.Success, Is.True,
                    $"Single-shot ~1MB upload should succeed while under budget. Error: {result.ErrorMessage}");

                RecordSuccessfulUpload(fileSize);

                using var quotaCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var quotaAfter = await client.GetQuotaAsync(quotaCts.Token);
                var quotaCapture = LastCapture(handler);
                LogCapture("Scenario 3: quota after 1MB upload", quotaCapture);

                Assert.That(quotaAfter, Is.Not.Null);
                Assert.That(quotaAfter.UsedBytes, Is.Not.Null);
                // 60s server-side cache: DO NOT assert an exact delta, only that usage never regresses.
                Assert.That(quotaAfter.UsedBytes.Value, Is.GreaterThanOrEqualTo(LastObservedUsedBytes),
                    "usedBytes must never regress, even under cache lag");
                LastObservedUsedBytes = Math.Max(LastObservedUsedBytes, quotaAfter.UsedBytes.Value);

                TestContext.WriteLine($"[LIVE QUOTA after 1MB upload] used={quotaAfter.UsedBytes} "
                    + $"remaining={quotaAfter.RemainingBytes} (cache may not reflect this upload yet)");
            }
            finally
            {
                client.Dispose();
            }
        }

        // ================================================================
        // Scenario 4: chunked SUCCESS (~8 MB) -- full initiate/chunks/complete against the real server
        // ================================================================

        [Test]
        [Order(4)]
        public async Task Scenario04_ChunkedUpload_Succeeds_EndToEnd()
        {
            const long fileSize = 8L * 1024 * 1024; // ~8 MB
            var filePath = E2ETestFileHelper.CreateTestFile(TempDir, fileSize, "live-chunked-8mb.fits");

            var handler = new CapturingHandler();
            var authStub = new TestAuthStub { ApiKey = Fixture.ApiKey };
            var queueDir = Path.Combine(TempDir, "queue-scenario4");
            Directory.CreateDirectory(queueDir);
            var queueRepo = new UploadQueueRepository(queueDir);
            var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl, handler, queueRepo);
            try
            {
                var job = CreateTestJob(filePath, fileSize, "live-chunked-8mb.fits", "scenario-04");
                var progress = new Progress<double>();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));

                var result = await client.UploadFileAsync(job, progress, cts.Token);
                LogChunkedCaptures("Scenario 4: chunked upload", handler);

                Assert.That(result.Success, Is.True,
                    $"Chunked ~8MB upload should succeed end-to-end. Error: {result.ErrorMessage}");

                RecordSuccessfulUpload(fileSize);
            }
            finally
            {
                client.Dispose();
            }
        }

        // ================================================================
        // Scenario 5: 507 AT INITIATE, before bytes move (declared size FAR over remaining)
        // ================================================================

        [Test]
        [Order(5)]
        public async Task Scenario05_ChunkedUpload_507AtInitiate_NoBytesMoved()
        {
            const long declaredSize = 200L * 1024 * 1024; // 200 MB -- far over the ~73.5 MB budget
            var filePath = CreateSparseFile(TempDir, declaredSize, "live-oversized-200mb.fits");

            var handler = new CapturingHandler();
            var authStub = new TestAuthStub { ApiKey = Fixture.ApiKey };
            var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl, handler);
            try
            {
                var job = CreateTestJob(filePath, declaredSize, "live-oversized-200mb.fits", "scenario-05");
                var progress = new Progress<double>();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

                var result = await client.UploadFileAsync(job, progress, cts.Token);
                var capture = LastCapture(handler);
                LogCapture("Scenario 5: chunked initiate 507", capture);

                Assert.That(result.Success, Is.False);
                Assert.That(result.FailureKind, Is.EqualTo(FailureKind.PermanentQuota));
                Assert.That(result.StatusCode, Is.EqualTo(507));
                Assert.That(capture.StatusCode, Is.EqualTo(507));

                Assert.That(result.QuotaInfo, Is.Not.Null, "QuotaInfoParser should have parsed the flat 507 body");
                Assert.That(result.QuotaInfo.Flavor, Is.EqualTo(QuotaBlockFlavor.StorageLimitExceeded));
                Assert.That(result.QuotaInfo.UsedBytes, Is.Not.Null);
                Assert.That(result.QuotaInfo.LimitBytes, Is.Not.Null);
                Assert.That(result.QuotaInfo.RemainingBytes, Is.Not.Null);

                // CONTRACT DISCREPANCY (found via live recon against the real relay, 2026-08-04):
                // the a-priori assumption was that remainingBytes on a 507 body is floored to 0. The
                // REAL body instead carries the account's TRUE current remaining capacity (~73.5 MB)
                // -- it is NOT floored to 0 here, because the account itself has not actually reached
                // its limit; only this one oversized DECLARED request would exceed it. Asserting the
                // originally-assumed "== 0" would fail against reality, so this asserts the OBSERVED
                // real contract instead: remainingBytes on this endpoint is the account's genuine
                // current remaining capacity, always >= 0 and never negative.
                Assert.That(result.QuotaInfo.RemainingBytes.Value, Is.GreaterThanOrEqualTo(0),
                    "remainingBytes must never be negative (server floors at 0 only once truly exhausted)");

                // Zero chunk bytes moved: exactly one request (the initiate) should have been sent.
                Assert.That(handler.Captures.Count, Is.EqualTo(1),
                    "Exactly one HTTP request (initiate) should have been sent -- zero chunk PUTs, "
                    + "proving no bytes were moved for a 507-at-initiate rejection");
                Assert.That(handler.Captures[0].Url, Does.Contain("/uploads/initiate"));
            }
            finally
            {
                client.Dispose();
            }
        }

        // ================================================================
        // Scenario 6: EXHAUST -- spend the remaining ledger (minus a safety buffer) via chunked upload
        // ================================================================

        [Test]
        [Order(6)]
        public async Task Scenario06_ExhaustRemainingBudget_ChunkedUploadSucceeds()
        {
            const long safetyBuffer = 1_900_000; // leaves ~1.81 MiB, comfortably under the 2 MB poll floor
            Assert.That(RemainingLedgerBytes, Is.GreaterThan(safetyBuffer + 5L * 1024 * 1024),
                "Ledger must show enough headroom to run the exhaust step and still take the chunked path");

            long uploadSize = RemainingLedgerBytes - safetyBuffer;
            TestContext.WriteLine($"[LEDGER] Remaining before exhaust: {RemainingLedgerBytes} bytes. "
                + $"Exhaust upload size: {uploadSize} bytes.");

            var filePath = E2ETestFileHelper.CreateTestFile(TempDir, uploadSize, "live-exhaust.fits");

            var handler = new CapturingHandler();
            var authStub = new TestAuthStub { ApiKey = Fixture.ApiKey };
            var queueDir = Path.Combine(TempDir, "queue-scenario6");
            Directory.CreateDirectory(queueDir);
            var queueRepo = new UploadQueueRepository(queueDir);
            var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl, handler, queueRepo);
            try
            {
                var job = CreateTestJob(filePath, uploadSize, "live-exhaust.fits", "scenario-06");
                var progress = new Progress<double>();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));

                var result = await client.UploadFileAsync(job, progress, cts.Token);
                LogChunkedCaptures("Scenario 6: exhaust upload", handler);

                Assert.That(result.Success, Is.True,
                    $"Exhaust chunked upload should succeed -- it was sized to stay within budget. Error: {result.ErrorMessage}");

                RecordSuccessfulUpload(uploadSize);

                using var pollCts = new CancellationTokenSource(TimeSpan.FromSeconds(95));
                var finalQuota = await PollUntilRemainingBelowAsync(
                    client, 2L * 1024 * 1024, TimeSpan.FromSeconds(90), pollCts.Token);

                Assert.That(finalQuota, Is.Not.Null, "Quota fetch must succeed while polling post-exhaust");
                TestContext.WriteLine($"[LIVE QUOTA after exhaust] used={finalQuota.UsedBytes} "
                    + $"remaining={finalQuota.RemainingBytes}");
                Assert.That(finalQuota.RemainingBytes, Is.Not.Null);
                Assert.That(finalQuota.RemainingBytes.Value, Is.LessThan(2L * 1024 * 1024),
                    $"Account should be nearly full after the exhaust upload (polled up to 90s for the "
                    + $"60s cache to catch up). Last observed remaining: {finalQuota.RemainingBytes}");
            }
            finally
            {
                client.Dispose();
            }
        }

        // ================================================================
        // Scenario 7: single-shot 507 (flat body) -- ~4 MB, under the 5 MB single-shot cap
        // ================================================================

        [Test]
        [Order(7)]
        public async Task Scenario07_SingleShotUpload_507_FlatBody()
        {
            const long fileSize = 4L * 1024 * 1024; // under the 5 MB single-shot cap
            var filePath = E2ETestFileHelper.CreateTestFile(TempDir, fileSize, "live-single-507-4mb.fits");

            var handler = new CapturingHandler();
            var authStub = new TestAuthStub { ApiKey = Fixture.ApiKey };
            var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl, handler);
            try
            {
                var job = CreateTestJob(filePath, fileSize, "live-single-507-4mb.fits", "scenario-07");
                var progress = new Progress<double>();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

                var result = await client.UploadFileAsync(job, progress, cts.Token);
                var capture = LastCapture(handler);
                LogCapture("Scenario 7: single-shot 507", capture);

                Assert.That(result.Success, Is.False);
                Assert.That(result.FailureKind, Is.EqualTo(FailureKind.PermanentQuota));
                Assert.That(result.StatusCode, Is.EqualTo(507));
                Assert.That(capture.StatusCode, Is.EqualTo(507));
                Assert.That(result.QuotaInfo, Is.Not.Null);
                Assert.That(result.QuotaInfo.Flavor, Is.EqualTo(QuotaBlockFlavor.StorageLimitExceeded));
            }
            finally
            {
                client.Dispose();
            }
        }

        // ================================================================
        // Scenario 8: chunked 507 again at initiate -- ~10 MB
        // ================================================================

        [Test]
        [Order(8)]
        public async Task Scenario08_ChunkedUpload_507AtInitiate_Again()
        {
            const long declaredSize = 10L * 1024 * 1024; // over the now-tiny remaining budget
            var filePath = CreateSparseFile(TempDir, declaredSize, "live-chunked-507-10mb.fits");

            var handler = new CapturingHandler();
            var authStub = new TestAuthStub { ApiKey = Fixture.ApiKey };
            var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl, handler);
            try
            {
                var job = CreateTestJob(filePath, declaredSize, "live-chunked-507-10mb.fits", "scenario-08");
                var progress = new Progress<double>();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

                var result = await client.UploadFileAsync(job, progress, cts.Token);
                var capture = LastCapture(handler);
                LogCapture("Scenario 8: chunked initiate 507 (again)", capture);

                Assert.That(result.Success, Is.False);
                Assert.That(result.FailureKind, Is.EqualTo(FailureKind.PermanentQuota));
                Assert.That(result.StatusCode, Is.EqualTo(507));
                Assert.That(handler.Captures.Count, Is.EqualTo(1), "No chunk PUTs should ever have been sent");
            }
            finally
            {
                client.Dispose();
            }
        }

        // ================================================================
        // Scenario 9: 401 DISTINCT -- garbage key: validate never 401s, upload does
        // ================================================================

        [Test]
        [Order(9)]
        public async Task Scenario09_InvalidKey_ValidateFalse_UploadPermanentAuth_NestedEnvelope()
        {
            var garbageKey = "garbage-key-" + Guid.NewGuid().ToString("N");
            var authStub = new TestAuthStub { ApiKey = garbageKey };

            // Step A: validate -- must be 200 valid:false, NEVER 401.
            var validateHandler = new CapturingHandler();
            var validateClient = new AstrovaultApiClient(authStub, Fixture.BaseUrl, validateHandler);
            try
            {
                var connected = await validateClient.TestConnectionAsync();
                var validateCapture = LastCapture(validateHandler);
                LogCapture("Scenario 9a: auth/validate with garbage key", validateCapture);

                Assert.That(connected, Is.False);
                Assert.That(validateCapture.StatusCode, Is.EqualTo(200),
                    "validate must return 200 valid:false for an invalid key -- never 401");
            }
            finally
            {
                validateClient.Dispose();
            }

            // Step B: an actual upload attempt with the same garbage key -- THIS is where 401 lives.
            var uploadHandler = new CapturingHandler();
            var uploadClient = new AstrovaultApiClient(authStub, Fixture.BaseUrl, uploadHandler);
            try
            {
                var filePath = E2ETestFileHelper.CreateTestFile(TempDir, 1024, "live-401-test.fits");
                var job = CreateTestJob(filePath, 1024, "live-401-test.fits", "scenario-09");
                var progress = new Progress<double>();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

                var result = await uploadClient.UploadFileAsync(job, progress, cts.Token);
                var uploadCapture = LastCapture(uploadHandler);
                LogCapture("Scenario 9b: upload with garbage key", uploadCapture);

                Assert.That(result.Success, Is.False);
                Assert.That(uploadCapture.StatusCode, Is.EqualTo(401));
                Assert.That(result.StatusCode, Is.EqualTo(401));
                Assert.That(result.FailureKind, Is.EqualTo(FailureKind.PermanentAuth));
                Assert.That(result.IsPermanent, Is.False,
                    "PermanentAuth must NOT be IsPermanent (that flag is skip-file-only) -- 401 stops the whole queue");

                var errorCode = ErrorEnvelopeParser.TryParseErrorCode(uploadCapture.Body);
                Assert.That(errorCode, Is.EqualTo("auth.invalid_key"),
                    "the nested envelope's error.code must be auth.invalid_key, proving the envelope-vs-flat split");
            }
            finally
            {
                uploadClient.Dispose();
            }
        }

        // ================================================================
        // Scenario 10: TRANSIENT REGRESSION -- 400 on a valid route classifies PermanentSkipFile
        // ================================================================

        [Test]
        [Order(10)]
        public async Task Scenario10_MalformedMetadata_400_ClassifiedPermanentSkipFile_NotQueueStop()
        {
            var filePath = E2ETestFileHelper.CreateTestFile(TempDir, 1024, "live-400-test.fits");

            var handler = new CapturingHandler();
            var authStub = new TestAuthStub { ApiKey = Fixture.ApiKey };
            var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl, handler);
            try
            {
                var job = CreateTestJob(filePath, 1024, "live-400-test.fits", "scenario-10");

                // Deliberately malformed payload, constructible via the REAL client (no raw HttpClient
                // needed): a non-JSON metadata string. Live recon against the real relay (curl,
                // 2026-08-04) confirmed this is the payload shape that genuinely triggers a 400 on
                // this backend -- omitting a required multipart field entirely (metadata or
                // remotePath) instead triggers an arguably-buggy 500 server.internal_error, which
                // cannot exercise this scenario's contract.
                job.MetadataJson = "not-valid-json{{{";

                var progress = new Progress<double>();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

                var result = await client.UploadFileAsync(job, progress, cts.Token);
                var capture = LastCapture(handler);
                LogCapture("Scenario 10: malformed metadata (400)", capture);

                Assert.That(capture.StatusCode, Is.EqualTo(400));
                Assert.That(result.Success, Is.False);
                Assert.That(result.StatusCode, Is.EqualTo(400));
                Assert.That(result.FailureKind, Is.EqualTo(FailureKind.PermanentSkipFile),
                    "a 400 must classify as skip-this-file, NOT PermanentAuth (queue-stop) and NOT PermanentQuota");
                Assert.That(result.FailureKind, Is.Not.EqualTo(FailureKind.PermanentAuth));
                Assert.That(result.FailureKind, Is.Not.EqualTo(FailureKind.PermanentQuota));
                Assert.That(result.IsPermanent, Is.True,
                    "PermanentSkipFile is the ONE kind where IsPermanent is true -- skip this file, queue continues");

                var errorCode = ErrorEnvelopeParser.TryParseErrorCode(capture.Body);
                Assert.That(errorCode, Is.EqualTo("validation.invalid_format"),
                    "pinning the real backend's envelope error.code for this malformed-metadata 400 (live recon confirmed)");
            }
            finally
            {
                client.Dispose();
            }
        }

        // ================================================================
        // Helpers
        // ================================================================

        private UploadJob CreateTestJob(string filePath, long fileSize, string fileName, string scenarioTag)
        {
            return new UploadJob
            {
                Id = Guid.NewGuid(),
                LocalPath = filePath,
                RelativePath = $"live-quota-test/{scenarioTag}/{Guid.NewGuid():N}/{fileName}",
                FileSize = fileSize,
                CapturedAt = DateTime.UtcNow,
                QueuedAt = DateTime.UtcNow,
                Status = UploadStatus.Pending,
                RetryCount = 0,
                Filter = "L",
                Duration = 60.0,
                FileType = "FITS",
                MetadataJson = "{}"
            };
        }

        /// <summary>
        /// Creates a file whose reported Length is <paramref name="sizeBytes"/> without writing that
        /// many real bytes to disk (NTFS defers/avoids physically zero-filling an extended region that
        /// is never read). Used for scenarios that only need the client's declared-size field (the
        /// chunked initiate call) to be large -- the real chunk content is never read from disk when
        /// the server rejects the upload at initiate, before any chunk PUT is ever issued.
        /// </summary>
        private static string CreateSparseFile(string directory, long sizeBytes, string fileName)
        {
            var filePath = Path.Combine(directory, fileName);
            using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            {
                fs.SetLength(sizeBytes);
            }
            return filePath;
        }

        private static void RecordSuccessfulUpload(long bytes)
        {
            RemainingLedgerBytes -= bytes;
            TotalSuccessfulUploadBytes += bytes;
        }

        private static CapturingHandler.Capture LastCapture(CapturingHandler handler)
        {
            var captures = handler.Captures;
            Assert.That(captures.Count, Is.GreaterThan(0), "Expected at least one captured HTTP request");
            return captures[captures.Count - 1];
        }

        private static void LogCapture(string label, CapturingHandler.Capture capture)
        {
            TestContext.WriteLine($"[{label}] {capture.Method} {capture.Url} -> HTTP {capture.StatusCode}");
            TestContext.WriteLine($"[{label}] body: {capture.Body}");
        }

        /// <summary>
        /// Logs every captured request for a chunked upload, keeping the report readable: full bodies
        /// only for the initiate/complete boundary calls and any non-2xx response; per-chunk PUT
        /// successes get a one-line summary.
        /// </summary>
        private static void LogChunkedCaptures(string label, CapturingHandler handler)
        {
            var captures = handler.Captures;
            TestContext.WriteLine($"[{label}] {captures.Count} HTTP request(s) captured");
            foreach (var c in captures)
            {
                bool isBoundary = c.Url != null && (c.Url.Contains("/initiate") || c.Url.Contains("/complete"));
                if (isBoundary || c.StatusCode >= 300)
                {
                    TestContext.WriteLine($"[{label}] {c.Method} {c.Url} -> HTTP {c.StatusCode} body: {c.Body}");
                }
                else
                {
                    TestContext.WriteLine($"[{label}] {c.Method} {c.Url} -> HTTP {c.StatusCode}");
                }
            }
        }

        /// <summary>
        /// Polls GetQuotaAsync (via the REAL client) until remainingBytes drops below
        /// <paramref name="thresholdBytes"/> or <paramref name="maxWait"/> elapses -- accounts for the
        /// backend's 60s server-side quota cache after Scenario 6's exhaust upload. Returns the last
        /// observed quota either way; the caller asserts on it (a timeout without convergence is a
        /// genuine finding, not silently swallowed).
        /// </summary>
        private async Task<QuotaUsage> PollUntilRemainingBelowAsync(
            AstrovaultApiClient client, long thresholdBytes, TimeSpan maxWait, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            QuotaUsage last = null;
            while (true)
            {
                last = await client.GetQuotaAsync(ct).ConfigureAwait(false);
                TestContext.WriteLine(
                    $"[POLL {sw.Elapsed.TotalSeconds:F1}s] remaining={last?.RemainingBytes} used={last?.UsedBytes}");

                if (last?.RemainingBytes.HasValue == true && last.RemainingBytes.Value < thresholdBytes)
                {
                    return last;
                }

                if (sw.Elapsed >= maxWait)
                {
                    return last;
                }

                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
        }

        private class ValidateBody
        {
            [JsonProperty("valid")]
            public bool Valid { get; set; }

            [JsonProperty("accountId")]
            public string AccountId { get; set; }

            [JsonProperty("accountName")]
            public string AccountName { get; set; }
        }
    }

    /// <summary>
    /// Delegating handler that transparently captures the real wire status code + raw response body of
    /// every request an <see cref="AstrovaultApiClient"/> sends, then re-injects an equivalent buffered
    /// content so the client's own read of the response is completely unaffected. This lets every
    /// scenario in <see cref="NinaQuotaLiveTests"/> exercise the REAL production client end-to-end
    /// while still recovering the exact wire evidence the live-verification report requires, from a
    /// single real network call -- never a duplicated parallel request. Deliberately never captures
    /// request headers (the X-API-Key header lives there) -- only method, URL, response status, and
    /// response body are recorded, so the API key can never leak into a captured value or test log.
    /// </summary>
    internal sealed class CapturingHandler : DelegatingHandler
    {
        public sealed class Capture
        {
            public string Method { get; init; }
            public string Url { get; init; }
            public int StatusCode { get; init; }
            public string Body { get; init; }
        }

        private readonly object captureLock = new object();
        private readonly List<Capture> captures = new List<Capture>();

        public IReadOnlyList<Capture> Captures
        {
            get { lock (captureLock) { return captures.ToList(); } }
        }

        public CapturingHandler() : base(new SocketsHttpHandler())
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            var bytes = response.Content == null
                ? Array.Empty<byte>()
                : await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var bodyText = Encoding.UTF8.GetString(bytes);

            lock (captureLock)
            {
                captures.Add(new Capture
                {
                    Method = request.Method.Method,
                    Url = request.RequestUri?.PathAndQuery,
                    StatusCode = (int)response.StatusCode,
                    Body = bodyText
                });
            }

            // Re-wrap so the caller (AstrovaultApiClient) can still read the content exactly as if
            // this handler were never in the chain.
            var newContent = new ByteArrayContent(bytes);
            if (response.Content != null)
            {
                foreach (var header in response.Content.Headers)
                {
                    newContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
            response.Content = newContent;
            return response;
        }
    }
}
