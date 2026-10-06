using System.Reflection;
using Xunit;

namespace CircleAI.DeviceTests;

/// <summary>
/// The Android heads are present, loadable, and carry the version this build cut.
/// </summary>
/// <remarks>
/// THESE ARE THE ASSERTIONS NO OTHER SUITE IN THE REPO CAN MAKE. The sixteen others
/// target net9.0/net10.0 and cannot reference a net10.0-android library at all, so
/// nothing has ever asserted anything about CircleAI.Device, CircleAI.Client or
/// CircleAI.Assistant.Device.
///
/// They are deliberately shallow. The value is not in the assertions — it is that
/// compiling this file at all forces the whole Android dependency graph to resolve.
/// That is what catches NETSDK1082 (which shipped in 3.8.0 and breaks every Android
/// consumer), a reference left dangling by a refactor, or a linker that stripped a
/// test assembly. None of that needs a device; `dotnet build -f net10.0-android` is
/// enough, and that is why this project belongs in the gate rather than only in a
/// device run.
///
/// A type is resolved by NAME rather than with typeof(), on purpose: typeof() is
/// checked by the compiler and would fail the BUILD, which tells you nothing about
/// what survived trimming into the APK. Reflection asks the shipped assembly.
/// </remarks>
public class AndroidHeadReachTests
{
    [Theory]
    [InlineData("CircleAI.Assistant.Device")]
    [InlineData("CircleAI.Client")]
    [InlineData("CircleAI.Device")]
    [InlineData("CircleAI")]
    public void The_head_assembly_is_in_the_package(string assemblyName)
    {
        var asm = Assembly.Load(assemblyName);
        Assert.NotNull(asm);
        Assert.NotEmpty(asm.GetTypes());
    }

    [Fact]
    public void Every_CircleAI_assembly_reports_the_same_version()
    {
        // The 3.7.0 cut moved <Version> and left <AssemblyVersion> behind, so
        // packages went out at one number carrying assemblies stamped another. On
        // device that mismatch is invisible; here it is one assertion.
        var versions = new[] { "CircleAI", "CircleAI.Device", "CircleAI.Client", "CircleAI.Assistant.Device" }
            .Select(Assembly.Load)
            .Select(a => a.GetName().Version?.ToString())
            .Distinct()
            .ToList();

        Assert.Single(versions);
        Assert.NotEqual("0.0.0.0", versions[0]);
    }

    [Fact]
    public void The_model_registry_resource_is_still_named_what_its_callers_expect()
    {
        // Folding 169 libraries into one assembly renames every embedded resource,
        // because a resource is $(RootNamespace) plus its path - and six call sites
        // look theirs up by an exact string. GetManifestResourceStream returns NULL
        // for a name that does not exist; it does not throw. So the failure mode is
        // a model catalogue that silently has nothing in it.
        var asm = Assembly.Load("CircleAI");
        var names = asm.GetManifestResourceNames();

        Assert.Contains("CircleAI.Core.Models.embedded_registry.json", names);
        Assert.Contains("CircleAI.Skills.capabilities.json", names);
    }
}
