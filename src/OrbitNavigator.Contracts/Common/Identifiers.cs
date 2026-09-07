namespace OrbitNavigator.Contracts.Common;

public readonly record struct ProfileId(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;
}

public readonly record struct BrowserSessionId(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;
}

public readonly record struct BrowserWindowId(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;
}

public readonly record struct BrowserTabId(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;
}

public readonly record struct RequestId(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;
}

public readonly record struct ResponseToken(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;
}

public readonly record struct DeviceId(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;
}

public readonly record struct OpaqueAuthHandle(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;
}

