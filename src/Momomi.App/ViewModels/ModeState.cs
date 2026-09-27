namespace Momomi.App.ViewModels;

public static class ModeState
{
    public static string Current { get; private set; } = "rule";

    public static bool IsGlobal => string.Equals(Current, "global", StringComparison.OrdinalIgnoreCase);

    public static event EventHandler<string>? Changed;

    public static void Set(string mode)
    {
        if (string.Equals(Current, mode, StringComparison.OrdinalIgnoreCase)) return;
        Current = mode;
        Changed?.Invoke(null, mode);
    }
}
