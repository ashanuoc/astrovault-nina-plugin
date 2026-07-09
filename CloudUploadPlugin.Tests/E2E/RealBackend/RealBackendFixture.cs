using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NUnit.Framework;

namespace CloudUploadPlugin.Tests.E2E.RealBackend
{
    /// <summary>
    /// NUnit SetUpFixture for real backend E2E tests.
    /// Checks for ASTROVAULT_API_KEY env var and probes backend connectivity.
    ///
    /// Skip logic (per Phase 13 pattern):
    /// - ASTROVAULT_API_KEY not set -> Assert.Ignore (opt-in, safe for CI)
    /// - API key set but backend unreachable -> Assert.Fail (surfaces broken infra)
    /// - API key set but invalid response -> Assert.Fail (surfaces contract issues)
    /// </summary>
    [SetUpFixture]
    public class RealBackendFixture
    {
        public static RealBackendFixture Instance { get; private set; }
        public string BaseUrl { get; private set; }
        public string ApiKey { get; private set; }

        [OneTimeSetUp]
        public async Task GlobalSetUp()
        {
            Instance = this;

            // Read from environment variables
            BaseUrl = Environment.GetEnvironmentVariable("ASTROVAULT_BACKEND_URL")
                      ?? "https://vault.astrospherehub.com/";
            ApiKey = Environment.GetEnvironmentVariable("ASTROVAULT_API_KEY");

            if (string.IsNullOrWhiteSpace(ApiKey))
            {
                Assert.Ignore("Real backend tests skipped: ASTROVAULT_API_KEY environment variable not set.");
                return;
            }

            // Ensure BaseUrl has trailing slash for consistent URI joining
            if (!BaseUrl.EndsWith("/"))
                BaseUrl += "/";

            // Probe connectivity with 10-second timeout.
            // If API key is set but backend is broken, FAIL -- do not silently skip.
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{BaseUrl.TrimEnd('/')}/v1/storage/nina/auth/validate");
                request.Headers.Add("X-API-Key", ApiKey);

                var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    Assert.Fail(
                        $"Real backend infrastructure failure: backend returned HTTP {(int)response.StatusCode}. " +
                        $"URL: {BaseUrl} -- If the backend is down, fix it. Do not let tests silently skip.");
                }

                // Verify response body is valid JSON with expected shape
                var body = await response.Content.ReadAsStringAsync();
                var result = JsonConvert.DeserializeObject<ValidateProbeResponse>(body);
                if (result == null)
                {
                    Assert.Fail(
                        $"Real backend contract failure: auth/validate returned unparseable JSON. Body: {body}");
                }

                // If the provided key is invalid, warn but don't fail fixture --
                // the AuthValidation_InvalidKey test needs to work too
                if (!result.Valid)
                {
                    TestContext.WriteLine(
                        $"WARNING: ASTROVAULT_API_KEY appears invalid (valid=false). " +
                        $"Upload tests will likely fail. Check your API key.");
                }
            }
            catch (TaskCanceledException)
            {
                Assert.Fail(
                    $"Real backend infrastructure failure: connection timed out after 10s. " +
                    $"URL: {BaseUrl} -- Backend may be down.");
            }
            catch (HttpRequestException ex)
            {
                Assert.Fail(
                    $"Real backend infrastructure failure: {ex.GetType().Name} - {ex.Message}. " +
                    $"URL: {BaseUrl} -- Backend may be unreachable.");
            }
        }

        [OneTimeTearDown]
        public void GlobalTearDown()
        {
            Instance = null;
        }

        private class ValidateProbeResponse
        {
            [JsonProperty("valid")]
            public bool Valid { get; set; }

            [JsonProperty("accountName")]
            public string AccountName { get; set; }
        }
    }
}
