namespace Sakshi.Platform.Tests;

// Env vars are process-wide, so tests that set them must not run in parallel.
[CollectionDefinition("Environment", DisableParallelization = true)]
public class EnvironmentCollection;
