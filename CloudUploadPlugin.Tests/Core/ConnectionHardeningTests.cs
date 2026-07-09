using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Astrovault.Core;
using Astrovault.Interfaces;
using Moq;
using NUnit.Framework;

namespace CloudUploadPlugin.Tests.Core
{
    /// <summary>
    /// REL-08 / D-10 hardening tests for AstrovaultApiClient and AuthManager.
    ///
    /// REL-08: production HttpClients must be built over a SocketsHttpHandler with a finite
    /// PooledConnectionLifetime so stale DNS / dead sockets are refreshed on overnight
    /// sessions. The two HttpMessageHandler-injecting test constructors of AstrovaultApiClient
    /// must be left untouched (they still inject the mock handler verbatim).
    ///
    /// D-10: MaxSessionRestarts is raised generously so multi-night outages don't exhaust the
    /// session-restart budget.
    /// </summary>
    [TestFixture]
    [Category("Resilience")]
    public class ConnectionHardeningTests
    {
        /// <summary>
        /// Reflects the private HttpMessageHandler that a given HttpClient was constructed over.
        /// Walks the internal _handler chain to the innermost primary handler.
        /// </summary>
        private static HttpMessageHandler GetPrimaryHandler(HttpClient client)
        {
            // HttpClient -> HttpMessageInvoker._handler (the handler passed to the ctor).
            var handlerField = typeof(HttpMessageInvoker).GetField("_handler",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(handlerField, Is.Not.Null, "HttpMessageInvoker._handler field not found");
            var handler = (HttpMessageHandler)handlerField.GetValue(client);

            // Unwrap any DelegatingHandler layers to reach the primary handler.
            while (handler is DelegatingHandler dh && dh.InnerHandler != null)
            {
                handler = dh.InnerHandler;
            }
            return handler;
        }

        private static HttpClient GetHttpClient(object instance, string fieldName = "httpClient")
        {
            var field = instance.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Field '{fieldName}' not found on {instance.GetType().Name}");
            return (HttpClient)field.GetValue(instance);
        }

        [Test]
        public void ApiClient_ProductionCtor_BaseUrl_UsesSocketsHttpHandlerWithPooledLifetime()
        {
            var auth = new Mock<IAuthManager>().Object;
            using var sut = new AstrovaultApiClient(auth, "https://api.test.io");

            var handler = GetPrimaryHandler(GetHttpClient(sut));

            Assert.That(handler, Is.InstanceOf<SocketsHttpHandler>(),
                "production (authManager, baseUrl) ctor must use SocketsHttpHandler");
            var sockets = (SocketsHttpHandler)handler;
            Assert.That(sockets.PooledConnectionLifetime, Is.LessThan(TimeSpan.MaxValue),
                "PooledConnectionLifetime must be finite to refresh stale connections");
            Assert.That(sockets.PooledConnectionLifetime, Is.GreaterThan(TimeSpan.Zero));
        }

        [Test]
        public void ApiClient_ProductionCtor_WithQueueRepo_UsesSocketsHttpHandlerWithPooledLifetime()
        {
            var auth = new Mock<IAuthManager>().Object;
            var repo = new Mock<IUploadQueueRepository>().Object;
            using var sut = new AstrovaultApiClient(auth, "https://api.test.io", repo);

            var handler = GetPrimaryHandler(GetHttpClient(sut));

            Assert.That(handler, Is.InstanceOf<SocketsHttpHandler>(),
                "production (authManager, baseUrl, queueRepository) ctor must use SocketsHttpHandler");
            var sockets = (SocketsHttpHandler)handler;
            Assert.That(sockets.PooledConnectionLifetime, Is.LessThan(TimeSpan.MaxValue));
            Assert.That(sockets.PooledConnectionLifetime, Is.GreaterThan(TimeSpan.Zero));
        }

        [Test]
        public void ApiClient_TestCtor_WithHandler_LeavesInjectedHandlerIntact()
        {
            var auth = new Mock<IAuthManager>().Object;
            using var injected = new MockHttpMessageHandler();
            using var sut = new AstrovaultApiClient(auth, "https://api.test.io", injected);

            var handler = GetPrimaryHandler(GetHttpClient(sut));

            Assert.That(handler, Is.SameAs(injected),
                "the HttpMessageHandler-injecting test ctor must keep injecting the mock handler unchanged");
        }

        [Test]
        public void ApiClient_TestCtor_WithHandlerAndRepo_LeavesInjectedHandlerIntact()
        {
            var auth = new Mock<IAuthManager>().Object;
            var repo = new Mock<IUploadQueueRepository>().Object;
            using var injected = new MockHttpMessageHandler();
            using var sut = new AstrovaultApiClient(auth, "https://api.test.io", injected, repo);

            var handler = GetPrimaryHandler(GetHttpClient(sut));

            Assert.That(handler, Is.SameAs(injected),
                "the (handler, queueRepository) test ctor must keep injecting the mock handler unchanged");
        }

        [Test]
        public void AuthManager_Ctor_UsesSocketsHttpHandlerWithPooledLifetime_PreservingTimeoutAndBaseAddress()
        {
            var dataFolder = Path.Combine(Path.GetTempPath(), "av-authtest-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var auth = new AuthManager("https://api.test.io", dataFolder);
                var client = GetHttpClient(auth);

                var handler = GetPrimaryHandler(client);
                Assert.That(handler, Is.InstanceOf<SocketsHttpHandler>(),
                    "AuthManager's single ctor must use SocketsHttpHandler");
                var sockets = (SocketsHttpHandler)handler;
                Assert.That(sockets.PooledConnectionLifetime, Is.LessThan(TimeSpan.MaxValue));
                Assert.That(sockets.PooledConnectionLifetime, Is.GreaterThan(TimeSpan.Zero));

                // Preserve BaseAddress and the 10s validation timeout.
                Assert.That(client.BaseAddress, Is.EqualTo(new Uri("https://api.test.io/")));
                Assert.That(client.Timeout, Is.EqualTo(TimeSpan.FromSeconds(10)));
            }
            finally
            {
                try { Directory.Delete(dataFolder, true); } catch { /* best effort */ }
            }
        }
    }
}
