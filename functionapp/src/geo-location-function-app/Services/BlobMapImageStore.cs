using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using GeoLocation.Models;
using Microsoft.Extensions.Configuration;

namespace GeoLocation.Services;

public sealed class BlobMapImageStore : IMapImageStore
{
    private const string DefaultContainerName = "map-images";
    private const int DefaultLifetimeMinutes = 15;

    // Tolerates clock skew between this host and the storage service.
    private static readonly TimeSpan StartSkew = TimeSpan.FromMinutes(5);

    private readonly BlobServiceClient _blobServiceClient;
    private readonly string _containerName;
    private readonly TimeSpan _lifetime;
    private bool _containerVerified;

    public BlobMapImageStore(BlobServiceClient blobServiceClient, IConfiguration configuration)
    {
        _blobServiceClient = blobServiceClient;
        _containerName = configuration["Storage:MapImageContainer"] is { Length: > 0 } container
            ? container
            : DefaultContainerName;
        _lifetime = TimeSpan.FromMinutes(
            int.TryParse(configuration["Storage:MapImageUrlMinutes"], out var minutes) && minutes > 0
                ? minutes
                : DefaultLifetimeMinutes);
    }

    public async Task<StoredMapImage> StoreAsync(MapImage image, CancellationToken cancellationToken)
    {
        var container = _blobServiceClient.GetBlobContainerClient(_containerName);

        if (!_containerVerified)
        {
            await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
            _containerVerified = true;
        }

        var blob = container.GetBlobClient($"{DateTime.UtcNow:yyyy/MM/dd}/{Guid.NewGuid():n}.png");

        using var content = new MemoryStream(image.Content, writable: false);
        await blob.UploadAsync(
            content,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = image.ContentType }
            },
            cancellationToken);

        var expiresOn = DateTimeOffset.UtcNow.Add(_lifetime);
        return new StoredMapImage(await CreateReadUrlAsync(blob, expiresOn, cancellationToken), expiresOn);
    }

    private async Task<Uri> CreateReadUrlAsync(
        BlobClient blob,
        DateTimeOffset expiresOn,
        CancellationToken cancellationToken)
    {
        var builder = new BlobSasBuilder(BlobSasPermissions.Read, expiresOn)
        {
            BlobContainerName = blob.BlobContainerName,
            BlobName = blob.Name,
            Resource = "b",
            StartsOn = DateTimeOffset.UtcNow.Subtract(StartSkew)
        };

        // True only when the client holds an account key, such as Azurite locally.
        if (blob.CanGenerateSasUri)
        {
            return blob.GenerateSasUri(builder);
        }

        var delegationKey = await _blobServiceClient.GetUserDelegationKeyAsync(
            builder.StartsOn,
            expiresOn,
            cancellationToken);

        return new UriBuilder(blob.Uri)
        {
            Query = builder
                .ToSasQueryParameters(delegationKey.Value, _blobServiceClient.AccountName)
                .ToString()
        }.Uri;
    }
}
