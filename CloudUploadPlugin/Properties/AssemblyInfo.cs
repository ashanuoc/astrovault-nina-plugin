using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// ============================================================================
// MANDATORY: Unique plugin identifier
// WARNING: Never change this GUID after publishing - it identifies your plugin
// ============================================================================
[assembly: Guid("177b9193-0cec-429c-b9b1-19c776f26fe0")]

// ============================================================================
// MANDATORY: Version information
// Format: Major.Minor.Patch.Build - increment for each release
// ============================================================================
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// ============================================================================
// MANDATORY: Plugin display information
// ============================================================================
[assembly: AssemblyTitle("Astrovault")]
[assembly: AssemblyDescription("Automatically uploads captured astrophotography images to Astrovault cloud storage")]

// ============================================================================
// Author and product information
// ============================================================================
[assembly: AssemblyCompany("AstrosphereHub")]
[assembly: AssemblyProduct("Astrovault")]
[assembly: AssemblyCopyright("Copyright © 2026 AstrosphereHub")]

// ============================================================================
// NINA Compatibility - minimum version required to run this plugin
// ============================================================================
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.9001")]

// ============================================================================
// License information (MPL-2.0 is compatible with NINA)
// ============================================================================
[assembly: AssemblyMetadata("License", "MPL-2.0")]
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]

// ============================================================================
// Repository and homepage (update with your actual URLs)
// ============================================================================
[assembly: AssemblyMetadata("Repository", "https://github.com/ashanuoc/astrovault-nina-plugin")]
[assembly: AssemblyMetadata("Homepage", "https://vault.astrospherehub.com/")]
[assembly: AssemblyMetadata("FeaturedImageURL", "https://github.com/ashanuoc/astrovault-nina-plugin/releases/download/1.0.0.0/logo.png")]

// ============================================================================
// Optional: Tags for plugin discovery in N.I.N.A. plugin manager
// ============================================================================
[assembly: AssemblyMetadata("Tags", "cloud, upload, backup, sync, storage, astrovault")]

// ============================================================================
// Optional: Detailed description shown when user clicks plugin details
// ============================================================================
[assembly: AssemblyMetadata("LongDescription", @"Astrovault automatically uploads your captured astrophotography images to secure cloud storage.

Features:
- Automatic upload when images are saved
- Persistent queue that survives restarts
- Retry logic with exponential backoff
- Non-blocking uploads (doesn't slow down imaging)

Configure your API endpoint and key in the plugin options.")]

// ============================================================================
// Test project access to internal members
// ============================================================================
[assembly: InternalsVisibleTo("CloudUploadPlugin.Tests")]

// ============================================================================
// COM visibility (not needed for NINA plugins)
// ============================================================================
[assembly: ComVisible(false)]
