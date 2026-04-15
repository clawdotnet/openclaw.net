using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface IUserProfileStore
{
    ValueTask<UserProfile?> GetProfileAsync(string actorId, CancellationToken ct);
    ValueTask<IReadOnlyList<UserProfile>> ListProfilesAsync(CancellationToken ct);
    ValueTask SaveProfileAsync(UserProfile profile, CancellationToken ct);
    ValueTask DeleteProfileAsync(string actorId, CancellationToken ct);
}
