using System;

namespace MISAML.Exceptions;

public class BadModManifestException : Exception
{
    /// <summary>
    /// The exception that is thrown when a mod.json is malformed
    /// </summary>
    public BadModManifestException()
    {
    }

    /// <summary>
    /// The exception that is thrown when a mod.json is malformed
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public BadModManifestException(string message) : base(message)
    {
    }
    
    /// <summary>
    /// The exception that is thrown when a mod.json is malformed
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public BadModManifestException(string message, Exception innerException) : base(message, innerException)
    {
    }
}