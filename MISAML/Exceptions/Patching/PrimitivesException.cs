using System;

namespace MISAML.Exceptions.Patching;

public class PrimitivesException : Exception
{
    public PrimitivesException()
    {
    }

    public PrimitivesException(string message) : base(message)
    {
    }

    public PrimitivesException(string message, Exception innerException) : base(message, innerException)
    {
    }
}