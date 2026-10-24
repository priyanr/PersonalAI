namespace PersonalAI.Core.Models;

// Thrown by services for a request that can't be served; ApiExceptionHandler turns each into its status code
// with a { message } body, so controllers don't map errors themselves.

/// <summary>400: the request itself is invalid (a missing field, a value out of range).</summary>
public sealed class InvalidRequestException(string message) : Exception(message);

/// <summary>404: the thing the request names doesn't exist.</summary>
public sealed class NotFoundException(string message) : Exception(message);

/// <summary>409: the request clashes with what is already stored (a duplicate name).</summary>
public sealed class ConflictException(string message) : Exception(message);

/// <summary>503: a service the request depends on (an AI provider) could not be reached.</summary>
public sealed class UpstreamUnavailableException(string message, Exception inner) : Exception(message, inner);
