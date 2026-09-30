using System.Text.Json;

namespace VidArchiverGui.Core.Services;

/// <summary>Header shared by the app's export files, so importing the wrong kind of file gives a clear message.</summary>
internal abstract class ExchangeFile
{
    public string Format { get; set; } = "";
    public int Version { get; set; }

    /// <summary>Parses an export file. Throws <see cref="FormatException"/> with a readable reason if it isn't <paramref name="what"/>.</summary>
    public static T Read<T>(string json, string format, int version, string what) where T : ExchangeFile
    {
        T? file;
        try
        {
            file = JsonSerializer.Deserialize<T>(json, SettingsStore.Options);
        }
        catch (JsonException e)
        {
            throw new FormatException("This file isn't valid JSON: " + e.Message, e);
        }

        if (file is null || file.Format != format)
        {
            throw new FormatException($"This file isn't {what} from Vid Archiver GUI.");
        }

        if (file.Version > version)
        {
            throw new FormatException("This file was exported by a newer version of the app. Update it to import the file.");
        }

        return file;
    }
}
