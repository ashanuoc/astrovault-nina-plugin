using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Astrovault;
using Astrovault.Interfaces;
using Astrovault.Models;
using Moq;
using NINA.Profile;
using NINA.Profile.Interfaces;

namespace Astrovault.Tests.Core
{
    /// <summary>
    /// CONSENT-1 (SC6 / P19-05): proves the disconnect re-consent overload drives auto-upload.
    /// <c>DisconnectAsync(true)</c> must turn off <c>UploadEnabled</c> (the user opted to also
    /// stop auto-upload), while <c>DisconnectAsync(false)</c> must leave it untouched (disconnect
    /// only). This is the load-bearing behavior behind the Options.xaml.cs Yes/No prompt; the prompt
    /// itself renders a real WPF modal and is covered by the Task 3 human-verify checkpoint, not here.
    ///
    /// Arrangement note: <see cref="AstrovaultPlugin"/> exposes only a MEF
    /// <c>[ImportingConstructor]</c> that eagerly runs full service initialization (file I/O,
    /// HttpClient construction, StartServices) against NINA host types -- not unit-constructable.
    /// Following the plan's guidance ("assert on the UploadEnabled side-effect only"), the SUT is
    /// allocated without a constructor and the two private fields <c>DisconnectAsync(bool)</c>
    /// actually touches are injected: <c>pluginSettings</c> (a real <see cref="PluginOptionsAccessor"/>
    /// backed by an in-memory <see cref="PluginSettings"/> + mocked <see cref="IProfileService"/>,
    /// mirroring NINA's own PluginOptionsAccessorTest so UploadEnabled get/set round-trips) and
    /// <c>authManager</c> (a Moq <see cref="IAuthManager"/> whose <c>ClearApiKey()</c> is a no-op).
    /// <c>uploadManager</c> stays null -- DisconnectAsync guards it with
    /// <c>if (uploadManager != null and uploadManager.IsRunning)</c> -- and <c>RefreshAuthUI()</c> only
    /// raises PropertyChanged (null-safe with no subscribers) over primitive backing fields.
    ///
    /// KEYCLR-1 (SC6, failed-connect key clear): <c>TestConnectionAsync</c>'s invalid-key and
    /// exception branches touch only the injected <c>authManager</c> field, the observable
    /// <c>ApiKeyInput</c>/<c>IsApiKeyRevealed</c> properties, and <c>RefreshAuthUI()</c> (PropertyChanged
    /// is null-safe with no subscribers) -- no HTTP and no reinit on these paths (that only happens on
    /// the success branch via StoreApiKey), so the same uninitialized-object seam used above applies
    /// directly. See <see cref="TestConnectionAsync_InvalidKeyResult_ClearsApiKeyInputAndHidesReveal"/>
    /// and <see cref="TestConnectionAsync_ValidationThrows_ClearsApiKeyInputAndHidesReveal"/> below for
    /// the two automated failure-path assertions; the PasswordBox (WPF control) sync itself remains
    /// covered by the Task 3 manual checkpoint.
    /// </summary>
    [TestFixture]
    [Category("Security")]
    public class DisconnectConsentTests
    {
        /// <summary>
        /// Allocates an AstrovaultPlugin without running its constructor (no service init / file I/O)
        /// and injects only the fields DisconnectAsync(bool) reads: an in-memory-backed
        /// PluginOptionsAccessor for UploadEnabled, and a mocked IAuthManager for ClearApiKey().
        /// </summary>
        private static AstrovaultPlugin CreatePluginWithInMemorySettings(out IAuthManager authManager)
        {
            var plugin = (AstrovaultPlugin)RuntimeHelpers.GetUninitializedObject(typeof(AstrovaultPlugin));

            // Real accessor over an in-memory store (mirrors NINA's PluginOptionsAccessorTest), so
            // UploadEnabled's GetValueBoolean/SetValueBoolean round-trips without a live profile.
            var pluginGuid = Guid.NewGuid();
            var pluginSettings = new PluginSettings();
            var mockProfileService = new Mock<IProfileService>();
            mockProfileService.SetupGet(ps => ps.ActiveProfile.PluginSettings).Returns(pluginSettings);
            IPluginOptionsAccessor accessor = new PluginOptionsAccessor(mockProfileService.Object, pluginGuid);

            authManager = new Mock<IAuthManager>().Object;

            SetPrivateField(plugin, "pluginSettings", accessor);
            SetPrivateField(plugin, "authManager", authManager);
            // uploadManager deliberately left null -- DisconnectAsync null-guards it.

            return plugin;
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null,
                $"Field '{fieldName}' not found on {target.GetType().Name}");
            field.SetValue(target, value);
        }

        [Test]
        public async Task DisconnectAsync_AlsoDisableUploadTrue_TurnsOffUploadEnabled()
        {
            // Arrange: auto-upload is currently ON.
            var plugin = CreatePluginWithInMemorySettings(out _);
            plugin.UploadEnabled = true;
            Assert.That(plugin.UploadEnabled, Is.True, "precondition: auto-upload starts enabled");

            // Act: user disconnects AND chooses to also turn off auto-upload (default Yes).
            await plugin.DisconnectAsync(true);

            // Assert: auto-upload is now OFF (P19-05 re-consent honored).
            Assert.That(plugin.UploadEnabled, Is.False,
                "DisconnectAsync(true) must disable UploadEnabled");
        }

        [Test]
        public async Task DisconnectAsync_AlsoDisableUploadFalse_LeavesUploadEnabledUnchanged()
        {
            // Arrange: auto-upload is currently ON.
            var plugin = CreatePluginWithInMemorySettings(out _);
            plugin.UploadEnabled = true;
            Assert.That(plugin.UploadEnabled, Is.True, "precondition: auto-upload starts enabled");

            // Act: user disconnects but chooses No -- disconnect only, keep auto-upload enabled.
            await plugin.DisconnectAsync(false);

            // Assert: auto-upload is left untouched (disconnect-only path).
            Assert.That(plugin.UploadEnabled, Is.True,
                "DisconnectAsync(false) must leave UploadEnabled unchanged");
        }

        [Test]
        public async Task DisconnectAsync_ClearsApiKey_OnBothConsentChoices()
        {
            // Both consent paths must still clear the stored credential (disconnect always
            // revokes the key; only the auto-upload toggle differs). Proves the overload does not
            // skip key clearing when the user declines to disable auto-upload.
            var pluginDisable = CreatePluginWithInMemorySettings(out var authDisable);
            await pluginDisable.DisconnectAsync(true);
            Mock.Get(authDisable).Verify(a => a.ClearApiKey(), Times.Once,
                "DisconnectAsync(true) must clear the stored API key");

            var pluginKeep = CreatePluginWithInMemorySettings(out var authKeep);
            await pluginKeep.DisconnectAsync(false);
            Mock.Get(authKeep).Verify(a => a.ClearApiKey(), Times.Once,
                "DisconnectAsync(false) must still clear the stored API key");
        }

        [Test]
        public async Task TestConnectionAsync_InvalidKeyResult_ClearsApiKeyInputAndHidesReveal()
        {
            // Arrange: user typed a key and revealed it; the server rejects it (Valid == false).
            var plugin = CreatePluginWithInMemorySettings(out var authManager);
            Mock.Get(authManager)
                .Setup(a => a.ValidateApiKeyAsync(It.IsAny<string>()))
                .ReturnsAsync(new ApiKeyValidationResult { Valid = false, ErrorMessage = "invalid" });
            plugin.ApiKeyInput = "astrovault_live_shouldbescrubbed";
            plugin.IsApiKeyRevealed = true;

            // Act: a failed connect attempt (KEYCLR-1 / SC6 -- invalid-key branch).
            await plugin.TestConnectionAsync();

            // Assert: the typed key is scrubbed from the view-model and the reveal toggle re-hides.
            Assert.That(plugin.ApiKeyInput, Is.Empty,
                "KEYCLR-1: ApiKeyInput must be scrubbed after a failed (invalid-key) connect");
            Assert.That(plugin.IsApiKeyRevealed, Is.False,
                "KEYCLR-1: IsApiKeyRevealed must be reset to false after a failed (invalid-key) connect");
        }

        [Test]
        public async Task TestConnectionAsync_ValidationThrows_ClearsApiKeyInputAndHidesReveal()
        {
            // Arrange: user typed a key and revealed it; validation itself blows up (e.g. network error).
            var plugin = CreatePluginWithInMemorySettings(out var authManager);
            Mock.Get(authManager)
                .Setup(a => a.ValidateApiKeyAsync(It.IsAny<string>()))
                .ThrowsAsync(new Exception("boom"));
            plugin.ApiKeyInput = "astrovault_live_shouldbescrubbed";
            plugin.IsApiKeyRevealed = true;

            // Act: connect attempt throws inside the try (KEYCLR-1 / SC6 -- exception/catch branch).
            await plugin.TestConnectionAsync();

            // Assert: the typed key is scrubbed from the view-model and the reveal toggle re-hides.
            Assert.That(plugin.ApiKeyInput, Is.Empty,
                "KEYCLR-1: ApiKeyInput must be scrubbed after a connect attempt that throws");
            Assert.That(plugin.IsApiKeyRevealed, Is.False,
                "KEYCLR-1: IsApiKeyRevealed must be reset to false after a connect attempt that throws");
        }
    }
}
