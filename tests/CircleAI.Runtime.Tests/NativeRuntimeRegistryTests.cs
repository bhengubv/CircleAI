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
    public void Embedded_Registry_Has_Windows_X64_Cuda_Bundle()
    {
        var reg = NativeRuntimeRegistry.LoadEmbedded();
        var bundle = reg.Find(OperatingSystemKind.Windows, ArchitectureKind.X64, BackendKind.Cuda);
        Assert.NotNull(bundle);
        Assert.Equal("3.0.0", bundle!.MnnVersion);
        Assert.Equal("modelscope.cn", bundle.PrimaryUri.Host);
        Assert.NotNull(bundle.FallbackUri);
    }

    [Fact]
    public void Embedded_Registry_Has_MacOS_Arm64_Metal_Bundle()
    {
        var reg = NativeRuntimeRegistry.LoadEmbedded();
        var bundle = reg.Find(OperatingSystemKind.MacOS, ArchitectureKind.Arm64, BackendKind.Metal);
        Assert.NotNull(bundle);
        Assert.Equal("libmnnbridge.dylib", bundle!.MnnBridgeLibraryName);
        Assert.Equal("libMNN.dylib", bundle.MnnCoreLibraryName);
    }

    [Fact]
    public void Embedded_Registry_Has_Linux_Arm64_Ascend_Bundle_For_Huawei_Atlas()
    {
        var reg = NativeRuntimeRegistry.LoadEmbedded();
        var bundle = reg.Find(OperatingSystemKind.Linux, ArchitectureKind.Arm64, BackendKind.Ascend);
        Assert.NotNull(bundle);
        Assert.Equal(BackendKind.Ascend, bundle!.Backend);
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
