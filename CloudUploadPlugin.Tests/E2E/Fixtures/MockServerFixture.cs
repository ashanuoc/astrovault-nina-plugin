using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace CloudUploadPlugin.Tests.E2E
{
    /// <summary>
    /// NUnit SetUpFixture that starts the Python mock API server once for all
    /// E2E tests in the CloudUploadPlugin.Tests.E2E namespace.
    /// Provides server lifecycle management, health check polling,
    /// and admin endpoint helpers for test isolation.
    /// </summary>
    [SetUpFixture]
    public class MockServerFixture
    {
        /// <summary>
        /// Singleton instance set during [OneTimeSetUp].
        /// Accessed by E2ETestBase and individual test classes.
        /// </summary>
        public static MockServerFixture Instance { get; private set; }

        protected Process _serverProcess;
        protected readonly StringBuilder _stdoutCapture = new StringBuilder();
        protected readonly StringBuilder _stderrCapture = new StringBuilder();
        protected readonly object _logLock = new object();
        protected readonly HttpClient _adminClient = new HttpClient();

        /// <summary>
        /// Base URL of the running mock server (e.g., http://127.0.0.1:12345).
        /// </summary>
        public string BaseUrl { get; protected set; }

        /// <summary>
        /// Whether the mock server process is still running.
        /// </summary>
        public bool IsServerRunning => _serverProcess != null && !_serverProcess.HasExited;

        [OneTimeSetUp]
        public virtual async Task GlobalSetUp()
        {
            Instance = this;

            // Pick a free port dynamically
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            // Resolve paths relative to the test assembly location
            var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var repoRoot = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", ".."));
            var pythonPath = Path.Combine(repoRoot, "mock-api-server", "venv", "Scripts", "python.exe");
            var serverDir = Path.Combine(repoRoot, "mock-api-server");

            // Gracefully skip if Python venv is not available
            if (!File.Exists(pythonPath))
            {
                Assert.Ignore($"E2E tests skipped: Python venv not found at {pythonPath}. Install Python and run 'python -m venv venv' in mock-api-server/.");
                return;
            }

            // Configure and start the mock server process
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
            psi.Environment["STORAGE_BACKEND"] = "local";

            _serverProcess = Process.Start(psi);

            // Wire up async output capture
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

            // Set base URL and wait for server readiness
            BaseUrl = $"http://127.0.0.1:{port}";
            bool ready = await WaitForServerReady(BaseUrl, timeoutMs: 10000);

            if (!ready)
            {
                string logs = GetCapturedLogs();
                if (_serverProcess.HasExited)
                {
                    Assert.Ignore($"E2E tests skipped: Mock server process exited with code {_serverProcess.ExitCode}. Logs:\n{logs}");
                }
                else
                {
                    _serverProcess.Kill();
                    _serverProcess.WaitForExit(5000);
                    Assert.Ignore($"E2E tests skipped: Mock server did not respond to health check within 10s. Logs:\n{logs}");
                }
            }
        }

        [OneTimeTearDown]
        public virtual void GlobalTearDown()
        {
            if (_serverProcess != null && !_serverProcess.HasExited)
            {
                try
                {
                    _serverProcess.Kill();
                    if (!_serverProcess.WaitForExit(5000))
                        TestContext.Progress.WriteLine("WARNING: Mock server process did not exit within 5s after Kill()");
                }
                catch (InvalidOperationException) { /* process already exited */ }
            }
            _serverProcess?.Dispose();
            _adminClient?.Dispose();
        }

        /// <summary>
        /// Resets mock server state: clears error modes and deletes all stored files.
        /// Call before each test for isolation.
        /// </summary>
        public async Task ResetServerState()
        {
            if (!IsServerRunning)
                Assert.Fail($"Mock server is not running (PID exited). Stderr:\n{GetCapturedLogs()}");

            // Reset all error modes
            var resetRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/admin/reset-modes");
            resetRequest.Headers.Add("X-API-Key", "test-key-1");
            var resetResponse = await _adminClient.SendAsync(resetRequest);
            Assert.That(resetResponse.IsSuccessStatusCode, Is.True,
                $"Failed to reset server modes: {resetResponse.StatusCode}");

            // Clear all stored files
            var clearRequest = new HttpRequestMessage(HttpMethod.Delete, $"{BaseUrl}/admin/clear-bucket");
            clearRequest.Headers.Add("X-API-Key", "test-key-1");
            var clearResponse = await _adminClient.SendAsync(clearRequest);
            Assert.That(clearResponse.IsSuccessStatusCode, Is.True,
                $"Failed to clear server bucket: {clearResponse.StatusCode}");
        }

        /// <summary>
        /// Sets an error simulation mode on the mock server.
        /// </summary>
        /// <param name="mode">Error mode name (reject_uploads, random_failure, slow_response, auth_rejection).</param>
        /// <param name="value">Mode value (bool for toggle modes, int for numeric modes).</param>
        public async Task SetErrorMode(string mode, object value)
        {
            if (!IsServerRunning)
                Assert.Fail($"Mock server is not running (PID exited). Stderr:\n{GetCapturedLogs()}");

            var body = JsonSerializer.Serialize(new { mode, value });
            var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/admin/set-mode")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-API-Key", "test-key-1");

            var response = await _adminClient.SendAsync(request);
            Assert.That(response.IsSuccessStatusCode, Is.True,
                $"Failed to set error mode '{mode}': {response.StatusCode}");
        }

        /// <summary>
        /// Returns captured stdout and stderr from the mock server process.
        /// </summary>
        public string GetCapturedLogs()
        {
            lock (_logLock)
            {
                return $"=== MOCK SERVER STDOUT ===\n{_stdoutCapture}\n=== MOCK SERVER STDERR ===\n{_stderrCapture}";
            }
        }

        /// <summary>
        /// Clears captured stdout and stderr logs.
        /// </summary>
        public void ClearCapturedLogs()
        {
            lock (_logLock)
            {
                _stdoutCapture.Clear();
                _stderrCapture.Clear();
            }
        }

        /// <summary>
        /// If the current test failed, dumps captured server logs to NUnit test output.
        /// Call from [TearDown] in test classes for CI debugging.
        /// </summary>
        public void DumpLogsOnFailure()
        {
            if (TestContext.CurrentContext.Result.Outcome.Status == TestStatus.Failed)
            {
                TestContext.Out.WriteLine(GetCapturedLogs());
            }
        }

        /// <summary>
        /// Polls the server health endpoint until it responds or timeout elapses.
        /// </summary>
        protected async Task<bool> WaitForServerReady(string baseUrl, int timeoutMs = 10000)
        {
            using var healthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (_serverProcess.HasExited)
                    return false;

                try
                {
                    var response = await healthClient.GetAsync($"{baseUrl}/health");
                    if (response.IsSuccessStatusCode)
                        return true;
                }
                catch (HttpRequestException) { /* server not ready yet */ }
                catch (TaskCanceledException) { /* request timed out */ }

                await Task.Delay(250);
            }

            return false;
        }
    }
}
