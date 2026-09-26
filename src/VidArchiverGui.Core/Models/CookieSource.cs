using CommunityToolkit.Mvvm.ComponentModel;

namespace VidArchiverGui.Core.Models;

public enum CookieSourceKind
{
    /// <summary>A Netscape-format cookies.txt file, passed with --cookies.</summary>
    File,
    /// <summary>A yt-dlp browser spec such as "firefox:work" or "chrome+gnomekeyring", passed with --cookies-from-browser.</summary>
    Browser,
}

/// <summary>A cookie source the user added by hand (installed browsers are detected and don't need one).</summary>
public partial class CookieSource : ObservableObject
{
    [ObservableProperty] private string _id = Guid.NewGuid().ToString("N");
    [ObservableProperty] private string _name = "Cookies";
    [ObservableProperty] private CookieSourceKind _kind = CookieSourceKind.File;

    /// <summary>File path for <see cref="CookieSourceKind.File"/>, browser spec for <see cref="CookieSourceKind.Browser"/>.</summary>
    [ObservableProperty] private string _value = "";

    public override string ToString() => Name;
}
