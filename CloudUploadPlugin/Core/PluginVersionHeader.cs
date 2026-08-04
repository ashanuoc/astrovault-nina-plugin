using System.Reflection;

namespace Astrovault.Core
{
    /// <summary>
    /// Single source for the <c>X-Plugin-Version</c> header sent on every upload-lifecycle HTTP
    /// request (storage-quota plan §4 Step 1). The value is read once, via reflection, from this
    /// assembly's AssemblyVersion (<c>Properties/AssemblyInfo.cs</c> -- not modified by this
    /// change). Gives the backend telemetry to distinguish plugin versions in the field from this
    /// release onward; it cannot help with versions already deployed before this change shipped.
    /// </summary>
    internal static class PluginVersionHeader
    {
        public const string HeaderName = "X-Plugin-Version";

        public static readonly string Value =
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
    }
}
