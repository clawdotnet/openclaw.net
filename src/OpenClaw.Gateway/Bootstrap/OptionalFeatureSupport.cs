namespace OpenClaw.Gateway.Bootstrap;

internal static class OptionalFeatureSupport
{
    public static bool OpenSandboxEnabled
    {
        get
        {
#if OPENCLAW_ENABLE_OPENSANDBOX
            return true;
#else
            return false;
#endif
        }
    }
}
