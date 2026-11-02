using System;

namespace MISAML.Exceptions.Patching;

public class BridgeNotFoundException : Exception
{
    public BridgeNotFoundException()
    {
    }
    
    public BridgeNotFoundException(string message) : base(message)
    {
    }
    
    public BridgeNotFoundException(string message, Exception innerException) : base(message, innerException)
    {
    }
}