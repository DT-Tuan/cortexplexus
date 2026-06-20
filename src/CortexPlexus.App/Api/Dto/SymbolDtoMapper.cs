using CortexPlexus.Core.Models;

namespace CortexPlexus.App.Api.Dto;

public static class SymbolDtoMapper
{
    public static CodeSymbol ToModel(SymbolDto dto, Guid repoId)
    {
        return dto.Kind.ToLowerInvariant() switch
        {
            "class" or "struct" or "record" => new ClassInfo
            {
                Fqn = dto.Fqn, Name = dto.Name, Kind = dto.Kind,
                FilePath = dto.FilePath, StartLine = dto.StartLine, EndLine = dto.EndLine,
                RepoId = repoId, Documentation = dto.Documentation, AiSummary = dto.AiSummary,
                Accessibility = dto.Accessibility,
                IsAbstract = dto.IsAbstract ?? false,
                IsStatic = dto.IsStatic ?? false,
                IsSealed = dto.IsSealed ?? false,
                IsPartial = dto.IsPartial ?? false,
                BaseTypeFqn = dto.BaseTypeFqn,
                InterfaceFqns = dto.InterfaceFqns ?? []
            },
            "method" or "function" => new MethodInfo
            {
                Fqn = dto.Fqn, Name = dto.Name, Kind = dto.Kind,
                FilePath = dto.FilePath, StartLine = dto.StartLine, EndLine = dto.EndLine,
                RepoId = repoId, Documentation = dto.Documentation, AiSummary = dto.AiSummary,
                Signature = dto.Signature ?? dto.Name,
                ReturnType = dto.ReturnType,
                Accessibility = dto.Accessibility,
                IsAsync = dto.IsAsync ?? false,
                IsStatic = dto.IsStatic ?? false,
                IsVirtual = dto.IsVirtual ?? false,
                IsOverride = dto.IsOverride ?? false,
                IsTestMethod = dto.IsTestMethod ?? false,
                ContainingTypeFqn = dto.ContainingTypeFqn,
                Parameters = dto.Parameters?.Select(p => new ParameterInfo(p.Name, p.Type, p.Position)).ToList() ?? []
            },
            "interface" => new InterfaceInfo
            {
                Fqn = dto.Fqn, Name = dto.Name, Kind = dto.Kind,
                FilePath = dto.FilePath, StartLine = dto.StartLine, EndLine = dto.EndLine,
                RepoId = repoId, Documentation = dto.Documentation, AiSummary = dto.AiSummary,
                Accessibility = dto.Accessibility,
                MemberFqns = dto.MemberFqns ?? []
            },
            "property" => new PropertyInfo
            {
                Fqn = dto.Fqn, Name = dto.Name, Kind = dto.Kind,
                FilePath = dto.FilePath, StartLine = dto.StartLine, EndLine = dto.EndLine,
                RepoId = repoId, Documentation = dto.Documentation, AiSummary = dto.AiSummary,
                Type = dto.Type ?? "object",
                HasGetter = dto.HasGetter ?? false,
                HasSetter = dto.HasSetter ?? false,
                ContainingTypeFqn = dto.ContainingTypeFqn
            },
            "constructor" => new ConstructorInfo
            {
                Fqn = dto.Fqn, Name = dto.Name, Kind = dto.Kind,
                FilePath = dto.FilePath, StartLine = dto.StartLine, EndLine = dto.EndLine,
                RepoId = repoId, Documentation = dto.Documentation, AiSummary = dto.AiSummary,
                Signature = dto.Signature ?? dto.Name,
                Accessibility = dto.Accessibility,
                ContainingTypeFqn = dto.ContainingTypeFqn,
                Parameters = dto.Parameters?.Select(p => new ParameterInfo(p.Name, p.Type, p.Position)).ToList() ?? []
            },
            "namespace" => new NamespaceInfo
            {
                Fqn = dto.Fqn, Name = dto.Name, Kind = dto.Kind,
                FilePath = dto.FilePath, StartLine = dto.StartLine, EndLine = dto.EndLine,
                RepoId = repoId
            },
            "document" or "section" => new DocumentSection
            {
                Fqn = dto.Fqn, Name = dto.Name, Kind = dto.Kind,
                FilePath = dto.FilePath, StartLine = dto.StartLine, EndLine = dto.EndLine,
                RepoId = repoId, Documentation = dto.Documentation, AiSummary = dto.AiSummary,
                Level = dto.Level ?? 0,
                Content = dto.Content ?? "",
                DocumentPath = dto.DocumentPath ?? dto.FilePath ?? ""
            },
            "dbcontext" => new DbContextInfo
            {
                Fqn = dto.Fqn, Name = dto.Name, Kind = dto.Kind,
                FilePath = dto.FilePath, StartLine = dto.StartLine, EndLine = dto.EndLine,
                RepoId = repoId, Documentation = dto.Documentation, AiSummary = dto.AiSummary,
                DbSets = dto.DbSets?.Select(d => new DbSetInfo(d.EntityTypeFqn, d.PropertyName, d.TableName)).ToList() ?? []
            },
            "di-registration" => new DiRegistrationInfo
            {
                Fqn = dto.Fqn, Name = dto.Name, Kind = dto.Kind,
                FilePath = dto.FilePath, StartLine = dto.StartLine, EndLine = dto.EndLine,
                RepoId = repoId, Documentation = dto.Documentation, AiSummary = dto.AiSummary,
                ServiceTypeFqn = dto.ServiceTypeFqn ?? dto.Fqn,
                ImplementationTypeFqn = dto.ImplementationTypeFqn ?? dto.Fqn,
                Lifetime = dto.Lifetime ?? "Scoped",
                ModuleName = dto.ModuleName
            },
            // "api_endpoint" is the Kind the emitters actually set (Roslyn + tree-sitter, ADR-016 C2);
            // "endpoint" kept for back-compat. Without the api_endpoint case an uploaded endpoint
            // fell through to the NamespaceInfo default, dropping HttpMethod/RouteTemplate.
            "endpoint" or "api_endpoint" => new ApiEndpointInfo
            {
                Fqn = dto.Fqn, Name = dto.Name, Kind = dto.Kind,
                FilePath = dto.FilePath, StartLine = dto.StartLine, EndLine = dto.EndLine,
                RepoId = repoId, Documentation = dto.Documentation, AiSummary = dto.AiSummary,
                HttpMethod = dto.HttpMethod ?? "GET",
                RouteTemplate = dto.RouteTemplate ?? "/",
                HandlerMethodFqn = dto.HandlerMethodFqn,
                EndpointName = dto.EndpointName,
                Summary = dto.Summary,
                ModuleName = dto.ModuleName
            },
            _ => new NamespaceInfo
            {
                Fqn = dto.Fqn, Name = dto.Name, Kind = dto.Kind,
                FilePath = dto.FilePath, StartLine = dto.StartLine, EndLine = dto.EndLine,
                RepoId = repoId
            }
        };
    }

    public static Relationship ToRelationship(RelationshipDto dto)
    {
        var type = Enum.TryParse<RelationshipType>(dto.Type, ignoreCase: true, out var parsed)
            ? parsed
            : RelationshipType.References;

        return new Relationship(dto.FromFqn, dto.ToFqn, type, dto.Metadata);
    }

    public static SymbolDto FromModel(CodeSymbol symbol)
    {
        var dto = new SymbolDto
        {
            Fqn = symbol.Fqn, Name = symbol.Name, Kind = symbol.Kind,
            FilePath = symbol.FilePath, StartLine = symbol.StartLine, EndLine = symbol.EndLine,
            Documentation = symbol.Documentation, AiSummary = symbol.AiSummary
        };

        return symbol switch
        {
            ClassInfo c => dto with
            {
                Accessibility = c.Accessibility, IsAbstract = c.IsAbstract,
                IsStatic = c.IsStatic, IsSealed = c.IsSealed, IsPartial = c.IsPartial,
                BaseTypeFqn = c.BaseTypeFqn, InterfaceFqns = c.InterfaceFqns
            },
            MethodInfo m => dto with
            {
                Signature = m.Signature, ReturnType = m.ReturnType,
                Accessibility = m.Accessibility, IsAsync = m.IsAsync,
                IsStatic = m.IsStatic, IsVirtual = m.IsVirtual, IsOverride = m.IsOverride, IsTestMethod = m.IsTestMethod,
                ContainingTypeFqn = m.ContainingTypeFqn,
                Parameters = m.Parameters.Select(p => new ParameterDto(p.Name, p.Type, p.Position)).ToList()
            },
            InterfaceInfo i => dto with
            {
                Accessibility = i.Accessibility, MemberFqns = i.MemberFqns
            },
            PropertyInfo p => dto with
            {
                Type = p.Type, HasGetter = p.HasGetter, HasSetter = p.HasSetter,
                ContainingTypeFqn = p.ContainingTypeFqn
            },
            ConstructorInfo ct => dto with
            {
                Signature = ct.Signature, Accessibility = ct.Accessibility,
                ContainingTypeFqn = ct.ContainingTypeFqn,
                Parameters = ct.Parameters.Select(p => new ParameterDto(p.Name, p.Type, p.Position)).ToList()
            },
            DocumentSection d => dto with
            {
                Level = d.Level, Content = d.Content, DocumentPath = d.DocumentPath
            },
            DbContextInfo db => dto with
            {
                DbSets = db.DbSets.Select(s => new DbSetDto(s.EntityTypeFqn, s.PropertyName, s.TableName)).ToList()
            },
            DiRegistrationInfo di => dto with
            {
                ServiceTypeFqn = di.ServiceTypeFqn, ImplementationTypeFqn = di.ImplementationTypeFqn,
                Lifetime = di.Lifetime, ModuleName = di.ModuleName
            },
            ApiEndpointInfo api => dto with
            {
                HttpMethod = api.HttpMethod, RouteTemplate = api.RouteTemplate,
                HandlerMethodFqn = api.HandlerMethodFqn, EndpointName = api.EndpointName,
                Summary = api.Summary, ModuleName = api.ModuleName
            },
            _ => dto
        };
    }

    public static RelationshipDto FromRelationship(Relationship rel)
    {
        return new RelationshipDto
        {
            FromFqn = rel.FromFqn,
            ToFqn = rel.ToFqn,
            Type = rel.Type.ToString(),
            Metadata = rel.Metadata
        };
    }
}
