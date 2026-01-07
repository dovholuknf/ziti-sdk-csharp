using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SmokeTest.Tests;

[StructLayout(LayoutKind.Sequential)]
internal struct ziti_version
{
    public IntPtr version;     // const char*
    public IntPtr revision;    // const char*
    public IntPtr build_date;  // const char*
}

[TestClass]
public class NativeLibraryTests
{
    [DllImport("ziti", EntryPoint = "ziti_get_version", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ziti_get_version();

    [TestMethod]
    public void CanLoadNativeLibrary()
    {
        // This will throw DllNotFoundException if the native library can't be loaded
        IntPtr versionPtr = ziti_get_version();

        Assert.AreNotEqual(IntPtr.Zero, versionPtr, "ziti_get_version returned null pointer");
    }

    [TestMethod]
    public void CanGetVersionInfo()
    {
        IntPtr versionPtr = ziti_get_version();
        ziti_version v = Marshal.PtrToStructure<ziti_version>(versionPtr);

        string? version = Marshal.PtrToStringUTF8(v.version);
        string? revision = Marshal.PtrToStringUTF8(v.revision);
        string? build_date = Marshal.PtrToStringUTF8(v.build_date);

        Assert.IsNotNull(version, "Version string is null");
        Assert.IsNotNull(revision, "Revision string is null");
        Assert.IsNotNull(build_date, "Build date string is null");

        Assert.IsFalse(string.IsNullOrWhiteSpace(version), "Version string is empty");
        Assert.IsFalse(string.IsNullOrWhiteSpace(revision), "Revision string is empty");
        Assert.IsFalse(string.IsNullOrWhiteSpace(build_date), "Build date string is empty");

        // Output for debugging
        Console.WriteLine($"Native Library Version Info:");
        Console.WriteLine($"  version={version}");
        Console.WriteLine($"  revision={revision}");
        Console.WriteLine($"  build_date={build_date}");
    }

    [TestMethod]
    public void VersionMatchesPackageVersion()
    {
        IntPtr versionPtr = ziti_get_version();
        ziti_version v = Marshal.PtrToStructure<ziti_version>(versionPtr);
        string? nativeVersion = Marshal.PtrToStringUTF8(v.version);

        Assert.IsNotNull(nativeVersion, "Native version is null");

        // Get expected version from environment variable (set by test script)
        string? expectedVersion = Environment.GetEnvironmentVariable("EXPECTED_ZITI_VERSION");

        if (!string.IsNullOrEmpty(expectedVersion))
        {
            Assert.AreEqual(expectedVersion, nativeVersion,
                $"Native library version ({nativeVersion}) doesn't match expected version ({expectedVersion})");
        }
        else
        {
            // If no expected version set, just verify it's not empty
            Assert.IsFalse(string.IsNullOrWhiteSpace(nativeVersion), "Version string is empty");
            Console.WriteLine($"Warning: EXPECTED_ZITI_VERSION not set. Native version is: {nativeVersion}");
        }
    }
}
