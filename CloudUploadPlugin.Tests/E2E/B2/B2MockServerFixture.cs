using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;

namespace CloudUploadPlugin.Tests.E2E.B2
{
    /// <summary>
    /// NUnit SetUpFixture that starts a separate mock API server configured for
    /// Backblaze B2 storage backend. Manages B2 env var checking, credential
    /// gating (Assert.Ignore when missing), and clear-bucket probe validation.
    /// Inherits from MockServerFixture for type compatibility with E2ETestBase
    /// and reuse of admin helpers (ResetServerState, SetErrorMode, etc.).
    /// </summary>
    [SetUpFixture]
    public class B2MockServerFixture : MockServerFixture
    {
        /// <summary>
        /// Singleton instance for B2 tests. Accessed via GetFixture() override in E2EB2UploadTests.
        /// Uses 'new' to shadow the parent's Instance property with the correct B2 type.
        /// </summary>
        public new static B2MockServerFixture Instance { get; private set; }

        [OneTimeSetUp]
        public override async Task GlobalSetUp()
        {
            Instance = this;

            // ================================================================
            // Phase 1: Check B2 env vars BEFORE starting server
            // ================================================================
            var keyId = Environment.GetEnvironmentVariable("B2_KEY_ID");
            var appKey = Environment.GetEnvironmentVariable("B2_APP_KEY");
            var bucketName = Environment.GetEnvironmentVariable("B2_BUCKET_NAME");
            var endpoint = Environment.GetEnvironmentVariable("B2_ENDPOINT");

            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(keyId)) missing.Add("B2_KEY_ID");
            if (string.IsNullOrWhiteSpace(appKey)) missing.Add("B2_APP_KEY");
            if (string.IsNullOrWhiteSpace(bucketName)) missing.Add("B2_BUCKET_NAME");
            if (string.IsNullOrWhiteSpace(endpoint)) missing.Add("B2_ENDPOINT");

            if (missing.Count > 0)
            {
                Assert.Ignore(
                    $"B2 E2E tests skipped: missing environment variables: {string.Join(", ", missing)}. " +
                    "See mock-api-server/.env.b2.example for setup instructions.");
                return;
            }

            // ================================================================
            // Phase 2: Start server with B2 env vars
            // Replicates parent startup logic with B2-specific environment config.
            // Accepted duplication (~35 lines) vs. template method for single subclass.
            // ================================================================
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var repoRoot = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", ".."));
            var pythonPath = Path.Combine(repoRoot, "mock-api-server", "venv", "Scripts", "python.exe");
            var serverDir = Path.Combine(repoRoot, "mock-api-server");

            if (!File.Exists(pythonPath))
            {
                Assert.Ignore($"B2 E2E tests skipped: Python venv not found at {pythonPath}.");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = $"-m uvicorn main:app --host 127.0.0.1 --port {port}",
                WorkingDirectory = serverDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.Environment["STORAGE_BACKEND"] = "b2";
            psi.Environment["B2_KEY_ID"] = keyId;
            psi.Environment["B2_APP_KEY"] = appKey;
            psi.Environment["B2_BUCKET_NAME"] = bucketName;
            psi.Environment["B2_ENDPOINT"] = endpoint;

            _serverProcess = Process.Start(psi);

            _serverProcess.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null)
                    lock (_logLock) _stdoutCapture.AppendLine(e.Data);
            };
            _serverProcess.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null)
                    lock (_logLock) _stderrCapture.AppendLine(e.Data);
            };
            _serverProcess.BeginOutputReadLine();
            _serverProcess.BeginErrorReadLine();

            BaseUrl = $"http://127.0.0.1:{port}";
            bool ready = await WaitForServerReady(BaseUrl, timeoutMs: 10000);

            if (!ready)
            {
                string logs = GetCapturedLogs();
                if (_serverProcess.HasExited)
                {
                    // Credentials were provided but server crashed -- real failure, not a skip
                    // (cross-AI review: Assert.Ignore only for missing creds, not broken infrastructure)
                    Assert.Fail($"B2 mock server exited with code {_serverProcess.ExitCode}. Logs:\n{logs}");
                }
                else
                {
                    _serverProcess.Kill();
                    _serverProcess.WaitForExit(5000);
                    Assert.Fail($"B2 mock server did not respond within 10s. Logs:\n{logs}");
                }
                return;
            }

            // ================================================================
            // Phase 3: Probe B2 credentials via clear-bucket
            // Credentials are present (passed Phase 1), so probe failure = real problem
            // ================================================================
            try
            {
                await ResetServerState();
            }
            catch (Exception ex)
            {
                Assert.Fail(
                    $"B2 credentials appear invalid (clear-bucket failed: {ex.Message}). " +
                    "Verify B2_KEY_ID, B2_APP_KEY, B2_BUCKET_NAME, and B2_ENDPOINT.");
            }
        }

        [OneTimeTearDown]
        public override void GlobalTearDown()
        {
            // Clean up B2 bucket before killing server (prevent storage accumulation)
            try
            {
                ResetServerState().GetAwaiter().GetResult();
            }
            catch
            {
                // Best-effort cleanup -- server may already be dead
            }
            base.GlobalTearDown();
        }
    }
}
