namespace Covenant.Core;

/// <summary>The one configuration version a request is bound to (ADR-0011). Implemented by the compiled,
/// immutable runtime snapshot in Governance; the Host acquires it once at request entry and sets it on
/// <see cref="InferenceContext.Configuration"/>. Stages read ONLY this for the whole request, so no request
/// can mix old policy with new prices or new provider settings. When it is null (pure pipeline tests and
/// pre-v2 compositions), every stage keeps its constructor-injected legacy behaviour.</summary>
public interface IRequestConfiguration
{
    /// <summary>Configuration version (monotonic; the ETag is "v&lt;N&gt;").</summary>
    int Version { get; }

    /// <summary>The validated configuration this snapshot was compiled from. Contains no secret values.</summary>
    CovenantConfig Config { get; }

    ApplicationConfig? FindApplication(string applicationId);

    WorkloadProfileConfig? FindProfile(string profileId);

    ConnectionConfig? FindConnection(string connectionId);

    /// <summary>Model identity is connection-scoped (ModelRef); bare model names are not globally unique.</summary>
    ModelConfig? FindModel(string connectionId, string modelId);

    /// <summary>Snapshot-owned runtime resources (e.g. the provider clients compiled for this version, owned and
    /// disposed with the snapshot). Typed lookup only; null when the snapshot carries no resource of that type.</summary>
    T? GetResource<T>() where T : class;
}
