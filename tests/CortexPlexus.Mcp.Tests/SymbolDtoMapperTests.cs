using CortexPlexus.App.Api.Dto;
using CortexPlexus.Core.Models;

namespace CortexPlexus.Mcp.Tests;

/// <summary>
/// ADR-016 C2: the agent-upload round-trip must preserve api_endpoint nodes. Before the fix,
/// ToModel only matched kind "endpoint"; emitters set "api_endpoint", so an uploaded endpoint
/// fell through to the NamespaceInfo default and lost HttpMethod/RouteTemplate.
/// </summary>
public class SymbolDtoMapperTests
{
    [Fact]
    public void ApiEndpoint_RoundTrip_PreservesTypeAndRouteFields()
    {
        var original = new ApiEndpointInfo
        {
            Fqn = "API:GET:/users",
            Name = "GET /users",
            Kind = "api_endpoint",
            FilePath = "app/routes.py",
            StartLine = 10,
            EndLine = 12,
            HttpMethod = "GET",
            RouteTemplate = "/users",
            HandlerMethodFqn = "app.routes.list_users",
        };

        var dto = SymbolDtoMapper.FromModel(original);
        var restored = SymbolDtoMapper.ToModel(dto, Guid.NewGuid());

        var api = Assert.IsType<ApiEndpointInfo>(restored);
        Assert.Equal("api_endpoint", api.Kind);
        Assert.Equal("GET", api.HttpMethod);
        Assert.Equal("/users", api.RouteTemplate);
        Assert.Equal("app.routes.list_users", api.HandlerMethodFqn);
    }

    [Fact]
    public void LegacyEndpointKind_StillMapsToApiEndpoint()
    {
        // Back-compat: any payload that used the older "endpoint" kind must still deserialize.
        var dto = new SymbolDto
        {
            Fqn = "API:POST:/items",
            Name = "POST /items",
            Kind = "endpoint",
            HttpMethod = "POST",
            RouteTemplate = "/items",
        };

        var restored = SymbolDtoMapper.ToModel(dto, Guid.NewGuid());

        var api = Assert.IsType<ApiEndpointInfo>(restored);
        Assert.Equal("POST", api.HttpMethod);
        Assert.Equal("/items", api.RouteTemplate);
    }
}
