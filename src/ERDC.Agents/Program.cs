using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ERDC.Agents.Services;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();
builder.Services.AddHttpClient<IMapImageService, AzureMapsService>();
builder.Services.AddHttpClient<IMapRouteService, AzureMapsRouteService>();
builder.Services.AddHttpClient<IGeocodeService, AzureMapsGeocodeService>();
builder.Services.AddHttpClient<IReverseGeocodeService, AzureMapsReverseGeocodeService>();
builder.Services.AddHttpClient<ITrafficIncidentService, AzureMapsTrafficService>();
builder.Services.AddHttpClient<IWeatherService, AzureMapsWeatherService>();
builder.Services.AddHttpClient<INwsAlertService, NwsAlertService>();
builder.Services.AddHttpClient<IElevationService, UsgsElevationService>();

builder.Services.AddSingleton(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();

    // Prefer managed identity against an explicit account URI; fall back to a
    // connection string for local development against Azurite.
    return configuration["Storage:MapImageServiceUri"] is { Length: > 0 } serviceUri
        ? new BlobServiceClient(new Uri(serviceUri), new DefaultAzureCredential())
        : new BlobServiceClient(configuration["AzureWebJobsStorage"] ?? "UseDevelopmentStorage=true");
});
builder.Services.AddSingleton<IMapImageStore, BlobMapImageStore>();

builder.Build().Run();