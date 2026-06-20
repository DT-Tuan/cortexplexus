using CortexPlexus.App.Api.Dto;
using CortexPlexus.Core.Models;

namespace CortexPlexus.Mcp.Tests;

/// <summary>
/// ADR-016 C3: the agent-upload round-trip must preserve di_registration nodes. ToModel only
/// matched the hyphen form "di-registration", but emitters set "di_registration" (underscore —
/// the only form SanitizeLabel keeps as the di_registration label). Without the underscore case an
/// uploaded registration fell through to NamespaceInfo, losing ServiceTypeFqn/Lifetime.
/// </summary>
public class DiRegistrationRoundTripTests
{
    [Fact]
    public void DiRegistration_RoundTrip_PreservesTypeAndFields()
    {
        var original = new DiRegistrationInfo
        {
            Fqn = "DI:com.example.UserService->com.example.UserService",
            Name = "Service UserService",
            Kind = "di_registration",
            FilePath = "src/UserService.java",
            StartLine = 5,
            EndLine = 9,
            ServiceTypeFqn = "com.example.UserService",
            ImplementationTypeFqn = "com.example.UserService",
            Lifetime = "Singleton",
            ModuleName = "Service",
        };

        var dto = SymbolDtoMapper.FromModel(original);
        var restored = SymbolDtoMapper.ToModel(dto, Guid.NewGuid());

        var di = Assert.IsType<DiRegistrationInfo>(restored);
        Assert.Equal("di_registration", di.Kind);
        Assert.Equal("com.example.UserService", di.ServiceTypeFqn);
        Assert.Equal("com.example.UserService", di.ImplementationTypeFqn);
        Assert.Equal("Singleton", di.Lifetime);
    }

    [Fact]
    public void LegacyHyphenKind_StillMapsToDiRegistration()
    {
        var dto = new SymbolDto
        {
            Fqn = "DI:X->X",
            Name = "AddScoped<X>",
            Kind = "di-registration",
            ServiceTypeFqn = "X",
            ImplementationTypeFqn = "X",
            Lifetime = "Scoped",
        };

        var restored = SymbolDtoMapper.ToModel(dto, Guid.NewGuid());

        var di = Assert.IsType<DiRegistrationInfo>(restored);
        Assert.Equal("Scoped", di.Lifetime);
    }
}
