namespace PayFlow.SharedKernel.Ids;

/// <summary>Generates time-ordered UUIDv7 primary keys from the injected clock.</summary>
public sealed class IdGenerator(TimeProvider timeProvider)
{
    public Guid NewId() => Guid.CreateVersion7(timeProvider.GetUtcNow());
}
