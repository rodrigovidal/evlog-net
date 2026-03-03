namespace Evlog;

public delegate Task EvlogDrainDelegate(EvlogDrainContext context);

public sealed class EvlogDrainContext
{
    public required ReadOnlyMemory<byte> EventJson { get; init; }
    public required EvlogLevel Level { get; init; }
    public required int Status { get; init; }
}

public sealed class EvlogOptions
{
    private const string DefaultService = "app";
    private const string DefaultEnvironment = "production";
    private bool _serviceExplicit;
    private bool _versionExplicit;
    private bool _commitHashExplicit;
    private bool _regionExplicit;

    private string _service = DefaultService;

    public string Service
    {
        get => _service;
        set { _service = value; _serviceExplicit = true; }
    }

    public string Environment { get; set; } = DefaultEnvironment;
    public bool Pretty { get; set; }

    private string? _version;
    public string? Version
    {
        get => _version;
        set { _version = value; _versionExplicit = true; }
    }

    private string? _commitHash;
    public string? CommitHash
    {
        get => _commitHash;
        set { _commitHash = value; _commitHashExplicit = true; }
    }

    private string? _region;
    public string? Region
    {
        get => _region;
        set { _region = value; _regionExplicit = true; }
    }

    public SamplingOptions? Sampling { get; set; }
    public EvlogDrainDelegate? Drain { get; set; }

    public void ResolveFromEnvironment()
    {
        if (!_serviceExplicit)
            _service = Env("SERVICE_NAME") ?? DefaultService;

        var env = Env("ASPNETCORE_ENVIRONMENT") ?? Env("DOTNET_ENVIRONMENT");
        if (env is not null && Environment == DefaultEnvironment)
            Environment = env;

        if (!_versionExplicit)
            _version = Env("APP_VERSION");

        if (!_commitHashExplicit)
            _commitHash = Env("COMMIT_SHA") ?? Env("GITHUB_SHA");

        if (!_regionExplicit)
            _region = Env("REGION") ?? Env("FLY_REGION") ?? Env("AWS_REGION");
    }

    private static string? Env(string name)
    {
        var value = System.Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
