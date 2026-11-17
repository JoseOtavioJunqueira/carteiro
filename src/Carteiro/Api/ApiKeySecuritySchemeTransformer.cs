using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi.Models;

namespace Carteiro.Api;

/// <summary>
/// Declares the X-Api-Key header in the OpenAPI document so clients and tools know to send it.
/// </summary>
internal sealed class ApiKeySecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    private const string SchemeId = "ApiKey";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info.Title = "Carteiro";
        document.Info.Description = "Reliable webhook delivery. Every operation requires the X-Api-Key header.";

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes[SchemeId] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = ApiKeyFilter.HeaderName,
        };

        document.SecurityRequirements.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = SchemeId } }] = [],
        });

        return Task.CompletedTask;
    }
}
