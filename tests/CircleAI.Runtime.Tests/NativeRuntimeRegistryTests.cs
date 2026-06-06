// NativeRuntimeRegistryTests.cs
//
// Verifies the embedded registry loads, lookups return the expected bundle
// for each (Os, Arch, Backend) tuple, and the parser tolerates non-object
// top-level entries (_notes array).

using System.IO;
using System.Text;
using CircleAI.Runtime.Backends;
using CircleAI.Runtime.Capabilities;
using CircleAI.Runtime.NativeRuntimes;
using Xunit;

namespace CircleAI.Runtime.Tests;

public sealed class NativeRuntimeRegistryTests
{
    [Fact]
    public void Embedded_Registry_Loads_With_At_Least_One_Bundle_Per_Major_Os()
    {
        var reg = NativeRuntimeRegistry.LoadEmbedded();

        Assert.NotEmpty(reg.All);
        Assert.Contains(reg.All, b => b.Os == OperatingSystemKind.Windows);
        Assert.Contains(reg.All, b => b.Os == OperatingSystemKind.Linux);
        Assert.Contains(reg.All, b => b.Os == OperatingSystemKind.MacOS);
        Assert.Contains(reg.All, b => b.Os == OperatingSystemKind.Android);
    }

    [Fact]
    public void Embedded_Registry_Has_Windows_X64_OpenCL_Bundle_With_Real_Sha256()
    {
        // Alibaba MNN 3.5.0 ships ONE Windows x64 bundle (cpu+opencl). CUDA is
        // not pre-built — caller would build from source. We verify the real
        // OpenCL/CPU bundle is present with its real SHA-256.
        var reg = NativeRuntimeRegistry.LoadEmbedded();
        var bundle = reg.Find(OperatingSystemKind.Windows, ArchitectureKind.X64, BackendKind.OpenCL);
        Assert.NotNull(bundle);
        Assert.Equal("3.5.0", bundle!.MnnVersion);
        Assert.Equal("github.com", bundle.PrimaryUri.Host);
        Assert.Equal(
            "e37dbed6a5a6c26122239468d7fc8569d003c7f4a12c8a8024a33660fb13e4b7",
            bundle.ArchiveSha256Hex);
        Assert.NotNull(bundle.FallbackUri);
    }

    [Fact]
    public void Embedded_Registry_Does_Not_Claim_Cuda_Bundles_That_Are_Not_Shipped()
    {
        // CUDA is not in any of MNN's pre-built archives — guard against
        // somebody re-adding speculative entries.
        var reg = NativeRuntimeRegistry.LoadEmbedded();
        Assert.Null(reg.Find(OperatingSystemKind.Windows, ArchitectureKind.X64, BackendKind.Cuda));
        Assert.Null(reg.Find(OperatingSystemKind.Linux,   ArchitectureKind.X64, BackendKind.Cuda));
    }

    [Fact]
    public void Embedded_Registry_Has_MacOS_Arm64_Metal_Bundle_With_Framework_Binary_Name()
    {
        // macOS / iOS ship MNN as a framework: the binary at
        // MNN.framework/Versions/A/MNN has no prefix or extension, so the
        // registry's mnn_lib for these platforms is just "MNN". The
        // fetcher recognises the framework layout and finds the binary at
        // its real nested path.
        var reg = NativeRuntimeRegistry.LoadEmbedded();
        var bundle = reg.Find(OperatingSystemKind.MacOS, ArchitectureKind.Arm64, BackendKind.Metal);
        Assert.NotNull(bundle);
        Assert.Equal("MNN", bundle!.MnnCoreLibraryName);
    }

    [Fact]
    public void Embedded_Registry_Has_Android_Arm64_Vulkan_Bundle_With_Real_Sha256()
    {
        // Android universal bundle ships CPU + OpenCL + Vulkan. Confirm the
        // Vulkan tuple resolves to the real archive with its real hash.
        var reg = NativeRuntimeRegistry.LoadEmbedded();
        var bundle = reg.Find(OperatingSystemKind.Android, ArchitectureKind.Arm64, BackendKind.Vulkan);
        Assert.NotNull(bundle);
        Assert.Equal(
            "b5513459ee5d70dec98e7a0763ce2d09a9824897c150069e65b2b1a04570c573",
            bundle!.ArchiveSha256Hex);
    }

    [Fact]
    public void Embedded_Registry_Does_Not_Claim_Ascend_Or_Cambricon_Bundles_Not_Shipped()
    {
        // Alibaba does not pre-build Ascend (CANN) or Cambricon MLU archives.
        // Hosts using those accelerators currently build MNN from source.
        var reg = NativeRuntimeRegistry.LoadEmbedded();
        Assert.Null(reg.Find(OperatingSystemKind.Linux, ArchitectureKind.Arm64, BackendKind.Ascend));
        Assert.Null(reg.Find(OperatingSystemKind.Linux, ArchitectureKind.X64,   BackendKind.Ascend));
        Assert.Null(reg.Find(OperatingSystemKind.Linux, ArchitectureKind.X64,   BackendKind.Cambricon));
    }

    [Fact]
    public void Find_Returns_Null_For_Unregistered_Tuple()
    {
        var reg = NativeRuntimeRegistry.LoadEmbedded();
        var bundle = reg.Find(OperatingSystemKind.IOS, ArchitectureKind.X86, BackendKind.Cambricon);
        Assert.Null(bundle);
    }

    [Fact]
    public void Parser_Tolerates_NonObject_Notes_Entry()
    {
        var json = @"
        {
            ""_notes"": [""a"", ""b"", ""c""],
            ""mnn_versions"": [
              { ""version"": ""1.0"", ""bundles"": [
                  { ""os"": ""Linux"", ""arch"": ""X64"", ""backend"": ""Cpu"",
                    ""url"": ""https://example.com/x.tar.gz"" }
              ]}
            ]
        }";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var reg = NativeRuntimeRegistry.LoadFromStream(stream);
        Assert.Single(reg.All);
    }

    [Fact]
    public void Parser_Skips_Bundle_With_Invalid_Url()
    {
        var json = @"
        {
            ""mnn_versions"": [
              { ""version"": ""1.0"", ""bundles"": [
                  { ""os"": ""Linux"", ""arch"": ""X64"", ""backend"": ""Cpu"",
                    ""url"": ""not-a-real-uri"" },
                  { ""os"": ""Linux"", ""arch"": ""Arm64"", ""backend"": ""Cpu"",
                    ""url"": ""https://valid.example/x"" }
              ]}
            ]
        }";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var reg = NativeRuntimeRegistry.LoadFromStream(stream);
        Assert.Single(reg.All);
        Assert.Equal(ArchitectureKind.Arm64, reg.All[0].Arch);
    }

    [Fact]
    public void Parser_Skips_Bundle_With_Unknown_Enum_Values()
    {
        var json = @"
        {
            ""mnn_versions"": [
              { ""version"": ""1.0"", ""bundles"": [
                  { ""os"": ""Plan9"", ""arch"": ""X64"", ""backend"": ""Cpu"",
                    ""url"": ""https://example.com/x"" },
                  { ""os"": ""Linux"", ""arch"": ""Vulkan"", ""backend"": ""Cpu"",
                    ""url"": ""https://example.com/y"" },
                  { ""os"": ""Linux"", ""arch"": ""X64"", ""backend"": ""Tpu99"",
                    ""url"": ""https://example.com/z"" }
              ]}
            ]
        }";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var reg = NativeRuntimeRegistry.LoadFromStream(stream);
        Assert.Empty(reg.All);
    }
}
