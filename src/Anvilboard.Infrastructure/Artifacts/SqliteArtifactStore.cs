using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Infrastructure.Artifacts;

/// <summary>
/// First-release <see cref="IArtifactStore"/> implementation: content is stored as a BLOB in the
/// same single SQLite file as the rest of the board (see <see cref="AnvilboardDbContext"/>), so
/// "one file, no external dependency" holds for artifacts too. Opaque references are random GUIDs;
/// callers must never construct or parse them.
/// </summary>
public sealed class SqliteArtifactStore(AnvilboardDbContext dbContext) : IArtifactStore
{
    public async Task<string> StoreAsync(byte[] content, string? contentType, CancellationToken ct = default)
    {
        var reference = Guid.NewGuid().ToString("N");
        dbContext.ArtifactBlobs.Add(new ArtifactBlob
        {
            Reference = reference,
            Bytes = content,
            ContentType = contentType,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync(ct);
        return reference;
    }

    public async Task<ArtifactContent?> RetrieveAsync(string reference, CancellationToken ct = default)
    {
        var blob = await dbContext.ArtifactBlobs.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Reference == reference, ct);
        return blob is null ? null : new ArtifactContent(blob.Bytes, blob.ContentType);
    }

    public async Task DeleteAsync(string reference, CancellationToken ct = default)
    {
        var blob = await dbContext.ArtifactBlobs.FirstOrDefaultAsync(b => b.Reference == reference, ct);
        if (blob is null)
        {
            return;
        }

        dbContext.ArtifactBlobs.Remove(blob);
        await dbContext.SaveChangesAsync(ct);
    }
}
