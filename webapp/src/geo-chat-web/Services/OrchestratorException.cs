// Copyright (c) Microsoft. All rights reserved.

namespace GeoLocation.Web.Services;

/// <summary>
/// A request to the orchestrator that failed for a reason worth showing the person who asked.
/// </summary>
public sealed class OrchestratorException : Exception
{
    public OrchestratorException(string message)
        : base(message)
    {
    }

    public OrchestratorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
