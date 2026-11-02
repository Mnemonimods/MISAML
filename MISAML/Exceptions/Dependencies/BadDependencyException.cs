using System;
using MISAML.Exceptions.Manifests;

namespace MISAML.Exceptions.Dependencies;

public class BadDependencyException : Exception
{
    public BadDependencyException()
    {
    }
    
    public BadDependencyException(string message) : base(message)
    {
    }
    
    public BadDependencyException(string message, Exception innerException) : base(message, innerException)
    {
    }
}