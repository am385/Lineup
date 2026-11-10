namespace Lineup.App.Web.Services;

/// <summary>Maps the management host's built-in and custom guide publication downloads.</summary>
internal static class GuideOutputEndpoints
{
    /// <summary>Registers the existing guide download routes and their publication behavior.</summary>
    internal static void MapGuideOutputEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/xmltv", async Task<IResult> (
            ITopologyGuideService guides,
            ILineupSettingsService settings,
            IStreamingAdapterRegistry adapters,
            IXmltvPublicationStore publications,
            bool? download,
            CancellationToken cancellationToken) =>
        {
            var output = settings.Settings.Streaming.GuideOutputs.SingleOrDefault(candidate => candidate.IsBuiltIn && candidate.Enabled);
            return output is null
                ? Results.NotFound("The built-in XMLTV guide output is not enabled.")
                : await ServeGuideOutputAsync(output, guides, adapters, publications, download ?? true, cancellationToken);
        })
            .DisableAntiforgery()
            .WithName("GetGlobalXmltvGuideOutput")
            .WithTags("Guide Outputs")
            .WithSummary("Get the global XMLTV guide output")
            .WithDescription("Returns the built-in XMLTV output inline or as a file attachment. The download parameter defaults to true.");

        endpoints.MapGet("/api/guide-outputs/{guideOutputIdentifier}", async Task<IResult> (
            string guideOutputIdentifier,
            ITopologyGuideService guides,
            ILineupSettingsService settings,
            IStreamingAdapterRegistry adapters,
            IXmltvPublicationStore publications,
            bool? download,
            CancellationToken cancellationToken) =>
        {
            var output = GuideOutputIdentifierResolver.Resolve(
                settings.Settings.Streaming.GuideOutputs,
                guideOutputIdentifier);
            return output is null
                ? Results.NotFound()
                : await ServeGuideOutputAsync(output, guides, adapters, publications, download ?? true, cancellationToken);
        })
            .DisableAntiforgery()
            .WithName("GetGuideOutput")
            .WithTags("Guide Outputs")
            .WithSummary("Get a configured guide output")
            .WithDescription("Returns a guide output selected by its editable key or stable identifier, inline or as a file attachment. The download parameter defaults to true.");
    }

    private static async Task<IResult> ServeGuideOutputAsync(
        GuideOutputDefinition output,
        ITopologyGuideService guides,
        IStreamingAdapterRegistry adapters,
        IXmltvPublicationStore publications,
        bool download,
        CancellationToken cancellationToken)
    {
        GuideOutputDestination destination;
        try
        {
            destination = adapters.GetGuideOutput(output.Adapter.Kind).ResolveDestination(output.Adapter);
        }
        catch (InvalidOperationException exception)
        {
            return Results.NotFound(exception.Message);
        }

        if (!publications.Exists(destination.Path))
        {
            var publication = await guides.PublishAsync(output.Id, cancellationToken);
            if (publication is null || !publication.Succeeded)
            {
                return Results.NotFound(publication?.Error ?? "The guide output is not available.");
            }
        }

        var stream = publications.OpenRead(destination.Path);
        if (stream is null)
        {
            return Results.NotFound("The guide output publication is not available.");
        }
        return download
            ? Results.File(stream, destination.ContentType, destination.FileName)
            : Results.File(stream, destination.ContentType);
    }
}
