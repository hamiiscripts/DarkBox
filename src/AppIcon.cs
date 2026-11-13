using System.Drawing;

// Loads the embedded app icon (falls back to a system icon).
public static class AppIcon
{
    private static Icon _cached;

    public static Icon Get()
    {
        if (_cached != null) return _cached;
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using (var stream = asm.GetManifestResourceStream("app.ico"))
            {
                if (stream != null) _cached = new Icon(stream);
            }
        }
        catch { }
        if (_cached == null) _cached = SystemIcons.Application;
        return _cached;
    }
}
