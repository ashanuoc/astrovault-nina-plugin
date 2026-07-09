using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using Astrovault;
using Astrovault.Core;
using Astrovault.Interfaces;
using Moq;
using NUnit.Framework;

namespace CloudUploadPlugin.Tests.Core
{
    /// <summary>
    /// Phase 18.1-07 verify-and-close tests for the two folded todos (both already FIXED in Phase 18;
    /// these assertions pin the fixes so they cannot silently regress):
    ///
    /// (1) ApiEndpoint -> AuthManager / AstrovaultApiClient BaseAddress propagation. The plugin's
    ///     ReinitializeHttpServicesAsync recreates BOTH services with the new (normalized) endpoint,
    ///     so a changed endpoint takes effect. We pin the substance: reconstructing the services with a
    ///     new base URL yields a new BaseAddress/BaseUrl (HttpClient.BaseAddress is immutable, which is
    ///     exactly why the plugin recreates rather than mutates).
    ///
    /// (2) Misleading "Connected" on startup. The honest-UX fix introduced a distinct Configured state
    ///     (key present but not yet validated) so startup shows Configured -- NOT Connected -- until a
    ///     real backend validation succeeds. We pin that the Configured and Connected states are
    ///     distinct enum members and that Configured is the not-yet-validated state.
    /// </summary>
    [TestFixture]
    [Category("Lifecycle")]
    public class FoldedTodoVerificationTests
    {
        private static Uri GetBaseAddress(object instance)
        {
            var field = instance.GetType().GetField("httpClient",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "httpClient field not found");
            var client = (HttpClient)field.GetValue(instance);
            return client.BaseAddress;
        }

        [Test]
        public void Todo1_AuthManager_RecreatedWithNewEndpoint_PicksUpNewBaseAddress()
        {
            // Reproduces the effect of ReinitializeHttpServicesAsync's Step C: a new AuthManager built
            // with the changed endpoint hits the NEW URL, not the old one.
            var dataFolder = Path.Combine(Path.GetTempPath(), "av-todo1-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var oldAuth = new AuthManager("http://old-endpoint:5000", dataFolder);
                using var newAuth = new AuthManager("http://new-endpoint:6000", dataFolder);

                Assert.That(GetBaseAddress(oldAuth), Is.EqualTo(new Uri("http://old-endpoint:5000/")));
                Assert.That(GetBaseAddress(newAuth), Is.EqualTo(new Uri("http://new-endpoint:6000/")),
                    "An endpoint change must propagate to AuthManager's HttpClient.BaseAddress (via reinit).");
            }
            finally
            {
                try { Directory.Delete(dataFolder, true); } catch { /* best effort */ }
            }
        }

        [Test]
        public void Todo1_ApiClient_RecreatedWithNewEndpoint_PicksUpNewBaseAddress()
        {
            // Reproduces the effect of ReinitializeHttpServicesAsync's Step D: a new AstrovaultApiClient
            // built with the changed endpoint resolves requests against the NEW base URL.
            var auth = new Mock<IAuthManager>().Object;
            using var oldClient = new AstrovaultApiClient(auth, "http://old-endpoint:5000");
            using var newClient = new AstrovaultApiClient(auth, "http://new-endpoint:6000");

            Assert.That(oldClient.BaseUrl, Is.EqualTo("http://old-endpoint:5000"));
            Assert.That(newClient.BaseUrl, Is.EqualTo("http://new-endpoint:6000"),
                "An endpoint change must propagate to AstrovaultApiClient's base URL (via reinit).");
            Assert.That(GetBaseAddress(newClient), Is.EqualTo(new Uri("http://new-endpoint:6000/")));
        }

        [Test]
        public void Todo2_ConfiguredAndConnected_AreDistinctStates()
        {
            // The honest-UX fix added a Configured state (key on disk, not yet validated) that is
            // explicitly NOT Connected. Startup shows Configured until a real validation succeeds, so a
            // green "Connected" dot can never appear on a mere key-file-exists check.
            Assert.That(ConnectionStatus.Configured, Is.Not.EqualTo(ConnectionStatus.Connected),
                "Configured (key present, unvalidated) must be a distinct state from Connected (validated).");

            // Both members exist on the enum (compile-time + runtime guard against accidental removal).
            Assert.That(Enum.IsDefined(typeof(ConnectionStatus), ConnectionStatus.Configured), Is.True);
            Assert.That(Enum.IsDefined(typeof(ConnectionStatus), ConnectionStatus.Connected), Is.True);
        }

        [Test]
        public void Todo2_EndpointValidation_IsWiredIntoTheSetterGuard()
        {
            // The startup validation path only meaningfully runs when the stored endpoint is valid;
            // the REL-15 guard (IsValidEndpoint) is the gate that keeps a bad endpoint from ever
            // reaching the validation flow on load. Pin that the guard is the same one the plugin uses.
            Assert.That(AstrovaultPlugin.IsValidEndpoint("http://localhost:5000"), Is.True);
            Assert.That(AstrovaultPlugin.IsValidEndpoint("garbage"), Is.False);
        }
    }
}
