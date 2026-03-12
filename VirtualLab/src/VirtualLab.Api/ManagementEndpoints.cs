using Microsoft.EntityFrameworkCore;
using VirtualLab.Application.FccProfiles;
using VirtualLab.Application.Management;
using VirtualLab.Infrastructure.Management;
using VirtualLab.Infrastructure.Persistence;

namespace VirtualLab.Api;

internal static class ManagementEndpoints
{
    public static IEndpointRouteBuilder MapVirtualLabManagementEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/lab-environment", async (IVirtualLabManagementService managementService, CancellationToken cancellationToken) =>
        {
            LabEnvironmentDetailView? environment = await managementService.GetDefaultLabEnvironmentAsync(cancellationToken);
            return environment is null ? Results.NotFound() : Results.Ok(environment);
        });

        app.MapPut("/api/lab-environment", async (LabEnvironmentUpsertRequest request, IVirtualLabManagementService managementService, CancellationToken cancellationToken) =>
            await ExecuteAsync(
                async () =>
                {
                    LabEnvironmentDetailView? environment = await managementService.UpdateDefaultLabEnvironmentAsync(request, cancellationToken);
                    return environment is null ? Results.NotFound() : Results.Ok(environment);
                }));

        app.MapPost("/api/lab-environment/prune", async (LabEnvironmentPruneRequest? request, IVirtualLabManagementService managementService, CancellationToken cancellationToken) =>
            await ExecuteAsync(
                async () =>
                {
                    LabEnvironmentPruneResult? result = await managementService.PruneDefaultLabEnvironmentAsync(
                        request ?? new LabEnvironmentPruneRequest(),
                        cancellationToken);

                    return result is null ? Results.NotFound() : Results.Ok(result);
                }));

        app.MapGet("/api/lab-environment/export", async (bool? includeRuntimeData, IVirtualLabManagementService managementService, CancellationToken cancellationToken) =>
        {
            LabEnvironmentExportPackage? package = await managementService.ExportDefaultLabEnvironmentAsync(
                new LabEnvironmentExportRequest
                {
                    IncludeRuntimeData = includeRuntimeData,
                },
                cancellationToken);

            return package is null ? Results.NotFound() : Results.Ok(package);
        });

        app.MapPost("/api/lab-environment/import", async (LabEnvironmentImportRequest request, IVirtualLabManagementService managementService, CancellationToken cancellationToken) =>
            await ExecuteAsync(
                async () => Results.Ok(await managementService.ImportLabEnvironmentAsync(request, cancellationToken))));

        app.MapGet("/api/sites", async (bool? includeInactive, IVirtualLabManagementService managementService, CancellationToken cancellationToken) =>
            Results.Ok(await managementService.ListSitesAsync(includeInactive ?? true, cancellationToken)));

        app.MapGet("/api/sites/{id:guid}", async (Guid id, IVirtualLabManagementService managementService, CancellationToken cancellationToken) =>
        {
            SiteDetailView? site = await managementService.GetSiteAsync(id, cancellationToken);
            return site is null ? Results.NotFound() : Results.Ok(site);
        });

        app.MapPost("/api/sites", async (SiteUpsertRequest request, IVirtualLabManagementService managementService, CancellationToken cancellationToken) =>
            await ExecuteAsync(
                async () =>
                {
                    SiteDetailView created = await managementService.CreateSiteAsync(request, cancellationToken);
                    return Results.Created($"/api/sites/{created.Id}", created);
                }));

        app.MapPut("/api/sites/{id:guid}", async (Guid id, SiteUpsertRequest request, IVirtualLabManagementService managementService, CancellationToken cancellationToken) =>
            await ExecuteAsync(
                async () =>
                {
                    SiteDetailView? updated = await managementService.UpdateSiteAsync(id, request, cancellationToken);
                    return updated is null ? Results.NotFound() : Results.Ok(updated);
                }));

        app.MapDelete("/api/sites/{id:guid}", async (Guid id, IVirtualLabManagementService managementService, CancellationToken cancellationToken) =>
        {
            SiteDetailView? archived = await managementService.ArchiveSiteAsync(id, cancellationToken);
            return archived is null ? Results.NotFound() : Results.Ok(archived);
        });

        app.MapPost("/api/sites/{id:guid}/duplicate", async (Guid id, DuplicateSiteRequest request, IVirtualLabManagementService managementService, CancellationToken cancellationToken) =>
            await ExecuteAsync(
                async () =>
                {
                    SiteDetailView? duplicate = await managementService.DuplicateSiteAsync(id, request, cancellationToken);
                    return duplicate is null ? Results.NotFound() : Results.Created($"/api/sites/{duplicate.Id}", duplicate);
                }));

        app.MapGet("/api/sites/{id:guid}/forecourt", async (Guid id, IVirtualLabManagementService managementService, CancellationToken cancellationToken) =>
        {
            SiteForecourtView? forecourt = await managementService.GetForecourtAsync(id, cancellationToken);
            return forecourt is null ? Results.NotFound() : Results.Ok(forecourt);
        });

        app.MapPut("/api/sites/{id:guid}/forecourt", async (Guid id, SaveForecourtRequest request, IVirtualLabManagementService managementService, CancellationToken cancellationToken) =>
            await ExecuteAsync(
                async () =>
                {
                    SiteForecourtView? forecourt = await managementService.SaveForecourtAsync(id, request, cancellationToken);
                    return forecourt is null ? Results.NotFound() : Results.Ok(forecourt);
                }));

        app.MapPost("/api/sites/{id:guid}/seed", async (Guid id, SiteSeedRequest? request, IVirtualLabManagementService managementService, CancellationToken cancellationToken) =>
            await ExecuteAsync(
                async () =>
                {
                    SiteSeedResult? result = await managementService.SeedSiteAsync(id, request ?? new SiteSeedRequest(), cancellationToken);
                    return result is null ? Results.NotFound() : Results.Ok(result);
                }));

        app.MapPost("/api/sites/{id:guid}/reset", async (Guid id, IVirtualLabManagementService managementService, CancellationToken cancellationToken) =>
        {
            SiteSeedResult? result = await managementService.ResetSiteAsync(id, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapGet("/api/products", async (bool? includeInactive, IVirtualLabManagementService managementService, CancellationToken cancellationToken) =>
            Results.Ok(await managementService.ListProductsAsync(includeInactive ?? true, cancellationToken)));

        app.MapGet("/api/products/{id:guid}", async (Guid id, IVirtualLabManagementService managementService, CancellationToken cancellationToken) =>
        {
            ProductView? product = await managementService.GetProductAsync(id, cancellationToken);
            return product is null ? Results.NotFound() : Results.Ok(product);
        });

        app.MapPost("/api/products", async (ProductUpsertRequest request, IVirtualLabManagementService managementService, CancellationToken cancellationToken) =>
            await ExecuteAsync(
                async () =>
                {
                    ProductView created = await managementService.CreateProductAsync(request, cancellationToken);
                    return Results.Created($"/api/products/{created.Id}", created);
                }));

        app.MapPut("/api/products/{id:guid}", async (Guid id, ProductUpsertRequest request, IVirtualLabManagementService managementService, CancellationToken cancellationToken) =>
            await ExecuteAsync(
                async () =>
                {
                    ProductView? updated = await managementService.UpdateProductAsync(id, request, cancellationToken);
                    return updated is null ? Results.NotFound() : Results.Ok(updated);
                }));

        app.MapDelete("/api/products/{id:guid}", async (Guid id, IVirtualLabManagementService managementService, CancellationToken cancellationToken) =>
            await ExecuteAsync(
                async () =>
                {
                    ProductView? archived = await managementService.ArchiveProductAsync(id, cancellationToken);
                    return archived is null ? Results.NotFound() : Results.Ok(archived);
                }));

        app.MapDelete("/api/fcc-profiles/{id:guid}", async (Guid id, VirtualLabDbContext dbContext, IFccProfileService profileService, CancellationToken cancellationToken) =>
            await ExecuteAsync(
                async () =>
                {
                    VirtualLab.Domain.Models.FccSimulatorProfile? profile = await dbContext.FccSimulatorProfiles
                        .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

                    if (profile is null)
                    {
                        return Results.NotFound();
                    }

                    bool hasActiveSites = await dbContext.Sites
                        .AsNoTracking()
                        .AnyAsync(x => x.ActiveFccSimulatorProfileId == id && x.IsActive, cancellationToken);

                    if (hasActiveSites)
                    {
                        throw new ManagementOperationException(
                            409,
                            "Profile cannot be archived while it is assigned to an active site.",
                            [new("id", $"Profile '{profile.ProfileKey}' is assigned to one or more active sites.", "Error", "profile_in_use")]);
                    }

                    profile.IsActive = false;
                    profile.IsDefault = false;
                    profile.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    await dbContext.SaveChangesAsync(cancellationToken);

                    FccProfileRecord? archived = await profileService.GetAsync(id, cancellationToken);
                    return archived is null ? Results.NotFound() : Results.Ok(archived);
                }));

        app.MapPost("/api/admin/seed", async (bool? reset, IVirtualLabManagementService managementService, CancellationToken cancellationToken) =>
            Results.Ok(await managementService.SeedLabAsync(reset ?? false, cancellationToken)));

        app.MapPost("/api/admin/reset", async (IVirtualLabManagementService managementService, CancellationToken cancellationToken) =>
            Results.Ok(await managementService.SeedLabAsync(true, cancellationToken)));

        return app;
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ManagementOperationException exception)
        {
            return Results.Json(
                new
                {
                    message = exception.Message,
                    errors = exception.Messages,
                },
                statusCode: exception.StatusCode);
        }
    }
}
