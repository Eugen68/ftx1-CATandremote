namespace Ftx1.Audio;
public static class AudioDiagnostic
{
    public static string Describe(Exception error)
    {
        var cause = error.GetBaseException();
        return $"{cause.GetType().Name} (0x{cause.HResult:X8}): {cause.Message}";
    }
}
